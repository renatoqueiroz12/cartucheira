namespace CartucheiraVideo.Configuration;

/// <summary>
/// Configuração do módulo Playlist (dashboard web pros produtores) — Sprint Playlist-Dashboard
/// (2026-11-14, ver arquitetura/playlist-modulo-roteiro.md). <see cref="RootFolder"/> nulo/vazio
/// significa "módulo desligado" — a Cartucheira só sobe o servidor da dashboard depois do usuário
/// configurar uma pasta raiz real em Configurações → Playlist.
/// </summary>
public sealed class PlaylistSettingsSnapshot
{
    /// <summary>Pasta raiz onde cada Programa/Data ganha sua própria subpasta de ingest — nula até o usuário configurar (módulo fica desligado até lá).</summary>
    public string? RootFolder { get; set; }

    /// <summary>
    /// Porta TCP onde a dashboard escuta — diferente de <see cref="AppSettingsSnapshot.UdpTriggerPort"/>
    /// (UDP, mesa de áudio) e da porta fixa 9595 do <c>LocalTriggerApiHost</c> (só <c>localhost</c>):
    /// esta porta precisa ser alcançável por OUTROS computadores da rede local (os produtores), não
    /// só o próprio PC da Cartucheira — ver
    /// <c>CartucheiraVideo.Api.PlaylistDashboard.PlaylistDashboardHost</c>.
    /// </summary>
    public int DashboardPort { get; set; } = 7077;
}
