namespace CartucheiraVideo.Api;

/// <summary>
/// Resultado de <see cref="UdpTriggerListener.Start"/> — mesmo padrão honesto de
/// <see cref="LocalTriggerApiStartResult"/>/<c>CartucheiraVideo.Infrastructure.HotkeyRegistrationResult</c>:
/// abrir a porta UDP local pode falhar por um motivo real fora do controle deste programa (porta já
/// em uso por outro programa) — quem chama precisa saber pra avisar o operador em vez de fingir que
/// o comando da mesa vai chegar.
/// </summary>
public sealed class UdpTriggerStartResult
{
    private UdpTriggerStartResult(bool succeeded, int? port, string? errorMessage)
    {
        Succeeded = succeeded;
        Port = port;
        ErrorMessage = errorMessage;
    }

    public bool Succeeded { get; }

    public int? Port { get; }

    public string? ErrorMessage { get; }

    public static UdpTriggerStartResult Success(int port) => new(true, port, null);

    public static UdpTriggerStartResult Failure(string errorMessage) => new(false, null, errorMessage);
}
