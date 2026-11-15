namespace CartucheiraVideo.Core;

/// <summary>
/// Status do ingest do VT (vídeo) de uma <see cref="PlaylistLauda"/> — Sprint Playlist-Dashboard
/// (renomeado de <c>PlaylistEntryStatus</c> na Sprint 2, 2026-11-15, junto com
/// <c>PlaylistEntry</c> → <see cref="PlaylistLauda"/>). Importante: isto rastreia só o VT, não a
/// Lauda inteira — uma Lauda sem nenhum VT (ex.: Entrevista ao vivo, Passagem de apresentador) fica
/// em <see cref="PendingIngest"/> indefinidamente, e isso é normal, não um erro. Deliberadamente
/// maior que o uso de hoje (que só sabe diferenciar "sem arquivo" de "arquivo recebido") — evita
/// reabrir o enum quando a validação real de ingest (LibVLC) entrar numa sprint futura.
/// </summary>
public enum PlaylistLaudaStatus
{
    Unknown = 0,
    PendingIngest,
    Validating,
    Ready,
    Error
}
