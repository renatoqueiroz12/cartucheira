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

    public PlaylistItemViewModel(int index, string name, TimeSpan? duration, string filePath, bool isCurrent, bool isNext, string? tpText = null, IReadOnlyDictionary<string, string>? gcFields = null)
    {
        Index = index;
        Name = name;
        FilePath = filePath;
        _isCurrent = isCurrent;
        _isNext = isNext;
        DurationDisplay = duration?.ToString(@"mm\:ss") ?? "—";
        TpText = tpText;
        GcFields = gcFields ?? new Dictionary<string, string>();
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

    /// <summary>Texto de teleprompter (TP) desta Lauda (<see cref="Core.PlaylistLauda.TpText"/>) — nulo pros itens de exemplo (ver <see cref="PlaylistViewModel.CreateSampleItems"/>) ou Laudas sem TP preenchido. Lido pelo <c>MainViewModel</c> (mesmos pontos que chamam <see cref="PlaylistViewModel.MarkLoadedInDeck"/>) pra repassar ao <see cref="TeleprompterViewModel"/>.</summary>
    public string? TpText { get; }

    /// <summary>Campos de GC desta Lauda (<see cref="Core.PlaylistLauda.GcFields"/>) — vazio pros itens de exemplo ou Laudas sem nenhum campo de GC preenchido. Lido pelo <c>MainViewModel</c> (mesmos pontos que leem <see cref="TpText"/>) pra repassar ao <c>GcViewModel</c> — Sprint GC (2026-11-17).</summary>
    public IReadOnlyDictionary<string, string> GcFields { get; }

    /// <summary>
    /// Sprint UI-Playlist-Mais-Info (2026-11-19, pedido do usuário "no playlist do lado direito
    /// deixar as linhas de cada lauda maior com fontes maiores e colocar mais infos - titulo da
    /// lauda e o campo descriptions do GC e o tempo"): valor do campo de <see cref="GcFields"/> cujo
    /// NOME contém "description" (procura por substring, sem distinguir maiúsculas/minúsculas — o
    /// nome exato do campo vem de Configurações → GC e pode mudar, ex.: "Description.Text", então
    /// não dá pra travar numa chave fixa). Mostrado como subtítulo discreto embaixo do nome da Lauda
    /// na aba Playlist (ver FileExplorerPanel.xaml), pra dar mais contexto no relance sem precisar
    /// abrir a Lauda pra editar.
    /// </summary>
    public string GcDescriptionPreview
    {
        get
        {
            foreach (var pair in GcFields)
            {
                if (pair.Key.Contains("description", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(pair.Value))
                {
                    return pair.Value;
                }
            }

            return string.Empty;
        }
    }

    /// <summary>Verdadeiro quando <see cref="GcDescriptionPreview"/> tem algo pra mostrar — controla a visibilidade da linha extra no template (ver <c>BoolToVis</c> em FileExplorerPanel.xaml).</summary>
    public bool HasGcDescriptionPreview => !string.IsNullOrWhiteSpace(GcDescriptionPreview);

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
