namespace CartucheiraVideo.Api;

/// <summary>
/// Resultado de <see cref="LocalTriggerApiHost.Start"/> — mesmo racional honesto de
/// <c>CartucheiraVideo.Infrastructure.HotkeyRegistrationResult</c>: abrir uma porta local pode falhar
/// por um motivo real fora do controle deste programa (porta já em uso, ou — no Windows — o processo
/// sem permissão pra reservar a URL, ver comentário de <see cref="LocalTriggerApiHost"/>) — quem
/// chama precisa saber pra avisar o operador em vez de fingir que o Stream Deck/Companion vai
/// funcionar.
/// </summary>
public sealed class LocalTriggerApiStartResult
{
    private LocalTriggerApiStartResult(bool succeeded, int? port, string? errorMessage)
    {
        Succeeded = succeeded;
        Port = port;
        ErrorMessage = errorMessage;
    }

    public bool Succeeded { get; }

    public int? Port { get; }

    public string? ErrorMessage { get; }

    public static LocalTriggerApiStartResult Success(int port) => new(true, port, null);

    public static LocalTriggerApiStartResult Failure(string errorMessage) => new(false, null, errorMessage);
}
