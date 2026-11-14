namespace CartucheiraVideo.Core;

/// <summary>
/// A Playlist de um <see cref="PlaylistProgram"/> numa Data específica — Sprint Playlist-Dashboard
/// (2026-11-14). Cada Edição corresponde a uma pasta real no disco, dentro da pasta raiz configurada
/// em Configurações, criada pela dashboard quando o produtor cadastra a Data (ver
/// <c>CartucheiraVideo.Infrastructure.Playlist.PlaylistFileStore</c>, que fala em nomes de pasta
/// crus — é a Presentation quem traduz pra este objeto).
/// </summary>
public sealed class PlaylistEdition
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public Guid ProgramId { get; set; }

    public DateOnly Date { get; set; }

    /// <summary>Caminho real da pasta desta Edição no disco (<c>{Raiz}\{Programa}\{Data}</c>) — guardado aqui só como dado; quem cria/lê a pasta de verdade é a Infrastructure.</summary>
    public string FolderPath { get; set; } = string.Empty;

    public IList<PlaylistEntry> Entries { get; init; } = new List<PlaylistEntry>();
}
