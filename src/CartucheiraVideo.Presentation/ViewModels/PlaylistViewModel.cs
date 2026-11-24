using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Input;
using CartucheiraVideo.Infrastructure.Playlist;
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

    /// <summary>
    /// Correção pós-entrega #11, Sprint Reformulação-Visual-Neon (2026-11-24): "Carregar Playlist..."
    /// abria o <c>OpenPlaylistWindow</c> (janela modal separada) — pedido do usuário pra virar um
    /// seletor INLINE, dentro desta mesma aba Playlist, sem pop-up ("Programas, e dos programas
    /// clica, aparece as datas... tudo dentro dessa aba aqui"). A separação de responsabilidades
    /// continua a mesma de sempre: esta classe só guarda o ESTADO do seletor (qual passo, quais
    /// opções, erro) e expõe <see cref="ProgramChosen"/>/<see cref="EditionChosen"/> pro
    /// MainViewModel continuar sendo o único a ler disco (<c>PlaylistFileStore</c>) — mesmo papel
    /// que antes cumpria via <c>OpenPlaylistWindow.xaml.cs</c>, só que sem Window nenhuma.
    /// </summary>
    private IReadOnlyList<PlaylistEditionRecord> _pickerEditionRecords = Array.Empty<PlaylistEditionRecord>();
    private PlaylistProgramRecord? _pickerSelectedProgram;
    private bool _isPickerOpen;
    private bool _isPickerShowingEditions;
    private string _pickerErrorMessage = string.Empty;
    private string _pickerEditionsTitle = string.Empty;

    public PlaylistViewModel(Action<string> onStatus)
    {
        _onStatus = onStatus ?? throw new ArgumentNullException(nameof(onStatus));

        foreach (var item in CreateSampleItems())
        {
            Items.Add(item);
        }

        // Sprint 3 (2026-11-16, pedido do usuário "abrir o dia, aí abre o programa e carrega"):
        // quem sabe ESCOLHER um Programa/Edição (ler disco via PlaylistFileStore) é o
        // MainViewModel — a mesma camada que já faz essa ponte pro resto da Playlist-Dashboard (ver
        // PlaylistDashboardCoordinator) — não esta classe, que fica só com a UI da aba.
        // LoadPlaylistCommand só avisa (evento) que o operador pediu pra abrir; desde a correção
        // #11 o MainViewModel responde chamando ShowProgramPicker em vez de abrir uma Window.
        LoadPlaylistCommand = new RelayCommand(_ => OpenPlaylistRequested?.Invoke());
        ToggleAutoModeCommand = new RelayCommand(_ => ToggleAutoMode());
        // Correção pós-entrega #12 (2026-11-24): CanExecute tied a IsPickerShowingEditions — igual
        // ao botão "voltar" de um navegador, só faz sentido clicar quando tem pra onde voltar
        // (passo 2, Edições). WPF já desabilita o Button sozinho a partir do CanExecute, sem
        // precisar de Visibility condicional.
        BackToProgramsCommand = new RelayCommand(_ => BackToPrograms(), _ => IsPickerShowingEditions);
        ForwardToEditionsCommand = new RelayCommand(_ => GoForwardToEditions(), _ => CanGoForward);
        ClosePickerCommand = new RelayCommand(_ => ClosePicker());
    }

    /// <summary>Disparado quando o operador ativa (duplo clique) um item com arquivo real associado — mesmo papel do FileExplorerViewModel.FileActivated.</summary>
    public event Action<PlaylistItemViewModel>? ItemActivated;

    /// <summary>Disparado quando o operador clica "Carregar Playlist..." — Sprint 3 (2026-11-16), ver comentário do construtor.</summary>
    public event Action? OpenPlaylistRequested;

    /// <summary>Disparado quando o operador escolhe um Programa no seletor inline (correção #11) — MainViewModel resolve as Edições e chama <see cref="ShowEditionPicker"/>.</summary>
    public event Action<PlaylistProgramRecord>? ProgramChosen;

    /// <summary>Disparado quando o operador escolhe uma Data/Edição no seletor inline (correção #11) — MainViewModel lê as Laudas reais e chama <see cref="LoadReal"/>, igual sempre fez.</summary>
    public event Action<PlaylistProgramRecord, PlaylistEditionRecord>? EditionChosen;

    public ObservableCollection<PlaylistItemViewModel> Items { get; } = new();

    /// <summary>Correção pós-entrega #11 (2026-11-24): Programas disponíveis pro seletor inline — populado por <see cref="ShowProgramPicker"/>.</summary>
    public ObservableCollection<PlaylistProgramRecord> PickerPrograms { get; } = new();

    /// <summary>Correção pós-entrega #11 (2026-11-24): Edições (Datas) do Programa escolhido — populado por <see cref="ShowEditionPicker"/>.</summary>
    public ObservableCollection<PlaylistEditionRecord> PickerEditions { get; } = new();

    /// <summary>Correção pós-entrega #11 (2026-11-24): true enquanto o seletor inline (Programas/Datas) está visível por cima da lista normal da Playlist.</summary>
    public bool IsPickerOpen
    {
        get => _isPickerOpen;
        private set => SetProperty(ref _isPickerOpen, value);
    }

    /// <summary>Correção pós-entrega #11 (2026-11-24): false = mostrando a lista de Programas (passo 1); true = mostrando a lista de Datas do Programa escolhido (passo 2).</summary>
    public bool IsPickerShowingEditions
    {
        get => _isPickerShowingEditions;
        private set
        {
            if (SetProperty(ref _isPickerShowingEditions, value))
            {
                OnPropertyChanged(nameof(CanGoForward));
                // Correção pós-entrega #12 (2026-11-24): Voltar/Avançar (estilo histórico de
                // navegador) dependem deste valor pro CanExecute — avisados aqui, num lugar só,
                // em vez de espalhar a chamada em cada método que troca de passo.
                ((RelayCommand)BackToProgramsCommand).RaiseCanExecuteChanged();
                ((RelayCommand)ForwardToEditionsCommand).RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>
    /// Correção pós-entrega #12 (2026-11-24): "voltar/avançar, pra poder ir pra frente ou pra trás"
    /// — pedido explícito do usuário por uma navegação tipo histórico de navegador, não só uma ida
    /// única Programas -> Datas. True quando o operador já visitou as Datas de algum Programa nesta
    /// abertura do seletor e voltou pros Programas (<see cref="BackToPrograms"/>) SEM escolher um
    /// Programa diferente — nesse caso dá pra "avançar" de volta pras Datas já carregadas
    /// (<see cref="PickerEditions"/>) sem reler o disco. Fica false de novo assim que uma Data é
    /// escolhida de verdade (a Playlist carrega e o seletor fecha) ou quando ele é reaberto do zero
    /// (<see cref="ShowProgramPicker"/>).
    /// </summary>
    public bool CanGoForward => !IsPickerShowingEditions && _pickerSelectedProgram is not null;

    /// <summary>Correção pós-entrega #11 (2026-11-24): mensagem de erro do seletor inline (ex.: falha ao listar Programas/Datas/Laudas do disco) — vazio quando não há erro.</summary>
    public string PickerErrorMessage
    {
        get => _pickerErrorMessage;
        private set
        {
            if (SetProperty(ref _pickerErrorMessage, value))
            {
                OnPropertyChanged(nameof(HasPickerError));
            }
        }
    }

    /// <summary>Correção pós-entrega #11 (2026-11-24): true quando <see cref="PickerErrorMessage"/> tem algum texto — evita precisar de um converter string→Visibility só pra isso.</summary>
    public bool HasPickerError => !string.IsNullOrEmpty(PickerErrorMessage);

    /// <summary>Correção pós-entrega #11 (2026-11-24): título do passo 2 ("Datas de "X""), igual ao antigo <c>EditionsTitle</c> do diálogo modal.</summary>
    public string PickerEditionsTitle
    {
        get => _pickerEditionsTitle;
        private set => SetProperty(ref _pickerEditionsTitle, value);
    }

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

    /// <summary>Correção pós-entrega #11 (2026-11-24): "← Voltar pros Programas" do passo 2 do seletor inline.</summary>
    public ICommand BackToProgramsCommand { get; }

    /// <summary>Correção pós-entrega #12 (2026-11-24): "avançar" de volta pras Datas já carregadas, depois de ter usado <see cref="BackToProgramsCommand"/> sem escolher um Programa diferente — ver <see cref="CanGoForward"/>.</summary>
    public ICommand ForwardToEditionsCommand { get; }

    /// <summary>Correção pós-entrega #11 (2026-11-24): fecha o seletor inline sem escolher nada (equivalente ao "Cancelar" do antigo diálogo modal).</summary>
    public ICommand ClosePickerCommand { get; }

    /// <summary>
    /// Correção pós-entrega #11 (2026-11-24): chamado pelo MainViewModel (em resposta a
    /// <see cref="OpenPlaylistRequested"/>) com a lista de Programas já lida do disco — abre o
    /// seletor inline no passo 1 (Programas). Equivalente ao antigo <c>OpenPlaylistWindow.LoadPrograms</c>.
    /// </summary>
    public void ShowProgramPicker(IReadOnlyList<PlaylistProgramRecord> programs)
    {
        PickerPrograms.Clear();
        foreach (var program in programs)
        {
            PickerPrograms.Add(program);
        }

        _pickerSelectedProgram = null;
        PickerErrorMessage = string.Empty;
        IsPickerShowingEditions = false;
        IsPickerOpen = true;
        OnPropertyChanged(nameof(CanGoForward));
        ((RelayCommand)ForwardToEditionsCommand).RaiseCanExecuteChanged();
    }

    /// <summary>
    /// Correção pós-entrega #11 (2026-11-24): chamado pelo MainViewModel com as Edições (Datas) do
    /// Programa escolhido — avança o seletor inline pro passo 2. Equivalente ao antigo
    /// <c>OpenPlaylistWindow.LoadEditions</c>.
    /// </summary>
    public void ShowEditionPicker(PlaylistProgramRecord program, IReadOnlyList<PlaylistEditionRecord> editions)
    {
        _pickerSelectedProgram = program;
        _pickerEditionRecords = editions;

        PickerEditions.Clear();
        foreach (var edition in editions)
        {
            PickerEditions.Add(edition);
        }

        PickerEditionsTitle = $"Datas de \"{program.Name}\"";
        PickerErrorMessage = string.Empty;
        IsPickerShowingEditions = true;
        OnPropertyChanged(nameof(CanGoForward));
        ((RelayCommand)ForwardToEditionsCommand).RaiseCanExecuteChanged();
    }

    /// <summary>
    /// Correção pós-entrega #11 (2026-11-24): MainViewModel chama isto quando ler Programas/Datas/
    /// Laudas do disco falha — mesmo texto que antes ia pro <c>ErrorText</c> do diálogo modal, agora
    /// mostrado dentro do próprio seletor inline (que continua aberto, no passo em que já estava).
    /// </summary>
    public void ShowPickerError(string message)
    {
        PickerErrorMessage = message;
    }

    /// <summary>
    /// Correção pós-entrega #12 (2026-11-24): chamado pelo FileExplorerViewModel quando o operador
    /// ENTRA na aba Playlist (transição Explorer -> Playlist) — pedido do usuário pra tirar o botão
    /// "Carregar Playlist..." e a própria aba já abrir o seletor de Programas sozinha. Reaproveita o
    /// mesmo evento que o botão antigo disparava (<see cref="OpenPlaylistRequested"/>), então o
    /// MainViewModel não precisa saber a diferença entre "clicou no botão" e "entrou na aba".
    /// </summary>
    public void RequestPickerOnTabEnter()
    {
        OpenPlaylistRequested?.Invoke();
    }

    /// <summary>Chamado pela View (duplo clique numa linha de Programa, code-behind) — avisa o MainViewModel pra resolver as Edições daquele Programa.</summary>
    public void SelectProgram(PlaylistProgramRecord? program)
    {
        if (program is null)
        {
            return;
        }

        ProgramChosen?.Invoke(program);
    }

    /// <summary>Chamado pela View (duplo clique numa linha de Data, code-behind) — avisa o MainViewModel pra ler as Laudas reais daquela Edição e carregar a Playlist.</summary>
    public void SelectEdition(PlaylistEditionRecord? edition)
    {
        if (edition is null || _pickerSelectedProgram is null)
        {
            return;
        }

        EditionChosen?.Invoke(_pickerSelectedProgram, edition);
    }

    /// <summary>
    /// "← Voltar pros Programas" (passo 2 → passo 1). Correção pós-entrega #12 (2026-11-24): NÃO
    /// zera mais <see cref="_pickerSelectedProgram"/>/<see cref="_pickerEditionRecords"/> (só
    /// esconde o passo 2) — assim <see cref="GoForwardToEditions"/> consegue voltar pra elas sem
    /// reler o disco, igual um "avançar" de navegador. Só são zerados de verdade quando o usuário
    /// escolhe um Programa DIFERENTE (<see cref="ShowEditionPicker"/> sobrescreve) ou reabre o
    /// seletor do zero (<see cref="ShowProgramPicker"/>/<see cref="ClosePicker"/>).
    /// </summary>
    private void BackToPrograms()
    {
        PickerErrorMessage = string.Empty;
        IsPickerShowingEditions = false;
        ((RelayCommand)ForwardToEditionsCommand).RaiseCanExecuteChanged();
    }

    /// <summary>
    /// Correção pós-entrega #12 (2026-11-24): "avançar" (passo 1 → passo 2) de volta pras Datas já
    /// carregadas na visita anterior — só habilitado quando <see cref="CanGoForward"/> é true (ver
    /// RelayCommand no construtor), então não precisa reler <see cref="PickerEditions"/> aqui, ela
    /// já está populada desde o <see cref="ShowEditionPicker"/> original.
    /// </summary>
    private void GoForwardToEditions()
    {
        if (_pickerSelectedProgram is null)
        {
            return;
        }

        PickerErrorMessage = string.Empty;
        IsPickerShowingEditions = true;
        ((RelayCommand)ForwardToEditionsCommand).RaiseCanExecuteChanged();
    }

    /// <summary>Fecha o seletor inline sem escolher nada (equivalente ao "Cancelar"/fechar o antigo diálogo modal).</summary>
    private void ClosePicker()
    {
        IsPickerOpen = false;
        _pickerSelectedProgram = null;
        PickerErrorMessage = string.Empty;
        OnPropertyChanged(nameof(CanGoForward));
        ((RelayCommand)ForwardToEditionsCommand).RaiseCanExecuteChanged();
    }

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

        // Correção pós-entrega #11 (2026-11-24): LoadReal só é chamado depois de uma Edição
        // escolhida no seletor inline (ver EditionChosen/MainViewModel) — fecha o seletor de volta
        // pra lista normal da Playlist, equivalente a DialogResult=true fechar o antigo diálogo modal.
        IsPickerOpen = false;
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
