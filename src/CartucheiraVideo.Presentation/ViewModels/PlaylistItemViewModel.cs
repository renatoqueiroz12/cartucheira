using CartucheiraVideo.Presentation.Mvvm;

namespace CartucheiraVideo.Presentation.ViewModels;

/// <summary>
/// Uma linha da aba Playlist do Explorer (mockup v8, nota #11). Representa um item de uma
/// sequência montada por um PROGRAMA EXTERNO — o operador só consome a ordem já pronta, não a
/// edita aqui (por isso não há árvore de pastas na Playlist, diferente da aba Explorer).
///
/// Sprint 4 (2026-11-16, pedido do usuário depois de testar a v1.0.88 — "toca no deck 1, quando
/// acabar ele carrega no deck a próxima lauda"): <see cref="IsCurrent"/>/<see cref="IsNext"/>
/// passaram de get-only (fotografia fixa no momento da carga, item[0]/item[1] sempre, nunca
/// atualizado) pra propriedades de verdade, com <see cref="ViewModelBase.SetProperty{T}"/> —
/// agora refletem qual Lauda está realmente carregada em cada Deck, atualizadas por
/// <see cref="PlaylistViewModel.MarkLoadedInDeck"/> a cada carga manual (duplo clique) ou
/// automática (ver <see cref="PlaylistViewModel.TryGetNextItemForAutoAdvance"/>).
/// </summary>
public sealed class PlaylistItemViewModel : ViewModelBase
{
    private bool _isCurrent;
    private bool _isNext;

    public PlaylistItemViewModel(int index, string name, TimeSpan? duration, string filePath, bool isCurrent, bool isNext)
    {
        Index = index;
        Name = name;
        FilePath = filePath;
        _isCurrent = isCurrent;
        _isNext = isNext;
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
    public bool IsCurrent
    {
        get => _isCurrent;
        set
        {
            if (SetProperty(ref _isCurrent, value))
            {
                OnPropertyChanged(nameof(StatusLabel));
            }
        }
    }

    /// <summary>Borda azul — este é o próximo item a entrar quando o atual terminar (selo "PRÓXIMO").</summary>
    public bool IsNext
    {
        get => _isNext;
        set
        {
            if (SetProperty(ref _isNext, value))
            {
                OnPropertyChanged(nameof(StatusLabel));
            }
        }
    }

    public string StatusLabel => IsCurrent ? "TOCANDO" : IsNext ? "PRÓXIMO" : string.Empty;
}
