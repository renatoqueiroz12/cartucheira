namespace CartucheiraVideo.Core;

/// <summary>
/// Um agrupamento livre de midias (ex.: "Comerciais do cliente X", "Selecao da semana") — diferente
/// de uma <see cref="Playlist"/> por nao implicar ordem/sequencia de reproducao, so agrupamento.
/// Parte de <see cref="MediaLibrary"/>, ainda sem nenhuma sprint que a povoe de verdade.
/// </summary>
public sealed class MediaCollection
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public required string Name { get; set; }

    public IList<MediaItem> Items { get; init; } = new List<MediaItem>();
}
