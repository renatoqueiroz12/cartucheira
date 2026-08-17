using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;

namespace CartucheiraVideo.Api;

/// <summary>
/// Manda comando externo por UDP pra um IP:porta configurável — Sprint Comando Externo Deck→Mesa
/// (2026-08-13). Direção CONTRÁRIA do <see cref="UdpTriggerListener"/> (que recebe): este projeto
/// AVISA a mesa de áudio/console externo quando um Deck dá Play ou termina (fim natural ou Stop
/// manual, os dois convergem no mesmo texto — arquitetura §15), mandando um texto curto UTF-8, sem
/// esperar resposta nenhuma (UDP não tem conceito de confirmação — mesma natureza já documentada em
/// <see cref="UdpTriggerListener"/>, só que na ponta de saída).
///
/// Diferente do <see cref="UdpTriggerListener"/> (que só escuta em <c>127.0.0.1</c>, decisão de
/// segurança pra rede local de estúdio), este remetente manda pra QUALQUER host:porta configurado
/// pelo operador — o destino é decidido em Configurações → Deck (IP:porta independente por Deck,
/// decisão explícita do usuário: "IP:porta por Deck", já que a mesa que ele está construindo pode
/// crescer e cada Deck pode precisar apontar pra um lugar diferente).
///
/// O envio de verdade roda numa <see cref="Task.Run"/> (nunca na thread de quem chamou
/// <see cref="Send"/>) — prioridade Latência: dar Play/Stop num Deck NUNCA pode esperar rede (um
/// host mal configurado, por exemplo, pode exigir resolução de DNS antes de falhar, o que pode
/// demorar). <paramref name="onCompleted"/> é chamado numa thread de fundo, nunca a UI — quem
/// registrar o callback precisa marchar pra <c>Dispatcher.Invoke</c> se for tocar em binding do
/// WPF, mesmo contrato já usado pelo lado de escuta.
/// </summary>
public sealed class UdpTriggerSender : IDisposable
{
    private readonly UdpClient _client = new();
    private volatile bool _disposed;

    /// <summary>Manda <paramref name="text"/> (UTF-8) pra <paramref name="host"/>:<paramref name="port"/>, numa thread de fundo. Nunca lança: falha vira <see cref="UdpTriggerSendResult.Failure"/>, entregue via <paramref name="onCompleted"/> (também numa thread de fundo).</summary>
    public void Send(string host, int port, string text, Action<UdpTriggerSendResult> onCompleted)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(onCompleted);

        if (_disposed)
        {
            onCompleted(UdpTriggerSendResult.Failure("o remetente já foi encerrado."));
            return;
        }

        var client = _client;
        _ = Task.Run(() =>
        {
            try
            {
                var bytes = Encoding.UTF8.GetBytes(text);
                client.Send(bytes, bytes.Length, host, port);
                InvokeSafe(onCompleted, UdpTriggerSendResult.Success());
            }
            catch (Exception ex) when (ex is SocketException or ArgumentException or FormatException)
            {
                InvokeSafe(onCompleted, UdpTriggerSendResult.Failure($"não foi possível enviar pra {host}:{port} ({ex.Message})."));
            }
            catch (ObjectDisposedException)
            {
                InvokeSafe(onCompleted, UdpTriggerSendResult.Failure("o remetente já foi encerrado."));
            }
        });
    }

    private static void InvokeSafe(Action<UdpTriggerSendResult> callback, UdpTriggerSendResult result)
    {
        try
        {
            callback(result);
        }
        catch (Exception)
        {
            // Nunca deixa uma exceção no processamento de UM resultado matar a thread de fundo —
            // mesma defesa já usada em UdpTriggerListener.InvokeSafe/LocalTriggerApiHost.ProcessRequestSafe.
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        try
        {
            _client.Dispose();
        }
        catch (Exception)
        {
            // já encerrado — sem problema.
        }
    }
}
