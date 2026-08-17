namespace CartucheiraVideo.Core;

/// <summary>
/// Uma sequencia nomeada de midias. Parte de <see cref="MediaLibrary"/> — hoje sem nenhuma sprint
/// que a povoe ou reproduza de verdade, mas ja modelada pra o Explorer poder tratar "playlist" como
/// um conceito de primeira classe assim que essa funcionalidade entrar no roadmap.
/// </summary>
public sealed class Playlist
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public required string Name { get; set; }

    public IList<MediaItem> Items { get; init; } = new List<MediaItem>();
}
