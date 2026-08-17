namespace CartucheiraVideo.Presentation.ViewModels;

/// <summary>
/// Uma linha da aba Playlist do Explorer (mockup v8, nota #11). Representa um item de uma
/// sequência montada por um PROGRAMA EXTERNO — o operador só consome a ordem já pronta, não a
/// edita aqui (por isso não há árvore de pastas na Playlist, diferente da aba Explorer).
/// </summary>
public sealed class PlaylistItemViewModel
{
    public PlaylistItemViewModel(int index, string name, TimeSpan? duration, string filePath, bool isCurrent, bool isNext)
    {
        Index = index;
        Name = name;
        FilePath = filePath;
        IsCurrent = isCurrent;
        IsNext = isNext;
        DurationDisplay = duration?.ToString(@"mm\:ss") ?? "—";
    }

    /// <summary>Posição 1-based na sequência (coluna "#" do mockup).</summary>
    public int Index { get; }

    public string Name { get; }

    public string DurationDisplay { get; }

    /// <summary>
    /// Caminho completo do arquivo. Vazio para os itens de exemplo desta sprint (ver comentário
    /// de classe em <see cref="PlaylistViewModel"/>) — nesse caso o item não é carregável ainda.
    /// </summary>
    public string FilePath { get; }

    public bool HasRealFile => !string.IsNullOrWhiteSpace(FilePath);

    /// <summary>Linha destacada — este é o item tocando agora (selo "TOCANDO" vermelho no mockup).</summary>
    public bool IsCurrent { get; }

    /// <summary>Borda azul — este é o próximo item a entrar quando o atual terminar (selo "PRÓXIMO").</summary>
    public bool IsNext { get; }

    public string StatusLabel => IsCurrent ? "TOCANDO" : IsNext ? "PRÓXIMO" : string.Empty;
}
