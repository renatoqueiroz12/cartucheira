namespace CartucheiraVideo.Core;

/// <summary>
/// Representa uma pasta na biblioteca de midia (arvore do Explorer). Puramente domínio — quem
/// varre o sistema de arquivos de verdade e a Presentation/Infrastructure, nao esta classe.
///
/// <see cref="ChildrenLoaded"/> existe desde ja para permitir carregamento sob demanda (lazy
/// loading) das subpastas mais tarde, sem precisar remodelar esta classe: hoje quem povoa a
/// arvore pode marcar tudo como carregado de uma vez (eager); quando o volume de pastas justificar
/// lazy loading de verdade, so muda quem chama isso, nao a forma da classe.
/// </summary>
public sealed class MediaFolder
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public required string Name { get; set; }

    public required string Path { get; set; }

    /// <summary>Subpastas diretas. Pode estar vazia mesmo quando existem subpastas reais, se <see cref="ChildrenLoaded"/> for falso.</summary>
    public IList<MediaFolder> Folders { get; init; } = new List<MediaFolder>();

    /// <summary>Midias diretamente dentro desta pasta (nao inclui subpastas).</summary>
    public IList<MediaItem> Items { get; init; } = new List<MediaItem>();

    /// <summary>
    /// Indica se <see cref="Folders"/>/<see cref="Items"/> ja foram populados a partir do sistema
    /// de arquivos. Falso significa "ainda nao carregado" — o consumidor (ex.: um TreeView) decide
    /// quando chamar o carregamento, tipicamente na primeira expansao do no.
    /// </summary>
    public bool ChildrenLoaded { get; set; }
}
