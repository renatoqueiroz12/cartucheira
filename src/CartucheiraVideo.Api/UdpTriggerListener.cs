using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace CartucheiraVideo.Api;

/// <summary>
/// Recebe comando externo de disparo de uma mesa de áudio real via UDP em <c>127.0.0.1</c> —
/// Sprint Comando Externo, correção pós-entrega (2026-08-10): destrava o protocolo da console de
/// áudio (arquitetura §13.4/§14.2), que até aqui ficava honesto/placeholder por falta de
/// documentação do fabricante. Protocolo confirmado pelo próprio usuário, com captura de tela da
/// tela de configuração da mesa (colunas "Trigger ON"/"Trigger OFF"/"URL"/"Port"): cada trilha da
/// mesa manda um texto curto (ex.: "ON"/"OFF", customizável por trilha) via UDP pra um IP:porta
/// configurável — a trilha usada por esta Cartucheira aponta pra <c>127.0.0.1:8888</c>, mesma porta
/// pras duas Cartucheiras (Vinhetas/Trilhas) — quem diferencia qual banco e se é "ligar"/"desligar"
/// é o TEXTO do pacote, comparado pelo dono deste listener (<c>MainViewModel</c>) contra os quatro
/// campos configuráveis em Configurações → Cartucheira (Ligar/Desligar por banco), não este projeto
/// — <see cref="UdpTriggerListener"/> só decodifica o pacote como texto e entrega pro callback,
/// sem saber nada de Cartucheira/Vinhetas/Trilhas (mesmo desacoplamento já usado em
/// <see cref="LocalTriggerApiHost"/>).
///
/// Sem autenticação de propósito, mesma decisão já tomada pro <see cref="LocalTriggerApiHost"/>
/// (rede local de estúdio, não exposta à internet) — o socket escuta SÓ em
/// <see cref="IPAddress.Loopback"/> (<c>127.0.0.1</c>), nunca <c>0.0.0.0</c>/<c>+</c>/<c>*</c>, então
/// nem a mesa em outra máquina da mesma rede local alcança este listener — só processos rodando no
/// mesmo computador (a mesa precisa estar configurada com o IP <c>127.0.0.1</c> — que é exatamente o
/// que a captura de tela do usuário mostrou).
///
/// UDP é "sem conexão, sem confirmação" por natureza — diferente do <see cref="LocalTriggerApiHost"/>
/// (HTTP, que responde 200/403/404/405 pra cada requisição), este listener nunca manda nenhuma
/// resposta de volta pra mesa (não existe conceito de resposta em UDP puro, e a mesa não pediu uma)
/// — um pacote perdido na rede simplesmente nunca chega, sem erro visível de nenhum lado. Fora do
/// controle deste programa.
/// </summary>
public sealed class UdpTriggerListener : IDisposable
{
    /// <summary>Porta padrão — confirmada pelo usuário na tela de configuração real da mesa (coluna "Port", 8888 em toda trilha mostrada).</summary>
    public const int DefaultPort = 8888;

    private readonly int _port;
    private UdpClient? _client;
    private CancellationTokenSource? _cts;
    private Task? _loopTask;
    private Action<string>? _onCommandReceived;
    private volatile bool _disposed;
    private volatile bool _started;

    public UdpTriggerListener(int port = DefaultPort)
    {
        _port = port;
    }

    public int Port => _port;

    /// <summary>
    /// Abre o socket UDP em <c>127.0.0.1:porta</c> e começa a escutar — chamado uma vez, na
    /// construção do <c>MainViewModel</c>. <paramref name="onCommandReceived"/> é chamado uma vez
    /// por pacote recebido, já decodificado como texto (nunca com pacote vazio — ver
    /// <see cref="DecodeText"/>), numa thread de fundo (nunca a UI thread — quem ouve precisa
    /// marchar pra <c>Dispatcher.Invoke</c> se for tocar em binding do WPF, mesmo contrato já usado
    /// por <see cref="LocalTriggerApiHost"/>/<c>Infrastructure.GlobalHotkeyService</c>). Nunca
    /// lança: falha ao abrir a porta vira <see cref="UdpTriggerStartResult.Failure"/>.
    /// </summary>
    public UdpTriggerStartResult Start(Action<string> onCommandReceived)
    {
        ArgumentNullException.ThrowIfNull(onCommandReceived);

        if (_disposed)
        {
            return UdpTriggerStartResult.Failure("o listener já foi encerrado.");
        }

        if (_started)
        {
            return UdpTriggerStartResult.Success(_port);
        }

        _onCommandReceived = onCommandReceived;

        try
        {
            _client = new UdpClient(new IPEndPoint(IPAddress.Loopback, _port));
        }
        catch (SocketException ex)
        {
            return UdpTriggerStartResult.Failure($"porta {_port} já está em uso por outro programa, ou inacessível ({ex.Message}).");
        }

        _started = true;
        _cts = new CancellationTokenSource();
        _loopTask = Task.Run(() => ListenLoopAsync(_client, _cts.Token));
        return UdpTriggerStartResult.Success(_port);
    }

    private async Task ListenLoopAsync(UdpClient client, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            UdpReceiveResult result;
            try
            {
                result = await client.ReceiveAsync(token).ConfigureAwait(false);
            }
            catch (Exception) when (token.IsCancellationRequested || _disposed)
            {
                return; // Dispose() já foi chamado — encerramento normal, não é erro.
            }
            catch (Exception)
            {
                // Erro processando UM pacote (ex.: pacote malformado) — não derruba o listener
                // inteiro por causa de um pacote ruim; segue pro próximo. Mesma defesa já usada em
                // LocalTriggerApiHost.ListenLoopAsync.
                continue;
            }

            var text = DecodeText(result.Buffer);
            var callback = _onCommandReceived;
            if (callback is not null && text.Length > 0)
            {
                _ = Task.Run(() => InvokeSafe(callback, text), CancellationToken.None);
            }
        }
    }

    private static void InvokeSafe(Action<string> callback, string text)
    {
        try
        {
            callback(text);
        }
        catch (Exception)
        {
            // Nunca deixa uma exceção no processamento de UM comando matar a thread que processa os
            // próximos pacotes — mesma classe de defesa já usada em
            // LocalTriggerApiHost.ProcessRequestSafe.
        }
    }

    /// <summary>
    /// UTF-8 aceita ASCII puro sem problema (é um subconjunto válido) — cobre tanto mesas que
    /// mandam só ASCII (o caso confirmado, "ON"/"OFF") quanto qualquer acentuação eventual. Trim
    /// tira espaço/quebra de linha/caractere nulo que algumas mesas anexam no fim do pacote UDP.
    /// </summary>
    private static string DecodeText(byte[] buffer) => Encoding.UTF8.GetString(buffer).Trim(' ', '\r', '\n', '\t', '\0');

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _cts?.Cancel();

        try
        {
            _client?.Close();
        }
        catch (Exception)
        {
            // já parado / nunca chegou a iniciar — sem problema.
        }

        try
        {
            _loopTask?.Wait(TimeSpan.FromSeconds(2));
        }
        catch (Exception)
        {
            // encerramento best-effort — não trava o Dispose por causa disso.
        }

        try
        {
            _client?.Dispose();
        }
        catch (Exception)
        {
            // idem.
        }

        _cts?.Dispose();
    }
}
