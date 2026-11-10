using System.Net;
using System.Net.Sockets;

namespace CartucheiraVideo.Infrastructure.Livewire;

/// <summary>
/// Sprint Livewire-2 (2026-09-13): transmissor real de um canal Livewire — porta de
/// <c>LivewireSender.h</c> (Mesa Console, referência C++/JUCE cedida pelo usuário) pra
/// <c>System.Net.Sockets</c> puro, reaplicando as lições confirmadas por teste real contra hardware
/// Axia (ver <c>arquitetura/livewire-plano.md</c> no projeto):
/// <list type="bullet">
/// <item><b>Bind na porta efêmera (0), nunca na porta multicast fixa (5004)</b> — quem precisa
/// escutar em 5004 é o RECEPTOR (sprint futura, Livewire-6); um transmissor puro não precisa
/// receber nada de volta.</item>
/// <item><b>Seleção explícita da placa de saída via <see cref="SocketOptionName.MulticastInterface"/></b>
/// (equivalente .NET do <c>IP_MULTICAST_IF</c> usado no C++) — numa máquina com várias placas de
/// rede, deixar o Windows escolher pela tabela de rotas pode silenciosamente sair pela interface
/// errada: sem erro nenhum, o pacote "é enviado" e ninguém na rede Livewire recebe.</item>
/// <item><b>Loopback de multicast ligado</b> — necessário pra testar transmissão e recepção na mesma
/// máquina.</item>
/// </list>
///
/// Não decide QUANDO nem DE ONDE vem o áudio — só recebe blocos já prontos de exatamente
/// <see cref="LivewireRtpFraming.FramesPerPacket"/> quadros (48kHz estéreo float32, -1..1) via
/// <see cref="SendFrame"/> e os transmite como um pacote RTP/L24. Quem decide a fonte e o ritmo de
/// entrega (a cada 5ms) é o chamador — no caso desta sprint,
/// <c>CartucheiraVideo.Engine.AudioEngine</c> (ligado via <c>Presentation.AudioBankViewModel</c>, já
/// que Engine e Infrastructure não podem se referenciar diretamente — regra permanente do projeto,
/// ver <c>arquitetura-sistema.md</c>).
///
/// Falha honesta: qualquer erro de socket (placa desconectada, endereço multicast negado por
/// firewall, etc.) fica em <see cref="LastError"/> e <see cref="SendFrame"/> simplesmente não envia
/// aquele bloco — nunca lança exceção pra dentro da thread de áudio que chama.
/// </summary>
public sealed class LivewireSender : IDisposable
{
    private readonly IPEndPoint _multicastEndpoint;
    private readonly Random _random = new();
    private Socket? _socket;
    private ushort _sequence;
    private uint _timestamp;
    private uint _ssrc;
    private bool _disposed;

    /// <summary>Cria um transmissor pro canal Livewire indicado — não abre o socket ainda, ver <see cref="Start"/>.</summary>
    public LivewireSender(int channel)
    {
        if (channel <= 0 || channel > 32767)
        {
            throw new ArgumentOutOfRangeException(nameof(channel), "Canal Livewire precisa estar entre 1 e 32767.");
        }

        Channel = channel;
        _multicastEndpoint = new IPEndPoint(IPAddress.Parse(LivewireRtpFraming.GetMulticastAddress(channel)), LivewireRtpFraming.MulticastPort);
    }

    public int Channel { get; }

    /// <summary>Tipo de carga RTP anunciado — ajustável em runtime (a Axia pode recusar um valor que não reconheça), padrão 96 (ver <see cref="LivewireRtpFraming.DefaultPayloadType"/>).</summary>
    public byte PayloadType { get; set; } = LivewireRtpFraming.DefaultPayloadType;

    /// <summary>Quantos pacotes já foram enviados com sucesso desde <see cref="Start"/> — só diagnóstico, nunca usado pra lógica.</summary>
    public long PacketsSent { get; private set; }

    /// <summary>Motivo do último erro (abrir socket ou enviar um pacote) — nulo enquanto tudo deu certo. Nunca lança.</summary>
    public string? LastError { get; private set; }

    /// <summary>
    /// Abre o socket de envio: bind na porta efêmera (nunca a 5004), seleção da placa de saída (se
    /// <paramref name="localInterfaceIp"/> for informado) e loopback de multicast ligado. Devolve
    /// falso (com o motivo em <see cref="LastError"/>) em caso de falha — nunca lança.
    /// </summary>
    public bool Start(string? localInterfaceIp)
    {
        Stop();

        try
        {
            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);

            // Bind só na porta (0 = efêmera, escolhida pelo sistema) — nunca num IP especifico e
            // nunca na porta fixa do Livewire (5004, papel do RECEPTOR). Ver comentário de classe.
            socket.Bind(new IPEndPoint(IPAddress.Any, 0));

            if (!string.IsNullOrWhiteSpace(localInterfaceIp) && IPAddress.TryParse(localInterfaceIp, out var interfaceAddress))
            {
                // Equivalente .NET do IP_MULTICAST_IF do C++ original — decide por qual placa o
                // multicast SAI. Sem isto, numa máquina com várias placas, o Windows escolhe pela
                // tabela de rotas e pode escolher uma que não chega na rede Livewire, sem erro nenhum.
                socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, interfaceAddress.GetAddressBytes());
            }

            socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastLoopback, true);

            _sequence = (ushort)_random.Next(0, 65536);
            _timestamp = (uint)_random.Next();
            _ssrc = (uint)_random.Next();
            PacketsSent = 0;
            LastError = null;
            _socket = socket;
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
    /// Monta e envia um pacote RTP/L24 com exatamente <see cref="LivewireRtpFraming.FramesPerPacket"/>
    /// quadros. Sem efeito (nenhuma exceção) se <see cref="Start"/> não foi chamado/teve sucesso
    /// ainda, ou se os arrays não tiverem o tamanho esperado.
    /// </summary>
    public void SendFrame(float[] left, float[] right)
    {
        var socket = _socket;
        if (socket is null)
        {
            return;
        }

        if (left.Length != LivewireRtpFraming.FramesPerPacket || right.Length != LivewireRtpFraming.FramesPerPacket)
        {
            LastError = $"bloco de tamanho inesperado (esperado {LivewireRtpFraming.FramesPerPacket} quadros)";
            return;
        }

        Span<byte> packet = stackalloc byte[LivewireRtpFraming.RtpHeaderSize + LivewireRtpFraming.FramesPerPacket * LivewireRtpFraming.BytesPerFrame];

        LivewireRtpFraming.WriteHeader(packet, PayloadType, _sequence, _timestamp, _ssrc);
        unchecked
        {
            _sequence++;
        }
        _timestamp += LivewireRtpFraming.FramesPerPacket;

        var payload = packet.Slice(LivewireRtpFraming.RtpHeaderSize);
        for (var i = 0; i < LivewireRtpFraming.FramesPerPacket; i++)
        {
            var frame = payload.Slice(i * LivewireRtpFraming.BytesPerFrame, LivewireRtpFraming.BytesPerFrame);
            LivewireRtpFraming.WriteSample24(frame.Slice(0, 3), left[i]);
            LivewireRtpFraming.WriteSample24(frame.Slice(3, 3), right[i]);
        }

        try
        {
            socket.SendTo(packet, _multicastEndpoint);
            PacketsSent++;
        }
        catch (Exception ex)
        {
            // Falha honesta — nunca deve derrubar a thread de áudio que chama isto. Ver comentário de
            // classe.
            LastError = ex.Message;
        }
    }

    /// <summary>Fecha o socket, se houver. Seguro chamar mais de uma vez, ou antes de qualquer <see cref="Start"/>.</summary>
    public void Stop()
    {
        var socket = _socket;
        _socket = null;
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
}
