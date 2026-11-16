namespace CartucheiraVideo.Api.PlaylistDashboard;

/// <summary>
/// Resultado de uma tentativa de login (ver <see cref="PlaylistDashboardHost.PlaylistLoginHandler"/>)
/// — Sprint 5 (2026-11-16). Separa a RESPOSTA HTTP a devolver (<see cref="Response"/>, igual a
/// qualquer outra rota) de QUEM autenticou (<see cref="AuthenticatedUser"/>, nulo se a tentativa
/// falhou) — só quando não é nulo o <see cref="PlaylistDashboardHost"/> cria uma sessão nova e
/// manda o cookie de sessão pro navegador; quem verifica usuário/senha (Infrastructure.Playlist.
/// UserStore.VerifyCredentials, via a Presentation) nunca precisa saber nada sobre cookie/sessão.
/// </summary>
public sealed record PlaylistLoginAttemptResult(PlaylistDashboardResponse Response, AuthenticatedDashboardUser? AuthenticatedUser)
{
    public static PlaylistLoginAttemptResult Failure(PlaylistDashboardResponse response) => new(response, null);

    public static PlaylistLoginAttemptResult Success(PlaylistDashboardResponse response, AuthenticatedDashboardUser user) => new(response, user);
}
