namespace CartucheiraVideo.Api;

/// <summary>
/// Resultado de <see cref="UdpTriggerSender.Send"/> — mesmo padrão honesto de
/// <see cref="UdpTriggerStartResult"/>/<see cref="LocalTriggerApiStartResult"/>: mandar um pacote
/// UDP pode falhar por um motivo real fora do controle deste programa (host inválido, rede
/// inacessível) — quem chama precisa saber pra avisar o operador em vez de fingir que o comando
/// chegou. UDP em si nunca confirma ENTREGA (isso nenhum resultado aqui garante) — só cobre o que
/// dá pra saber localmente: se o pacote pôde ser colocado na rede ou não.
/// </summary>
public sealed class UdpTriggerSendResult
{
    private UdpTriggerSendResult(bool succeeded, string? errorMessage)
    {
        Succeeded = succeeded;
        ErrorMessage = errorMessage;
    }

    public bool Succeeded { get; }

    public string? ErrorMessage { get; }

    public static UdpTriggerSendResult Success() => new(true, null);

    public static UdpTriggerSendResult Failure(string errorMessage) => new(false, errorMessage);
}
