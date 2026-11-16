using System.Collections.ObjectModel;
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
/// população real por pasta. O avanço automático de item (ligado ao Modo Automático + Auto/Manual
/// de cada Deck) continua NÃO implementado — decisão explícita do usuário nesta mesma Sprint 3: o
/// operador carrega a sequência ordenada e toca cada item manualmente (duplo clique → Deck), nunca
/// avança sozinho. Até o primeiro <see cref="LoadReal"/>, os itens seguem fictícios (ver
/// <see cref="CreateSampleItems"/>), só pra lista não nascer vazia — sem caminho de arquivo real
/// (<see cref="PlaylistItemViewModel.HasRealFile"/> = false), então ativar um deles avisa
/// honestamente em vez de tentar carregar um arquivo que não existe (mesmo depois de uma Playlist
/// real carregada, uma Lauda sem VT — ex.: Entrevista/Nota/Passagem — também não tem o que carregar,
/// ver <see cref="ActivateItem"/>).
/// </summary>
public sealed class PlaylistViewModel : ViewModelBase
{
    private readonly Action<string> _onStatus;
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
    /// estiverem em modo Auto (o "disjuntor de cada tomada", ver DeckViewModel.IsAutoMode). Ainda
    /// não avança nada de verdade — ver comentário de classe.
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

        _onStatus(items.Count == 0
            ? $"Playlist de \"{programName}\" ({editionDateIso}) carregada — Espelho ainda sem nenhuma Lauda."
            : $"Playlist de \"{programName}\" ({editionDateIso}) carregada — {items.Count} Lauda(s).");
    }

    private void ToggleAutoMode()
    {
        AutoModeEnabled = !AutoModeEnabled;
        _onStatus(AutoModeEnabled
            ? "Modo Automático da Playlist ligado — decisão do usuário (Sprint 3): avanço automático real não existe, o operador sempre carrega cada item manualmente (duplo clique → Deck)."
            : "Modo Automático da Playlist desligado.");
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
