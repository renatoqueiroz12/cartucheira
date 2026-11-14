namespace CartucheiraVideo.Core;

/// <summary>
/// Um Programa cadastrado na dashboard web de Playlist (ex.: "Jornal da Tarde") — Sprint
/// Playlist-Dashboard (2026-11-14). Cada Programa pode ter várias <see cref="PlaylistEdition"/>
/// (uma por Data), cada uma com sua própria pasta de ingest.
/// </summary>
public sealed class PlaylistProgram
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public required string Name { get; set; }
}
