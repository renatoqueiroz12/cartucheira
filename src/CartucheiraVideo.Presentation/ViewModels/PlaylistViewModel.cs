using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Input;
using CartucheiraVideo.Presentation.Mvvm;

namespace CartucheiraVideo.Presentation.ViewModels;

/// <summary>
/// ViewModel da aba Playlist do Explorer (mockup v8, nota #11) — substitui a antiga Fila Rápida
/// (Core.MediaQueue), que saiu da coluna dos Decks e virou esta aba, ao lado de Explorer, no
/// mesmo painel de mídia.
///
/// Carregar uma Playlist de verdade — Sprint 3 (2026-11-16, pedido do usuário "abrir o dia, aí abre
/// o programa e carrega") migrou isso de "sprint futura" pra realidade: <see cref="LoadReal"/>
/// troca os itens de exemplo pelas Laudas reais do Espelho de uma Edição (Infrastructure.Playlist,
/// via MainViewModel/<see cref="OpenPlaylistRequested"/>) — mesmo precedente da Sprint Cartucheira
/// (2026-08-09) pros bancos de exemplo da Botoneira, que deixaram de ser fictícios quando ganharam
/// população real por pasta. Até o primeiro <see cref="LoadReal"/>, os itens seguem fictícios (ver
/// <see cref="CreateSampleItems"/>), só pra lista não nascer vazia — sem caminho de arquivo real
/// (<see cref="PlaylistItemViewModel.HasRealFile"/> = false), então ativar um deles avisa
/// honestamente em vez de tentar carregar um arquivo que não existe (mesmo depois de uma Playlist
/// real carregada, uma Lauda sem VT — ex.: Entrevista/Nota/Passagem — também não tem o que carregar,
/// ver <see cref="ActivateItem"/>).
///
/// Avanço automático — Sprint 4 (2026-11-16, pedido do usuário depois de testar a v1.0.88: "toca
/// no deck 1, quando acabar ele carrega no deck a próxima lauda"): até a v1.0.88 isto era
/// deliberadamente NÃO implementado (decisão explícita do usuário na Sprint 3: "operador plays one
/// at a time manually"); o próprio usuário pediu de volta ao testar a Playlist de verdade pela
/// primeira vez. Decisão confirmada desta vez (pergunta objetiva feita antes de implementar): o
/// avanço automático só CARREGA a próxima Lauda no Deck (nunca aperta Play sozinho — o operador
/// continua decidindo a hora de ir ao ar). Depende de DOIS "disjuntores" juntos, mesma metáfora já
/// documentada: <see cref="AutoModeEnabled"/> (geral da Playlist) E o Auto/Manual de CADA Deck
/// (<see cref="DeckViewModel.IsAutoMode"/>) — ver <see cref="MarkLoadedInDeck"/>/
/// <see cref="TryGetNextItemForAutoAdvance"/> pra como o rastreio "qual Lauda está em qual Deck"
/// funciona, e <c>MainViewModel.OnDeckMediaEnded</c> pra onde isso é de fato acionado (só no fim
/// NATURAL do vídeo, nunca em Stop/Clear manual).
/// </summary>
public sealed class PlaylistViewModel : ViewModelBase
{
    private readonly Action<string> _onStatus;
    private readonly Dictionary<DeckViewModel, int> _loadedLaudaIndexByDeck = new();
    private bool _autoModeEnabled = true;
    private string _name = "Playlist_Jornal18h_v3";

    /// <summary>
    /// Falso enquanto só os itens de exemplo fictícios (ver <see cref="CreateSampleItems"/>) estão
    /// na lista; vira true depois do primeiro <see cref="LoadReal"/> — Sprint 3 (2026-11-16), só pra
    /// <see cref="ActivateItem"/> escolher a mensagem certa (item de exemplo x Lauda real sem VT).
    /// </summary>
    private bool _isRealPlaylistLoaded;

    public PlaylistViewModel(Action<string> onStatus)
    {
        _onStatus = onStatus ?? throw new ArgumentNullException(nameof(onStatus));

        foreach (var item in CreateSampleItems())
        {
            Items.Add(item);
        }

        // Sprint 3 (2026-11-16, pedido do usuário "abrir o dia, aí abre o programa e carrega"):
        // quem sabe ESCOLHER um Programa/Edição (abrir uma janela, ler disco via
        // PlaylistFileStore) é o MainViewModel — a mesma camada que já faz essa ponte pro resto da
        // Playlist-Dashboard (ver PlaylistDashboardCoordinator) — não esta classe, que fica só com
        // a UI da aba. LoadPlaylistCommand só avisa (evento) que o operador pediu pra abrir.
        LoadPlaylistCommand = new RelayCommand(_ => OpenPlaylistRequested?.Invoke());
        ToggleAutoModeCommand = new RelayCommand(_ => ToggleAutoMode());
    }

    /// <summary>Disparado quando o operador ativa (duplo clique) um item com arquivo real associado — mesmo papel do FileExplorerViewModel.FileActivated.</summary>
    public event Action<PlaylistItemViewModel>? ItemActivated;

    /// <summary>Disparado quando o operador clica "Carregar Playlist..." — Sprint 3 (2026-11-16), ver comentário do construtor.</summary>
    public event Action? OpenPlaylistRequested;

    public ObservableCollection<PlaylistItemViewModel> Items { get; } = new();

    public string Name
    {
        get => _name;
        private set => SetProperty(ref _name, value);
    }

    /// <summary>
    /// "Modo Automático" geral da Playlist (mockup v8, nota #12) — o "disjuntor geral": quando
    /// ligado, o item seguinte fica livre pra carregar sozinho, mas só nos Decks que também
    /// estiverem em modo Auto (o "disjuntor de cada tomada", ver DeckViewModel.IsAutoMode). Desde a
    /// Sprint 4 (v1.0.89) isto avança de verdade — ver comentário de classe.
    /// </summary>
    public bool AutoModeEnabled
    {
        get => _autoModeEnabled;
        private set => SetProperty(ref _autoModeEnabled, value);
    }

    public ICommand LoadPlaylistCommand { get; }

    public ICommand ToggleAutoModeCommand { get; }

    /// <summary>Chamado pela View (duplo clique numa linha, code-behind) — mesmo papel de FileExplorerViewModel.ActivateFile.</summary>
    public void ActivateItem(PlaylistItemViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        if (!item.HasRealFile)
        {
            _onStatus(_isRealPlaylistLoaded
                ? $"\"{item.Name}\" não tem VT associado nesta Lauda (ex.: Entrevista/Nota/Passagem) — nada pra carregar num Deck."
                : $"\"{item.Name}\" é um item de exemplo da Playlist (sem arquivo real associado ainda) — use \"Carregar Playlist...\" pra abrir uma Playlist real.");
            return;
        }

        ItemActivated?.Invoke(item);
    }

    /// <summary>
    /// Substitui os itens de exemplo (ou a Playlist real carregada antes) pela Playlist real de uma
    /// Edição — Sprint 3 (2026-11-16). Chamado pelo MainViewModel depois de resolver o
    /// Programa/Edição escolhidos (ver <see cref="OpenPlaylistRequested"/>) e montar os
    /// <see cref="PlaylistItemViewModel"/> (caminho absoluto do VT quando existir, vazio quando a
    /// Lauda não tem VT — ex.: "Entrevista"/"Nota" sem arquivo — pra <see cref="PlaylistItemViewModel.HasRealFile"/>
    /// ficar correto). Operador continua tocando manualmente (duplo clique carrega no Deck, ver
    /// <see cref="ActivateItem"/>) — sem avanço automático real ainda, mesma ressalva de sempre.
    /// </summary>
    public void LoadReal(string programName, string editionDateIso, IReadOnlyList<PlaylistItemViewModel> items)
    {
        _isRealPlaylistLoaded = true;
        Name = $"{programName} — {editionDateIso}";
        Items.Clear();
        foreach (var item in items)
        {
            Items.Add(item);
        }

        // Nova Playlist carregada: qualquer rastreio de "qual Lauda está em qual Deck" da Playlist
        // ANTERIOR não vale mais pra esta (os índices 1-based se repetem entre Playlists
        // diferentes, então deixar o dicionário antigo vivo poderia disparar um avanço automático
        // errado assim que um Deck terminasse de tocar algo completamente não relacionado).
        _loadedLaudaIndexByDeck.Clear();

        _onStatus(items.Count == 0
            ? $"Playlist de \"{programName}\" ({editionDateIso}) carregada — Espelho ainda sem nenhuma Lauda."
            : $"Playlist de \"{programName}\" ({editionDateIso}) carregada — {items.Count} Lauda(s).");
    }

    /// <summary>
    /// Chamado pelo MainViewModel sempre que uma Lauda desta Playlist é carregada (manualmente, por
    /// duplo clique, OU automaticamente por <see cref="TryGetNextItemForAutoAdvance"/>) num Deck
    /// específico — Sprint 4 (2026-11-16). Grava o Index dessa Lauda pra ESTE Deck, pra o avanço
    /// automático saber de onde continuar quando ele terminar de tocar sozinho, e atualiza os selos
    /// "TOCANDO"/"PRÓXIMO" (<see cref="RefreshCurrentNextFlags"/>) pra refletir a realidade, não
    /// mais uma fotografia fixa do instante em que a Playlist foi aberta.
    /// </summary>
    public void MarkLoadedInDeck(DeckViewModel deck, PlaylistItemViewModel item)
    {
        _loadedLaudaIndexByDeck[deck] = item.Index;
        RefreshCurrentNextFlags();
    }

    /// <summary>
    /// Avanço automático (Sprint 4, 2026-11-16) — chamado pelo MainViewModel quando um Deck termina
    /// de tocar sozinho, SEM loop (fim natural; nunca em Stop/Clear manual — ver
    /// <c>MainViewModel.OnDeckMediaEnded</c>, que é o único chamador). Devolve a próxima Lauda da
    /// sequência pronta pra carregar nesse MESMO Deck, ou <c>null</c> se não houver nada a fazer:
    /// este Deck nunca teve uma Lauda desta Playlist carregada nele (ex.: tocou algo da aba
    /// Explorer), não existe uma próxima Lauda na sequência, ou a próxima Lauda existe mas não tem
    /// VT associado (ex.: Entrevista/Nota/Passagem) — decisão deliberada: nunca inventa um avanço
    /// pra uma Lauda sem o que tocar, o operador precisa resolver isso manualmente, mesmo princípio
    /// "nunca inventa" de sempre neste projeto. Quem chama ainda precisa checar
    /// <see cref="AutoModeEnabled"/> E <see cref="DeckViewModel.IsAutoMode"/> do próprio Deck antes
    /// de chamar este método — os dois "disjuntores" são responsabilidade de quem aciona, não desta
    /// classe, pra ficar explícito no ponto de chamada exatamente o que precisa estar ligado.
    /// </summary>
    public PlaylistItemViewModel? TryGetNextItemForAutoAdvance(DeckViewModel deck)
    {
        if (!_loadedLaudaIndexByDeck.TryGetValue(deck, out var currentIndex))
        {
            return null;
        }

        var next = Items.FirstOrDefault(i => i.Index == currentIndex + 1);
        return next is { HasRealFile: true } ? next : null;
    }

    /// <summary>
    /// Sprint GC (2026-11-17) — qual Lauda desta Playlist está carregada em <paramref name="deck"/>
    /// AGORA, segundo o mesmo rastreio de <see cref="MarkLoadedInDeck"/> (atualizado a cada carga
    /// manual ou automática). Usado pelo <c>MainViewModel</c> em <c>OnDeckPlay</c>/<c>OnDeckStop</c>/
    /// <c>OnDeckMediaEnded</c> pra saber os <see cref="PlaylistItemViewModel.GcFields"/> da Lauda que
    /// está indo ao ar/saindo do ar — nulo se este Deck nunca teve nenhuma Lauda desta Playlist
    /// carregada nele (ex.: tocou algo da aba Explorer), mesmo critério de "nada a fazer" de
    /// <see cref="TryGetNextItemForAutoAdvance"/>.
    /// </summary>
    public PlaylistItemViewModel? TryGetLoadedItem(DeckViewModel deck) =>
        _loadedLaudaIndexByDeck.TryGetValue(deck, out var currentIndex)
            ? Items.FirstOrDefault(i => i.Index == currentIndex)
            : null;

    /// <summary>
    /// Recalcula "TOCANDO"/"PRÓXIMO" (<see cref="PlaylistItemViewModel.IsCurrent"/>/
    /// <see cref="PlaylistItemViewModel.IsNext"/>) a partir do rastreio real de qual Lauda está em
    /// qual Deck — substitui a fotografia fixa que existia antes da Sprint 4 (sempre item[0]/
    /// item[1], nunca atualizada). Com dois Decks, mais de uma Lauda pode estar "TOCANDO" ao mesmo
    /// tempo (uma em cada Deck) — comportamento esperado, não um bug.
    /// </summary>
    private void RefreshCurrentNextFlags()
    {
        var currentIndexes = _loadedLaudaIndexByDeck.Values.ToHashSet();
        var nextIndexes = currentIndexes.Select(i => i + 1).ToHashSet();

        foreach (var item in Items)
        {
            item.IsCurrent = currentIndexes.Contains(item.Index);
            item.IsNext = !item.IsCurrent && nextIndexes.Contains(item.Index);
        }
    }

    private void ToggleAutoMode()
    {
        AutoModeEnabled = !AutoModeEnabled;
        _onStatus(AutoModeEnabled
            ? "Modo Automático da Playlist ligado — quando uma Lauda terminar de tocar sozinha num Deck que também esteja em modo Auto, a próxima Lauda da sequência carrega automaticamente nesse Deck (sem apertar Play sozinho)."
            : "Modo Automático da Playlist desligado — o operador carrega cada item manualmente (duplo clique → Deck).");
    }

    private static IEnumerable<PlaylistItemViewModel> CreateSampleItems()
    {
        yield return new PlaylistItemViewModel(1, "abertura_jornal_18h.mp4", TimeSpan.FromSeconds(28), string.Empty, isCurrent: true, isNext: false);
        yield return new PlaylistItemViewModel(2, "chamada_comercial_bloco2.mp4", TimeSpan.FromSeconds(15), string.Empty, isCurrent: false, isNext: true);
        yield return new PlaylistItemViewModel(3, "entrevista_convidado_final.mp4", new TimeSpan(0, 3, 42), string.Empty, isCurrent: false, isNext: false);
        yield return new PlaylistItemViewModel(4, "nota_pe_clima.mp4", new TimeSpan(0, 1, 12), string.Empty, isCurrent: false, isNext: false);
        yield return new PlaylistItemViewModel(5, "vinheta_esportes.mp4", TimeSpan.FromSeconds(5), string.Empty, isCurrent: false, isNext: false);
        yield return new PlaylistItemViewModel(6, "encerramento_bloco1.mp4", TimeSpan.FromSeconds(8), string.Empty, isCurrent: false, isNext: false);
        yield return new PlaylistItemViewModel(7, "bg_grafico_cotacoes.mp4", TimeSpan.FromSeconds(20), string.Empty, isCurrent: false, isNext: false);
        yield return new PlaylistItemViewModel(8, "sweeper_azul.mp4", TimeSpan.FromSeconds(3), string.Empty, isCurrent: false, isNext: false);
    }
}
