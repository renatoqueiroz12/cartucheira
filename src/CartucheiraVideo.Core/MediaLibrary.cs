namespace CartucheiraVideo.Core;

/// <summary>
/// Coracao do Explorer: representa toda a biblioteca de midia conhecida pela aplicacao, nao so
/// pastas. O Explorer (Presentation) passa a consumir este objeto em vez de conhecer pastas e
/// arquivos diretamente — isso mantem a UI desacoplada de como a biblioteca e organizada por
/// baixo (hoje: sistema de arquivos local; no futuro, poderia incluir outras fontes).
///
/// Metade destas colecoes fica vazia nesta sprint (Favorites/RecentItems/SearchHistory/Playlists/
/// Collections nao tem nenhuma sprint ainda que as povoe de verdade) — nascem aqui mesmo assim
/// pra o objeto ja representar o conceito completo, evitando remodelar a classe depois.
/// </summary>
public sealed class MediaLibrary
{
    /// <summary>Arvore de pastas conhecida (tipicamente uma raiz por pasta escolhida pelo usuario).</summary>
    public IList<MediaFolder> Folders { get; init; } = new List<MediaFolder>();

    /// <summary>Todas as midias conhecidas pela biblioteca, independente de pasta.</summary>
    public IList<MediaItem> Media { get; init; } = new List<MediaItem>();

    public IList<MediaItem> Favorites { get; init; } = new List<MediaItem>();

    public IList<MediaItem> RecentItems { get; init; } = new List<MediaItem>();

    public IList<string> SearchHistory { get; init; } = new List<string>();

    public IList<Playlist> Playlists { get; init; } = new List<Playlist>();

    public IList<MediaCollection> Collections { get; init; } = new List<MediaCollection>();
}
