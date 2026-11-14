namespace CartucheiraVideo.Api.PlaylistDashboard;

/// <summary>
/// Resultado de <see cref="PlaylistDashboardHost.Start"/> — mesmo racional honesto de
/// <see cref="LocalTriggerApiStartResult"/>: abrir a porta pode falhar por um motivo real fora do
/// controle deste programa (porta em uso, ou o Windows recusando a reserva da URL por falta de
/// privilégio — ver comentário de classe de <see cref="PlaylistDashboardHost"/>, que é um risco MAIOR
/// aqui do que no <c>LocalTriggerApiHost</c> porque esta porta precisa aceitar conexão de OUTRA
/// máquina da rede, não só <c>localhost</c>).
/// </summary>
public sealed class PlaylistDashboardStartResult
{
    private PlaylistDashboardStartResult(bool succeeded, int? port, string? errorMessage)
    {
        Succeeded = succeeded;
        Port = port;
        ErrorMessage = errorMessage;
    }

    public bool Succeeded { get; }

    public int? Port { get; }

    public string? ErrorMessage { get; }

    public static PlaylistDashboardStartResult Success(int port) => new(true, port, null);

    public static PlaylistDashboardStartResult Failure(string errorMessage) => new(false, null, errorMessage);
}
