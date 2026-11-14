namespace CartucheiraVideo.Core;

/// <summary>
/// Status do ingest de um item (vídeo) dentro de uma <see cref="PlaylistEdition"/> — Sprint
/// Playlist-Dashboard (2026-11-14, ver arquitetura/playlist-modulo-roteiro.md). Deliberadamente
/// maior que o uso do Sprint 1 (que só cria itens em <see cref="PendingIngest"/>, sem nenhum upload
/// real ainda) — evita reabrir o enum na Sprint 2 (upload + validação via LibVLC).
/// </summary>
public enum PlaylistEntryStatus
{
    Unknown = 0,
    PendingIngest,
    Validating,
    Ready,
    Error
}
