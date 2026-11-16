namespace CartucheiraVideo.Api.PlaylistDashboard;

/// <summary>
/// Identidade resolvida de uma sessão válida da dashboard — Sprint 5 (2026-11-16, login/permissão
/// por Programa). Vocabulário HTTP/sessão puro (strings/bool/lista), sem nenhum tipo de domínio —
/// mesmo princípio de isolamento de <see cref="PlaylistDashboardResponse"/>: quem traduz isto pra/da
/// representação em disco (<c>Infrastructure.Playlist.UserStore</c>/<c>DashboardUserRecord</c>) é a
/// Presentation (<c>PlaylistDashboardCoordinator</c>), nunca este projeto.
/// </summary>
public sealed record AuthenticatedDashboardUser(string Id, string Username, bool IsAdmin, IReadOnlyList<string> AllowedProgramIds);
