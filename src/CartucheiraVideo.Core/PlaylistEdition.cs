namespace CartucheiraVideo.Core;

/// <summary>
/// A Playlist de um <see cref="PlaylistProgram"/> numa Data específica — Sprint Playlist-Dashboard
/// (2026-11-14). Cada Edição corresponde a uma pasta real no disco, dentro da pasta raiz configurada
/// em Configurações, criada pela dashboard quando o produtor cadastra a Data (ver
/// <c>CartucheiraVideo.Infrastructure.Playlist.PlaylistFileStore</c>, que fala em nomes de pasta
/// crus — é a Presentation quem traduz pra este objeto).
///
/// <see cref="Laudas"/> é o Espelho desta Edição — a sequência ordenada de Laudas que vão ao ar
/// (renomeado de "Entries" na Sprint 2, 2026-11-15, junto com <c>PlaylistEntry</c> →
/// <see cref="PlaylistLauda"/>; "Espelho" não precisou virar uma classe própria, é só esta mesma
/// lista com um nome que bate com o vocabulário real de redação de TV).
/// </summary>
public sealed class PlaylistEdition
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public Guid ProgramId { get; set; }

    public DateOnly Date { get; set; }

    /// <summary>Caminho real da pasta desta Edição no disco (<c>{Raiz}\{Programa}\{Data}</c>) — guardado aqui só como dado; quem cria/lê a pasta de verdade é a Infrastructure.</summary>
    public string FolderPath { get; set; } = string.Empty;

    /// <summary>O Espelho desta Edição — a sequência ordenada de Laudas (ver <see cref="PlaylistLauda.Order"/>).</summary>
    public IList<PlaylistLauda> Laudas { get; init; } = new List<PlaylistLauda>();
}
