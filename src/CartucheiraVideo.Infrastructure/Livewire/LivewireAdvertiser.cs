using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;

namespace CartucheiraVideo.Infrastructure.Livewire;

/// <summary>
/// Sprint Livewire-3 (2026-09-13, reordenada na frente do Deck a pedido do usuário): anuncia as
/// Saídas Livewire configuradas na rede — porta de <c>LivewireAdvertiser.h</c> (projeto "Mesa
/// Console", referência C++/JUCE cedida pelo usuário), só a parte de ENVIAR (ADVT1 + ADVT2 + ADVT3)
/// pro grupo de anúncio. A leitura da porta de controle (WRIN/BUSY em si, o mecanismo que faz o
/// ADVT3 refletir uma reserva de verdade em vez de sempre uso zerado) fica pra uma sprint futura
/// (Livewire-5) — ver <c>arquitetura/livewire-plano.md</c>.
///
/// Por que esta sprint existe: a Sprint Livewire-2 (v1.0.19) só construiu a TRANSMISSÃO de áudio
/// (RTP/L24) de uma Saída Livewire selecionada numa Cartucheira — nada nela faz um console de
/// verdade "descobrir" essa fonte sozinho. O usuário confirmou, testando contra a Mesa Virtual dele,
/// exatamente esse sintoma: o canal configurado não aparecia na lista de fontes, mesmo antes de
/// tentar tocar qualquer áudio. Um console Livewire descobre fontes por ANÚNCIO (ADVT1, a lista
/// completa; ADVT2, um sinal de vida mais curto; ADVT3, o estado de uso/BUSY), mandado por multicast
/// 1x por segundo, independente de qualquer áudio estar fluindo — exatamente o que esta classe faz.
///
/// Fix pós-entrega (mesmo dia): a primeira versão desta classe mandava só ADVT1+ADVT2, apostando que
/// o ADVT3 só importava pra reserva (BUSY), ainda fora de escopo. Só que a referência NUNCA manda
/// ADVT1/ADVT2 sem o ADVT3 — os três sempre saem juntos, todo segundo, mesmo com uso zerado (ver
/// <c>Loop</c>) — e o usuário confirmou que faltar o terceiro deixava o anúncio incompleto pro
/// parser real da Mesa Console/Mesa Virtual, que nunca chegava a listar a fonte.
///
/// Diferença importante em relação a <see cref="LivewireSender"/>: o anúncio não depende de nenhuma
/// Cartucheira/Deck ter essa Saída selecionada — TODA Saída Livewire configurada em Configurações é
/// anunciada, assim que existir, mesmo que nunca seja usada em nenhum Deck/Cartucheira ainda (mesmo
/// comportamento de um nó Axia/Telos real: ele anuncia o que TEM provisionado, não só o que está em
/// uso agora). Por isso este anunciador é uma instância ÚNICA por processo (dono: MainViewModel),
/// não uma por banco como o <see cref="LivewireSender"/>.
///
/// Mesmas lições de rede da Mesa Console já aplicadas em <see cref="LivewireSender"/> (bind na porta
/// efêmera, seleção explícita da placa de saída via <see cref="SocketOptionName.MulticastInterface"/>,
/// loopback de multicast ligado) — ver o comentário completo lá.
/// </summary>
public sealed class LivewireAdvertiser : IDisposable
{
    /// <summary>Grupo/porta de anúncio Livewire — fixos pelo protocolo (capturados contra hardware Axia real, não publicados pelo fabricante), nunca configuráveis.</summary>
    private const string AnnounceGroup = "239.192.255.3";
    private const int AnnouncePort = 4001;

    /// <summary>Porta de controle (UDPC) anunciada — só o valor publicado no ADVT1/ADVT2 por enquanto; ninguém escuta nela ainda (Livewire-5, ver comentário de classe).</summary>
    private const int ControlPort = 4002;

    /// <summary>1x por segundo — mesmo ritmo do driver Axia (confirmado por teste real na Mesa Console): mais rápido só gera tráfego à toa; mais devagar e o console pode dar a fonte por desligada.</summary>
    private const int AnnounceIntervalMs = 1000;

    /// <summary>Fatiado em passos de 100ms (like o original) só pra <see cref="Stop"/> reagir rápido, sem esperar o segundo inteiro.</summary>
    private const int SleepStepMs = 100;

    private readonly object _gate = new();
    private readonly Random _random = new();

    private Socket? _socket;
    private Thread? _thread;
    private CancellationTokenSource? _cts;
    private LivewireIdentity _identity = new();
    private IReadOnlyList<LivewireSourceAnnouncement> _sources = Array.Empty<LivewireSourceAnnouncement>();
    private uint _sequence;
    private bool _disposed;

    /// <summary>Quantos datagramas (ADVT1+ADVT2+ADVT3 juntos, desde o fix pós-v1.0.20) já foram enviados com sucesso desde o último <see cref="Start"/> — só diagnóstico.</summary>
    public long AnnouncementsSent { get; private set; }

    /// <summary>Motivo do último erro (abrir socket ou enviar um datagrama) — nulo enquanto tudo deu certo. Nunca lança.</summary>
    public string? LastError { get; private set; }

    /// <summary>HWID calculado a partir do nome da máquina no último <see cref="Start"/> — só diagnóstico (ex.: exibir em Configurações no futuro).</summary>
    public ushort Hwid { get; private set; }

    /// <summary>
    /// Abre o socket de anúncio e sobe a thread dedicada. <paramref name="machineName"/> vira o campo
    /// ATRN do anúncio e a base do HWID (<see cref="LivewireAnnouncementCodec.ComputeHwidFromText"/>,
    /// nunca derivado do IP — ver <see cref="LivewireIdentity"/>). <paramref name="localInterfaceIp"/>
    /// nulo deixa o sistema escolher a placa de SAÍDA do socket (mesmo aviso de risco de
    /// <see cref="LivewireSender.Start"/>) — mas o campo INIP do anúncio sempre precisa de um IPv4
    /// concreto, então quando nulo esta classe resolve um automaticamente (<see cref="ResolveLocalIp"/>).
    /// Devolve falso (com o motivo em <see cref="LastError"/>) em caso de falha — nunca lança.
    /// </summary>
    public bool Start(string machineName, string? localInterfaceIp)
    {
        Stop();

        try
        {
            var resolvedIp = string.IsNullOrWhiteSpace(localInterfaceIp)
                ? ResolveLocalIp()
                : localInterfaceIp;

            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.Bind(new IPEndPoint(IPAddress.Any, 0));

            if (!string.IsNullOrWhiteSpace(localInterfaceIp) && IPAddress.TryParse(localInterfaceIp, out var interfaceAddress))
            {
                socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, interfaceAddress.GetAddressBytes());
            }

            socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastLoopback, true);

            var hwid = LivewireAnnouncementCodec.ComputeHwidFromText(machineName + "/Cartucheira");

            _identity = new LivewireIdentity
            {
                MachineName = machineName,
                Ip = IpTextToUInt32(resolvedIp),
                Udpc = ControlPort,
                Hwid = hwid,
            };
            Hwid = hwid;

            _sequence = (uint)_random.Next();
            AnnouncementsSent = 0;
            LastError = null;

            var cts = new CancellationTokenSource();
            var thread = new Thread(() => Loop(cts.Token))
            {
                IsBackground = true,
                Name = "LivewireAdvertiser",
            };

            _socket = socket;
            _cts = cts;
            _thread = thread;
            thread.Start();

            return true;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            _socket = null;
            return false;
        }
    }

    /// <summary>
    /// Substitui a lista de Saídas Livewire anunciadas — chamado sempre que Configurações é salvo
    /// (criar/remover/renomear/trocar canal de uma Saída). Não precisa reabrir socket nem reiniciar a
    /// thread: o próximo ciclo do <see cref="Loop"/> já usa a lista nova. Lista vazia = nenhum
    /// datagrama é enviado até a próxima Saída ser criada (mesmo comportamento do original: "só
    /// anuncia se tiver o que anunciar").
    /// </summary>
    public void UpdateSources(IReadOnlyList<LivewireSourceAnnouncement> sources)
    {
        lock (_gate)
        {
            _sources = sources;
        }
    }

    private void Loop(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            IReadOnlyList<LivewireSourceAnnouncement> sources;
            lock (_gate)
            {
                sources = _sources;
            }

            if (sources.Count > 0)
            {
                // Fix pós-v1.0.20 (2026-09-13): o original (LivewireAdvertiser.h) NUNCA manda ADVT1/
                // ADVT2 sem o ADVT3 — os três sempre saem juntos, todo segundo, mesmo com uso zerado
                // (nenhuma fonte foi reservada ainda). A v1.0.20 mandava só ADVT1+ADVT2, apostando que
                // ADVT3 só importava pra reserva (BUSY) — na prática isso deixou o anúncio incompleto
                // pro parser real (Mesa Console/Mesa Virtual do usuário), que nunca listou a fonte.
                // `Usage` de cada `LivewireSourceAnnouncement` já nasce zerado (ver construtor) e
                // continua assim até a Livewire-5 implementar a leitura de WRIN pela porta de
                // controle — exatamente o valor que a referência manda antes de qualquer reserva.
                Send(LivewireAnnouncementCodec.BuildSourceList(_identity, sources, ++_sequence));
                Send(LivewireAnnouncementCodec.BuildHeartbeat(_identity, sources.Count, ++_sequence));
                Send(LivewireAnnouncementCodec.BuildUsage(_identity, sources, ++_sequence));
            }

            for (var i = 0; i < AnnounceIntervalMs / SleepStepMs && !token.IsCancellationRequested; i++)
            {
                Thread.Sleep(SleepStepMs);
            }
        }
    }

    private void Send(byte[] packet)
    {
        var socket = _socket;
        if (socket is null)
        {
            return;
        }

        try
        {
            socket.SendTo(packet, new IPEndPoint(IPAddress.Parse(AnnounceGroup), AnnouncePort));
            AnnouncementsSent++;
        }
        catch (Exception ex)
        {
            // Falha honesta — nunca deve derrubar a thread de anúncio. Mesmo padrão de LivewireSender.
            LastError = ex.Message;
        }
    }

    /// <summary>Fecha o socket e espera a thread terminar sozinha (nunca <c>Abort</c>). Seguro chamar mais de uma vez, ou antes de qualquer <see cref="Start"/>.</summary>
    public void Stop()
    {
        var cts = _cts;
        var thread = _thread;
        var socket = _socket;

        _cts = null;
        _thread = null;
        _socket = null;

        cts?.Cancel();
        thread?.Join(500);
        cts?.Dispose();
        socket?.Dispose();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Stop();
    }

    /// <summary>
    /// Converte um IPv4 em texto (ex.: "192.168.2.116") pro inteiro big-endian de 32 bits que o campo
    /// INIP do anúncio espera — ver <see cref="LivewireIdentity.Ip"/>.
    /// </summary>
    private static uint IpTextToUInt32(string ipText)
    {
        if (!IPAddress.TryParse(ipText, out var address))
        {
            return 0;
        }

        var bytes = address.GetAddressBytes();
        return ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) | ((uint)bytes[2] << 8) | bytes[3];
    }

    /// <summary>
    /// Escolhe um IPv4 local pra preencher o campo INIP quando o usuário deixou "o sistema escolher"
    /// em Configurações → Saídas Livewire — o anúncio PRECISA de um IP concreto no pacote mesmo nesse
    /// caso (diferente do socket de saída em si, que aí realmente fica por conta da tabela de rotas
    /// do Windows). Primeira placa IPv4 ativa e não-loopback encontrada; <c>0.0.0.0</c> (nunca
    /// lança) se nenhuma for encontrada — honesto, mas não deveria acontecer numa máquina com rede.
    /// </summary>
    private static string ResolveLocalIp()
    {
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                {
                    continue;
                }

                foreach (var addr in nic.GetIPProperties().UnicastAddresses)
                {
                    if (addr.Address.AddressFamily == AddressFamily.InterNetwork)
                    {
                        return addr.Address.ToString();
                    }
                }
            }
        }
        catch (Exception)
        {
            // Falha honesta ao enumerar placas — cai no 0.0.0.0 abaixo, nunca lança.
        }

        return "0.0.0.0";
    }
}
