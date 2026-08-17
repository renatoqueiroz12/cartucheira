using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using CartucheiraVideo.Core;
using CartucheiraVideo.Ndi;
using CartucheiraVideo.Presentation.Mvvm;
using Engine = CartucheiraVideo.Engine;
using LibVLC = LibVLCSharp.Shared.LibVLC;
using MediaPlayer = LibVLCSharp.Shared.MediaPlayer;

namespace CartucheiraVideo.Presentation.ViewModels;

/// <summary>
/// ViewModel de um Deck (1 ou 2), totalmente independente do outro. Representa um <see cref="Core.Deck"/>
/// (arquitetura §12) — o estado de reproducao (<see cref="Core.DeckPlaybackState"/>) vem do Core desde
/// a Sprint 6.
///
/// Desde a Sprint Engine (2026-08-08), a reproducao e REAL: este ViewModel envolve um
/// <see cref="Engine.PlaybackEngine"/> (um por Deck, sobre a instancia compartilhada de
/// <see cref="LibVLC"/> injetada no construtor) em vez de simular tempo com um
/// <see cref="System.Windows.Threading.DispatcherTimer"/>. <see cref="DeckPlaybackState"/> agora e
/// dirigido pelos eventos reais do Engine (Playing/Paused/Stopped/Ended/Error), nao mais por um
/// timer de UI — por isso os handlers dos comandos (Play/Pause/Stop) so chamam o Engine; quem
/// muda <see cref="State"/> de fato sao os handlers de evento do Engine, marshalados pra UI thread
/// (os eventos do LibVLCSharp disparam numa thread interna do libvlc).
///
/// Os estados Loading/Ready/Seeking/Buffering/Offline do Core.DeckPlaybackState ainda nao sao
/// produzidos por esta sprint (o Engine nao reporta Loading/Buffering granular ainda, Seek so e
/// exposto como metodo, nao como estado, e Offline e sobre disponibilidade do proprio motor) —
/// continuam existindo no enum e com rotulo/cor definidos aqui, para quando entrarem numa sprint
/// futura.
///
/// Desde a Sprint Config (2026-08-08), <see cref="DomainDeck"/> deixa de ser so uma referencia em
/// comentario e passa a existir de verdade: SendToVMix/SendToResolume agora leem/escrevem direto
/// nos OutputRoute dele (nao mais bools soltos). Desde a Sprint Config Geral, parte 1 (2026-08-09),
/// quem edita NdiChannelName/OutputRoutes/AutomationActions deste objeto e a janela unica de
/// Configuracoes (Views.SettingsWindow, aberta pelo MainViewModel.OpenSettingsCommand, protegida
/// por senha de admin) — o Deck em si nao tem mais engrenagem/OpenSettingsCommand proprios, so
/// existe pela barra superior agora.
///
/// Desde a Sprint NDI (2026-08-09), cada Deck tambem cria seu proprio <see cref="Ndi.NdiSender"/>
/// (canal "sempre quente", nome tirado de <see cref="Core.Deck.NdiChannelName"/> ou, se vazio, do
/// proprio <see cref="Name"/> do Deck) e repassa pra ele, sem nenhum marshaling de thread, todo
/// quadro que o <see cref="Engine.PlaybackEngine.FrameReady"/> do seu Engine emitir. Se a NDI SDK
/// nao estiver instalada/utilizavel nesta maquina, a criacao do NdiSender falha de forma limpa
/// (capturada aqui) — o Deck continua funcionando normalmente pra reproducao local, so sem saida
/// NDI, e um aviso honesto vai pro StatusMessage da MainWindow.
///
/// Sprint Deck-pausa-Cartucheira (2026-08-11, revisada no mesmo dia — v2): o novo callback
/// <c>onMediaEnded</c> do construtor (disparado por <see cref="OnEngineMediaEnded"/>, sempre
/// marshalado pra UI thread) e o que o MainViewModel usa pra saber quando retomar as duas
/// Cartucheiras (Vinhetas/Trilhas) que este Deck tiver pausado — ver
/// <c>MainViewModel.PauseCartucheiraFor</c>/<c>MainViewModel.ResumeCartucheiraFor</c>. Decisao de
/// pausar ou nao (v1 desta sprint: um bool <c>PausesCartucheiraOnPlay</c> proprio, revertido) agora
/// mora inteiramente no <see cref="DomainDeck"/>: uma acao "Ao dar Play" com
/// <c>Destination == OutputRouteType.Cartucheira</c> em <see cref="Core.Deck.AutomationActions"/>
/// (aba Automacao, configurada via <c>DeckSettingsViewModel.OnPlayDestinationOptions</c>) — mesmo
/// vocabulario ja usado pra vMix/Resolume, em vez de um bool novo e paralelo.
/// </summary>
public sealed class DeckViewModel : ViewModelBase, IDisposable
{
    private readonly Action<DeckViewModel> _onPlay;
    private readonly Action<DeckViewModel> _onPause;
    private readonly Action<DeckViewModel> _onStop;
    private readonly Action<DeckViewModel> _onMediaEnded;
    private readonly Action<DeckViewModel, string> _onError;
    private readonly Action<string> _onStatus;
    private readonly Engine.PlaybackEngine _engine;
    private readonly NdiSender? _ndiSender;

    private string _loadedFileName = "Vazio";
    private string? _loadedFilePath;
    private DeckPlaybackState _state = DeckPlaybackState.Cue;
    private double _progress;
    private string _currentTime = "00:00";
    private string _remainingTime = "--:--";
    private bool _loopEnabled;
    private bool _isDragOver;

    /// <summary>
    /// Diagnostico pos-crash (2026-08-11, mesmo dia): verdadeiro depois que o primeiro bloco de
    /// audio de um Play chega em <see cref="OnEngineAudioFrameReady"/> — usado so pra mostrar UMA
    /// mensagem de status confirmando que o pipeline de audio-pro-NDI esta sendo executado de
    /// verdade (com os numeros reais recebidos), em vez de descobrir isso as cegas. Resetado a cada
    /// <see cref="Play"/> pra confirmar de novo em toda nova reproducao.
    /// </summary>
    private bool _audioNdiDiagnosticShown;
    private bool _isAutoMode;
    private bool _disposed;

    /// <summary>
    /// Sprint Preview-Isolado (2026-08-15): verdadeiro enquanto a reprodução em andamento foi
    /// iniciada por <see cref="Preview"/> (não por <see cref="Play"/>) — usado por
    /// <see cref="Stop"/>/<see cref="Clear"/>/<see cref="OnEngineMediaEnded"/> pra saber se devem
    /// notificar o MainViewModel (pausa de Cartucheira, aviso UDP de fim) ou não: uma sessão que
    /// nunca foi "ao vivo" (Preview) também não pode terminar mandando avisos de "ao vivo terminou".
    /// Resetado em <see cref="Load"/> (toda carga nova começa "limpa") e a cada chamada de
    /// <see cref="Play"/>/<see cref="Preview"/> (a mais recente decide).
    /// </summary>
    private bool _isPreviewOnly;

    public DeckViewModel(
        string name,
        LibVLC libVlc,
        Action<DeckViewModel> onPlay,
        Action<DeckViewModel> onPause,
        Action<DeckViewModel> onStop,
        Action<DeckViewModel> onMediaEnded,
        Action<DeckViewModel, string> onError,
        Action<string> onStatus)
    {
        ArgumentNullException.ThrowIfNull(libVlc);

        Name = name;

        // Sprint Config (2026-08-08): o Deck passa a envolver de fato um Core.Deck (arquitetura
        // §12) em vez de guardar SendToVMix/SendToResolume em bools soltos — os dois OutputRoute
        // abaixo (vMix/Resolume, Enabled=true por padrao, mesmo valor default que os bools tinham
        // antes) sao a fonte de verdade agora, e sao o que a tela de Configuracao (engrenagem)
        // edita de verdade. NdiChannelName/AutomationActions comecam vazios ate o operador
        // preencher na mesma tela.
        DomainDeck = new Deck
        {
            Name = name,
            OutputRoutes =
            {
                new OutputRoute { Destination = "vMix", OutputType = OutputRouteType.VMix, Enabled = true, Priority = 0 },
                new OutputRoute { Destination = "Resolume", OutputType = OutputRouteType.Resolume, Enabled = true, Priority = 1 }
            }
        };

        _onPlay = onPlay ?? throw new ArgumentNullException(nameof(onPlay));
        _onPause = onPause ?? throw new ArgumentNullException(nameof(onPause));
        _onStop = onStop ?? throw new ArgumentNullException(nameof(onStop));
        _onMediaEnded = onMediaEnded ?? throw new ArgumentNullException(nameof(onMediaEnded));
        _onError = onError ?? throw new ArgumentNullException(nameof(onError));
        _onStatus = onStatus ?? throw new ArgumentNullException(nameof(onStatus));

        _engine = new Engine.PlaybackEngine(libVlc);
        _engine.StateChanged += OnEngineStateChanged;
        _engine.PositionChanged += OnEnginePositionChanged;
        _engine.MediaEnded += OnEngineMediaEnded;
        _engine.ErrorOccurred += OnEngineError;

        // Sprint NDI (2026-08-09): tenta criar o emissor NDI deste Deck agora, pra ele ficar
        // "sempre quente" (pesquisa-ndi-switching.md) assim que o Deck existe, nao so quando o
        // operador der o primeiro Play. Nome do canal ainda nao foi definido pelo operador nesse
        // ponto do construtor (DomainDeck.NdiChannelName comeca nulo) — cai pro nome do Deck
        // ("Deck 1"/"Deck 2") ate a tela de Configuracao ser usada. Limitacao conhecida desta
        // sprint: renomear o canal NDI depois exige reiniciar o programa (o NdiSender ja foi criado
        // com o nome antigo) — sera resolvido numa sprint futura de hardening.
        try
        {
            var ndiName = !string.IsNullOrWhiteSpace(DomainDeck.NdiChannelName) ? DomainDeck.NdiChannelName! : Name;
            _ndiSender = new NdiSender(ndiName);
            _engine.FrameReady += OnEngineFrameReady;
            // Sprint NDI-audio (2026-08-11): mesmo canal NDI, agora tambem carregando audio — mesma
            // ligacao direta (sem RunOnUiThread) do FrameReady acima, pelo mesmo motivo de latencia.
            _engine.AudioFrameReady += OnEngineAudioFrameReady;
        }
        catch (Exception ex)
        {
            // Qualquer falha aqui (DLL nativa ausente, NDI Runtime nao instalado, etc.) NAO pode
            // derrubar o Deck inteiro — reproducao local continua funcionando normalmente, so sem
            // saida NDI. Excecao generica de proposito: e a fronteira com codigo nativo (P/Invoke),
            // onde o tipo exato da falha varia com o motivo (DLL ausente, SDK incompativel, etc.).
            _ndiSender = null;
            _onStatus($"{Name}: NDI indisponivel ({ex.Message})");
        }

        PlayCommand = new RelayCommand(_ => Play(), _ => HasVideo && State != DeckPlaybackState.Playing);
        PauseCommand = new RelayCommand(_ => Pause(), _ => State == DeckPlaybackState.Playing);
        StopCommand = new RelayCommand(_ => Stop(), _ => State != DeckPlaybackState.Cue);

        // Clear (ajustes de Deck, 2026-08-09): esvazia o Deck de vez — diferente do Stop (que
        // mantem a midia carregada pra recomecar do inicio), Clear tira a midia do Engine tambem.
        // Habilitado sempre que houver algo carregado (mesma logica do HasVideo que o Play/Preview
        // ja usam).
        ClearCommand = new RelayCommand(_ => Clear(), _ => HasVideo);

        // Loop (mockup v8, nota #8): virou um botao de toggle na linha de transporte, no lugar do
        // antigo ComboBox "Sem loop"/"Loop do arquivo" — mesmo efeito real sobre o Engine, so a
        // interacao mudou (um clique liga/desliga, sem abrir lista).
        ToggleLoopCommand = new RelayCommand(_ => ToggleLoop());

        // Next (mockup v8, nota #17): avanca manualmente pro proximo item da Playlist. So existe
        // de verdade quando houver uma Playlist real pra avancar — isso entra numa sprint futura
        // (roadmap, item "Integracao completa com a UI" / Playlist real). Ate la, clicavel e
        // honesto sobre nao fazer nada ainda, igual ao OpenSettingsCommand.
        NextCommand = new RelayCommand(_ => _onStatus($"{Name}: avancar pra proxima midia da Playlist ainda nao implementado."));

        // Preview (mockup v8, notas #8/#10; reescrito na Sprint Preview-Isolado, 2026-08-15): usa a
        // MESMA area de video que ja existe neste card (a que o comentario original chamava de "tela
        // cheia deste Deck") — nao existe janela separada, decisao confirmada com o usuario. O que
        // mudou de verdade e o que acontece por tras: Preview agora e um metodo proprio (ver
        // Preview()), NAO chama mais Play() — nao alimenta o player de captura NDI, nao dispara o
        // callback onPlay (logo, nao pausa a Cartucheira, nao manda aviso UDP pro mesa, nao espelha
        // pro Deck 2) e usa uma placa de audio propria (PreviewAudioDeviceId), separada da placa
        // principal do Deck. E a distincao real "so conferir x ir ao ar" que o comentario antigo
        // dizia so fazer sentido quando o NDI existisse — o NDI existe desde a Sprint NDI, e agora o
        // Preview de fato fica de fora dele.
        PreviewCommand = new RelayCommand(_ => Preview(), _ => HasVideo && State != DeckPlaybackState.Playing);

        // Auto/Manual (mockup v8, nota #13): cada Deck decide se aceita receber o proximo item da
        // Playlist sozinho (Auto) ou so quando o operador carregar manualmente (Manual). O estado
        // e real e persiste (IsAutoMode), mas o comportamento de avanco automatico em si depende
        // de uma Playlist real tocando — ainda nao existe, entao a troca de modo e honesta sobre
        // isso ao avisar o operador.
        SetAutoModeCommand = new RelayCommand(_ => SetAutoMode(true));
        SetManualModeCommand = new RelayCommand(_ => SetAutoMode(false));
    }

    public string Name { get; }

    /// <summary>Estado do botao de loop (mockup v8: toggle, nao mais ComboBox) — espelha <see cref="Engine.PlaybackEngine.LoopEnabled"/>.</summary>
    public bool LoopEnabled
    {
        get => _loopEnabled;
        private set => SetProperty(ref _loopEnabled, value);
    }

    /// <summary>
    /// "Auto" (aceita o proximo item da Playlist sozinho) ou "Manual" (so por comando do
    /// operador) — ver comentario do construtor sobre o que isto ja faz de verdade hoje.
    /// </summary>
    public bool IsAutoMode
    {
        get => _isAutoMode;
        private set => SetProperty(ref _isAutoMode, value);
    }

    /// <summary>
    /// Objeto de dominio (arquitetura §12) que este ViewModel envolve de verdade desde a Sprint
    /// Config (2026-08-08) — guarda NdiChannelName, os OutputRoute de vMix/Resolume (host/porta/
    /// input/layer em Configuration) e AutomationActions. A janela unica de Configuracoes
    /// (Views.SettingsWindow, aberta pelo MainViewModel.OpenSettingsCommand) le e escreve neste
    /// objeto direto, via DeckSettingsViewModel/DeckSettingsPanel.
    /// </summary>
    public Deck DomainDeck { get; }

    /// <summary>
    /// Exibido como "pill" no cabecalho do Deck (mockup v8, nota #8) — reflete
    /// <see cref="DomainDeck"/>.OutputRoutes (vMix). Desde a Sprint Config, o setter e publico e de
    /// verdade: quem escreve aqui e a tela de Configuracao (engrenagem), nao mais um checkbox no
    /// proprio card do Deck. Continua sem conexao real (vMix Controller e um proximo item do
    /// roadmap) — "marcado" ainda significa so "este Deck deve ir ao ar por este destino quando a
    /// integracao existir", nao "conectado de verdade".
    /// </summary>
    public bool SendToVMix
    {
        get => GetOutputRoute(OutputRouteType.VMix).Enabled;
        set
        {
            var route = GetOutputRoute(OutputRouteType.VMix);
            if (route.Enabled == value)
            {
                return;
            }

            route.Enabled = value;
            OnPropertyChanged();
            RaiseOutputDependentPropertiesChanged();
        }
    }

    public bool SendToResolume
    {
        get => GetOutputRoute(OutputRouteType.Resolume).Enabled;
        set
        {
            var route = GetOutputRoute(OutputRouteType.Resolume);
            if (route.Enabled == value)
            {
                return;
            }

            route.Enabled = value;
            OnPropertyChanged();
            RaiseOutputDependentPropertiesChanged();
        }
    }

    public string LoadedFileName
    {
        get => _loadedFileName;
        private set
        {
            if (SetProperty(ref _loadedFileName, value))
            {
                OnPropertyChanged(nameof(HasVideo));
                ((RelayCommand)PlayCommand).RaiseCanExecuteChanged();
                ((RelayCommand)PreviewCommand).RaiseCanExecuteChanged();
                ((RelayCommand)ClearCommand).RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>
    /// Caminho completo do arquivo atualmente carregado (nulo se o Deck estiver vazio). Ajustes de
    /// Deck (2026-08-09): exposto publicamente pro MainViewModel ler no espelhamento Deck 1 → Deck 2
    /// (ao dar Play no Deck 1, o MainViewModel le este valor e carrega o mesmo arquivo no Deck 2,
    /// sem tocar).
    /// </summary>
    public string? LoadedFilePath => _loadedFilePath;

    public bool HasVideo => !string.IsNullOrWhiteSpace(LoadedFileName) && LoadedFileName != "Vazio";

    public DeckPlaybackState State
    {
        get => _state;
        private set
        {
            if (SetProperty(ref _state, value))
            {
                OnPropertyChanged(nameof(IsPlaying));
                OnPropertyChanged(nameof(IsPaused));
                OnPropertyChanged(nameof(IsError));
                OnPropertyChanged(nameof(StatusLabel));
                OnPropertyChanged(nameof(StatusColorHex));
                OnPropertyChanged(nameof(ShowStatusOverlay));
                RaiseOutputDependentPropertiesChanged();
                ((RelayCommand)PlayCommand).RaiseCanExecuteChanged();
                ((RelayCommand)PauseCommand).RaiseCanExecuteChanged();
                ((RelayCommand)StopCommand).RaiseCanExecuteChanged();
                ((RelayCommand)PreviewCommand).RaiseCanExecuteChanged();
            }
        }
    }

    public bool IsPlaying => State == DeckPlaybackState.Playing;

    public bool IsPaused => State == DeckPlaybackState.Paused;

    /// <summary>
    /// Regra definitiva da cor de borda do Deck (arquitetura visual §8, fechada em 2026-08-07):
    /// vermelho de ON AIR é reservado pro que está de fato saindo pra algum destino. vMix/Resolume
    /// ainda não têm integração real (essa vem nas próximas sprints do roadmap, itens 4 e 5) —
    /// usa-se o sinal que já existe e é honesto — os checkboxes "Envia para" — em vez de inventar
    /// um "conectado" falso. Quando a integração real existir, este mesmo sinal evolui de
    /// "marcado" pra "realmente conectado", sem mudar a regra de cor em si.
    /// </summary>
    public bool IsOnAir => State == DeckPlaybackState.Playing && (SendToVMix || SendToResolume);

    /// <summary>Tocando, mas sem nenhuma saída marcada — reproduzindo só localmente (borda azul).</summary>
    public bool IsLocalOnly => State == DeckPlaybackState.Playing && !SendToVMix && !SendToResolume;

    public bool IsError => State == DeckPlaybackState.Error;

    /// <summary>Esconde o selo de status (CUE/PAUSADO/etc.) sobre o vídeo assim que ele começa a tocar — o vídeo real ocupa o espaço.</summary>
    public bool ShowStatusOverlay => State != DeckPlaybackState.Playing;

    /// <summary>Rotulo do selo de status, no vocabulario da v7 ("Estados do Deck").</summary>
    public string StatusLabel => State switch
    {
        DeckPlaybackState.Cue => "CUE",
        DeckPlaybackState.Loading => "CARREGANDO",
        DeckPlaybackState.Ready => "PRONTO",
        DeckPlaybackState.Playing => "EM EXECUÇÃO",
        DeckPlaybackState.Paused => "PAUSADO",
        DeckPlaybackState.Seeking => "SEEKING",
        DeckPlaybackState.Buffering => "BUFFERING",
        DeckPlaybackState.Stopped => "PARADO",
        DeckPlaybackState.Finished => "FINISHED",
        DeckPlaybackState.Error => "ERRO",
        DeckPlaybackState.Offline => "OFFLINE",
        _ => "PRONTO"
    };

    /// <summary>
    /// Cor do texto de status dentro da área de preview — reaproveita o HexColorToBrushConverter já
    /// existente. Nota conhecida (mesmo padrão desde a Sprint 6): estes hex são duplicados dos
    /// valores de Resources/Theme.xaml, porque StatusColorHex é consumido via converter de string,
    /// não de DynamicResource — igual à exceção já documentada do GridLength de coluna na
    /// MainWindow. Mantidos manualmente em sincronia com Brush.Red/Amber/Blue/Green/TextFaint.
    /// Erro usa o mesmo vermelho de Playing aqui (o texto precisa ser legível sobre o fundo escuro
    /// da preview) — a distinção visual entre ON AIR e Erro é feita na borda do card (Brush.ErrorDark),
    /// não neste rótulo.
    /// </summary>
    public string StatusColorHex => State switch
    {
        DeckPlaybackState.Playing => "#FFD84545",
        DeckPlaybackState.Paused => "#FFE3B341",
        DeckPlaybackState.Seeking or DeckPlaybackState.Buffering or DeckPlaybackState.Loading => "#FF2D7DFF",
        DeckPlaybackState.Error => "#FFD84545",
        DeckPlaybackState.Offline => "#FF5C5C5C",
        DeckPlaybackState.Finished => "#FF2DBE60",
        _ => "#FF2DBE60"
    };

    public string CurrentTime
    {
        get => _currentTime;
        private set => SetProperty(ref _currentTime, value);
    }

    public string RemainingTime
    {
        get => _remainingTime;
        private set => SetProperty(ref _remainingTime, value);
    }

    public double Progress
    {
        get => _progress;
        private set => SetProperty(ref _progress, value);
    }

    /// <summary>Realce visual "solte aqui" durante um arraste do Explorer sobre este Deck.</summary>
    public bool IsDragOver
    {
        get => _isDragOver;
        set => SetProperty(ref _isDragOver, value);
    }

    /// <summary>
    /// MediaPlayer real do LibVLCSharp, exposto so pra hospedar o render de video na tela (o
    /// VideoView do LibVLCSharp.WPF, em MainWindow.xaml, se liga nele via binding).
    /// </summary>
    public MediaPlayer VideoPlayer => _engine.MediaPlayer;

    public ICommand PlayCommand { get; }

    public ICommand PauseCommand { get; }

    public ICommand StopCommand { get; }

    /// <summary>Esvazia o Deck de vez (ajustes de Deck, 2026-08-09) — ver <see cref="Clear"/>.</summary>
    public ICommand ClearCommand { get; }

    public ICommand ToggleLoopCommand { get; }

    public ICommand NextCommand { get; }

    public ICommand PreviewCommand { get; }

    public ICommand SetAutoModeCommand { get; }

    public ICommand SetManualModeCommand { get; }

    /// <summary>
    /// Associa um novo arquivo ao Deck. Sempre aceita, mesmo se o Deck estiver tocando (a regra
    /// de bloquear drop em Deck tocando fica para uma sprint futura — ver arquitetura §11).
    /// Carregar um arquivo novo sempre volta o Deck pra Cue. <paramref name="filePath"/> precisa
    /// ser o caminho completo — e o que o Engine usa pra abrir o arquivo de verdade.
    /// </summary>
    public void Load(string filePath)
    {
        ResetToCue();
        _isPreviewOnly = false;

        if (string.IsNullOrWhiteSpace(filePath))
        {
            _loadedFilePath = null;
            LoadedFileName = "Vazio";
            return;
        }

        _loadedFilePath = filePath;
        LoadedFileName = Path.GetFileName(filePath);
        _engine.Load(filePath);
    }

    /// <summary>
    /// Sprint Áudio-Dispositivo-Deck (2026-08-11): direciona o áudio local/preview deste Deck (o
    /// vídeo tocando na tela) pro dispositivo de áudio do Windows escolhido em Configurações — mesma
    /// ideia já usada pela Cartucheira (<see cref="AudioBankViewModel.SetOutputDevice"/>), aqui só
    /// repassando pro Engine (<see cref="Engine.PlaybackEngine.SetAudioOutputDevice"/>, que também
    /// documenta a limitação honesta de reverter pro padrão do sistema). Nunca afeta o áudio que já
    /// vai pro NDI — caminho totalmente separado.
    /// </summary>
    public void SetAudioOutputDevice(string? deviceId) => _engine.SetAudioOutputDevice(deviceId);

    private void Play()
    {
        if (!HasVideo)
        {
            return;
        }

        _isPreviewOnly = false;

        // Reafirma a placa PRINCIPAL antes de tocar (Sprint Preview-Isolado, 2026-08-15) — garante
        // que um Play real nunca "herda" a placa configurada pro Preview, mesmo se um Preview tiver
        // rodado neste Deck um instante antes. Necessario porque o LibVLC nao garante um jeito de
        // "reverter" pro dispositivo padrao sozinho (mesma limitacao ja documentada em
        // Engine.PlaybackEngine.SetAudioOutputDevice) — reafirmar explicitamente a cada Play/Preview
        // e o jeito seguro e ja confirmado contra a API real.
        _engine.SetAudioOutputDevice(DomainDeck.AudioDeviceId);

        _audioNdiDiagnosticShown = false;
        _engine.Play();
        _onPlay(this);
    }

    /// <summary>
    /// Sprint Preview-Isolado (2026-08-15): conferir um arquivo carregado sem nada sair da maquina —
    /// toca SO no player de tela (<see cref="Engine.PlaybackEngine.PlayPreviewOnly"/>, nao alimenta o
    /// NDI), usa a placa de audio propria do Preview (<see cref="Core.Deck.PreviewAudioDeviceId"/>,
    /// separada da placa principal) e NUNCA chama <see cref="_onPlay"/> — logo nunca pausa a
    /// Cartucheira, nunca manda aviso UDP pro mesa, nunca espelha pro Deck 2 (tudo isso mora dentro
    /// do callback onPlay do MainViewModel). <see cref="_isPreviewOnly"/> registra que esta sessao
    /// foi assim, pra <see cref="Stop"/>/<see cref="Clear"/>/<see cref="OnEngineMediaEnded"/> saberem
    /// que tambem nao devem notificar nada quando ela acabar.
    /// </summary>
    private void Preview()
    {
        if (!HasVideo)
        {
            return;
        }

        _isPreviewOnly = true;
        _engine.SetAudioOutputDevice(DomainDeck.PreviewAudioDeviceId);
        _audioNdiDiagnosticShown = false;
        _engine.PlayPreviewOnly();
        _onStatus($"{Name}: preview de \"{LoadedFileName}\" (local — sem NDI, sem comandos).");
    }

    private void Pause()
    {
        _engine.Pause();
        _onPause(this);
    }

    private void Stop()
    {
        var wasPreviewOnly = _isPreviewOnly;
        _isPreviewOnly = false;

        _engine.Stop();

        if (wasPreviewOnly)
        {
            // Sprint Preview-Isolado (2026-08-15): uma sessao que nunca foi "ao vivo" (Preview) nao
            // pode terminar mandando avisos de "ao vivo terminou" — ver _onStop em OnDeckStop
            // (MainViewModel), que retoma a Cartucheira e manda o aviso UDP de Fim pra mesa.
            _onStatus($"{Name}: preview parado.");
        }
        else
        {
            _onStop(this);
        }
    }

    /// <summary>
    /// Esvazia o Deck de vez (ajustes de Deck, 2026-08-09) — diferente de <see cref="Stop"/> (que
    /// mantem a midia carregada, so volta a posicao pro inicio), Clear descarrega a midia do Engine
    /// tambem (ver <see cref="Engine.PlaybackEngine.Clear"/>) e volta o Deck ao estado vazio, igual
    /// a como ele comeca antes de qualquer Load.
    ///
    /// Sprint Deck-pausa-Cartucheira (2026-08-11): tambem chama <see cref="_onStop"/> — limpar o
    /// Deck tambem significa "com certeza nao esta mais tocando", entao precisa devolver a
    /// Cartucheira que este Deck estivesse segurando pausada, senao ela ficaria presa pausada pra
    /// sempre (Clear nunca dispara MediaEnded nem Stop do proprio Engine).
    ///
    /// Sprint Preview-Isolado (2026-08-15): mesma ressalva de <see cref="Stop"/> — se a sessao atual
    /// era so um Preview (<see cref="_isPreviewOnly"/>), nunca chegou a pausar a Cartucheira nem
    /// mandar aviso UDP nenhum, entao Clear tambem nao deve chamar <see cref="_onStop"/> nesse caso.
    /// </summary>
    private void Clear()
    {
        var wasPreviewOnly = _isPreviewOnly;

        _engine.Clear();
        _loadedFilePath = null;
        LoadedFileName = "Vazio";
        ResetToCue();
        _isPreviewOnly = false;

        if (!wasPreviewOnly)
        {
            _onStop(this);
        }

        _onStatus($"{Name}: limpo.");
    }

    private void ToggleLoop()
    {
        LoopEnabled = !LoopEnabled;
        _engine.LoopEnabled = LoopEnabled;
    }

    private void SetAutoMode(bool autoMode)
    {
        if (IsAutoMode == autoMode)
        {
            return;
        }

        IsAutoMode = autoMode;
        _onStatus(autoMode
            ? $"{Name} em modo Auto — avanço automático pela Playlist ainda não implementado (entra numa sprint futura)."
            : $"{Name} em modo Manual.");
    }

    /// <summary>Volta o Deck pro estado Cue e reseta os indicadores de tempo/progresso — usado por Load e pelo evento Stopped do Engine.</summary>
    private void ResetToCue()
    {
        State = DeckPlaybackState.Cue;
        Progress = 0;
        CurrentTime = "00:00";
        RemainingTime = "--:--";
    }

    /// <summary>Notifica IsOnAir/IsLocalOnly sempre que State, SendToVMix ou SendToResolume mudam — são todos derivados dos três.</summary>
    private void RaiseOutputDependentPropertiesChanged()
    {
        OnPropertyChanged(nameof(IsOnAir));
        OnPropertyChanged(nameof(IsLocalOnly));
    }

    /// <summary>
    /// Acha o OutputRoute de um tipo dentro de DomainDeck.OutputRoutes. O construtor sempre cria
    /// vMix e Resolume (unicos dois tipos consultados hoje — ver SendToVMix/SendToResolume), entao
    /// isto nunca deveria lançar em uso normal; InvalidOperationException aqui seria sinal de bug
    /// de inicializacao, nao de estado esperado do dominio.
    /// </summary>
    private OutputRoute GetOutputRoute(OutputRouteType type) =>
        DomainDeck.OutputRoutes.FirstOrDefault(r => r.OutputType == type)
            ?? throw new InvalidOperationException($"DomainDeck de {Name} nao tem OutputRoute do tipo {type}.");

    /// <summary>
    /// Handlers de evento do Engine disparam numa thread interna do LibVLC — todo acesso a
    /// propriedades observaveis (que os bindings do WPF leem na UI thread) precisa ser marshalado
    /// de volta pra UI thread primeiro.
    /// </summary>
    private static void RunOnUiThread(Action action)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            dispatcher.BeginInvoke(action);
        }
    }

    private void OnEngineStateChanged(object? sender, Engine.EngineStateChangedEventArgs e)
    {
        switch (e.State)
        {
            case Engine.EnginePlaybackState.Playing:
                RunOnUiThread(() => State = DeckPlaybackState.Playing);
                break;
            case Engine.EnginePlaybackState.Paused:
                RunOnUiThread(() => State = DeckPlaybackState.Paused);
                break;
            case Engine.EnginePlaybackState.Stopped:
                RunOnUiThread(ResetToCue);
                break;
            case Engine.EnginePlaybackState.Ended:
                // Tratado por OnEngineMediaEnded quando nao ha loop — aqui so cobre o caso de loop
                // ativo, onde o Engine ja reinicia sozinho e volta a disparar Playing em seguida.
                break;
        }
    }

    private void OnEnginePositionChanged(object? sender, Engine.EnginePositionChangedEventArgs e)
    {
        RunOnUiThread(() =>
        {
            CurrentTime = e.Position.ToString(@"mm\:ss");

            if (e.Duration is { } duration && duration > TimeSpan.Zero)
            {
                var remaining = duration - e.Position;
                RemainingTime = (remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero).ToString(@"mm\:ss");
                Progress = Math.Clamp(e.Position.TotalMilliseconds / duration.TotalMilliseconds * 100, 0, 100);
            }
            else
            {
                RemainingTime = "--:--";
            }
        });
    }

    private void OnEngineMediaEnded(object? sender, EventArgs e)
    {
        RunOnUiThread(() =>
        {
            State = DeckPlaybackState.Finished;

            // Sprint Preview-Isolado (2026-08-15): mesma ressalva de Stop/Clear — um Preview que
            // termina sozinho (fim natural do arquivo, sem loop) tambem nao pode retomar a
            // Cartucheira nem mandar aviso UDP de Fim, ja que nunca "entrou no ar" pra comecar.
            var wasPreviewOnly = _isPreviewOnly;
            _isPreviewOnly = false;

            if (wasPreviewOnly)
            {
                _onStatus($"{Name}: preview terminou.");
            }
            else
            {
                _onMediaEnded(this);
            }
        });
    }

    private void OnEngineError(object? sender, string message)
    {
        RunOnUiThread(() =>
        {
            State = DeckPlaybackState.Error;
            _onError(this, message);
        });
    }

    /// <summary>
    /// Repassa o quadro direto pro NdiSender, na MESMA thread do LibVLC que o disparou (SEM
    /// RunOnUiThread) — decisao deliberada: NdiSender.SendFrame nunca toca em estado ligado a
    /// binding do WPF, e marshalar pra UI thread aqui so acrescentaria latencia sem necessidade,
    /// contra a prioridade #1 do projeto. NDIlib_send_send_video_v2 (variante nao-async usada pelo
    /// NdiSender) copia o buffer antes de retornar, entao e seguro o Engine reusar o buffer fixo
    /// assim que este handler retornar.
    /// </summary>
    private void OnEngineFrameReady(object? sender, Engine.EngineFrameReadyEventArgs e)
    {
        _ndiSender?.SendFrame(e.Data, e.Width, e.Height, e.StrideBytes);
    }

    /// <summary>
    /// Sprint NDI-audio (2026-08-11): repassa o bloco de audio direto pro NdiSender, mesma decisao
    /// deliberada de <see cref="OnEngineFrameReady"/> (sem RunOnUiThread — latencia). A funcao nativa
    /// da NDI SDK usada por <c>NdiSender.SendAudioFrame</c> tambem copia o buffer antes de retornar,
    /// entao e seguro este handler retornar assim que a chamada terminar.
    ///
    /// Diagnostico pos-crash (2026-08-11, mesmo dia): a UNICA excecao a "sem RunOnUiThread" acima —
    /// na primeira vez que este handler roda depois de um <see cref="Play"/>, despacha (assincrono,
    /// <see cref="Dispatcher.BeginInvoke(Delegate)"/>, nunca bloqueia esta thread do LibVLC) uma
    /// mensagem de status confirmando que o pipeline chegou ate aqui, com os numeros reais recebidos
    /// — usuario reportou NDI so com video mesmo depois do audio parar de travar o programa; isto
    /// existe pra confirmar (ou descartar) se o bloco de audio esta de fato saindo do Engine, sem
    /// precisar adivinhar. <see cref="_audioNdiDiagnosticShown"/> garante que isso so acontece UMA
    /// vez por Play (nao a cada bloco — dezenas por segundo), entao o custo do despacho assincrono e
    /// desprezivel mesmo rodando no caminho quente.
    /// </summary>
    private void OnEngineAudioFrameReady(object? sender, Engine.EngineAudioFrameReadyEventArgs e)
    {
        if (!_audioNdiDiagnosticShown)
        {
            _audioNdiDiagnosticShown = true;
            var name = Name;
            var channels = e.Channels;
            var sampleRate = e.SampleRate;
            var sampleCount = e.SampleCount;
            Application.Current?.Dispatcher.BeginInvoke(() =>
                _onStatus($"{name}: audio NDI recebido do Engine ({channels}ch, {sampleRate}Hz, bloco de {sampleCount} amostras/canal)."));
        }

        _ndiSender?.SendAudioFrame(e.Data, e.SampleRate, e.Channels, e.SampleCount);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _engine.StateChanged -= OnEngineStateChanged;
        _engine.PositionChanged -= OnEnginePositionChanged;
        _engine.MediaEnded -= OnEngineMediaEnded;
        _engine.ErrorOccurred -= OnEngineError;

        if (_ndiSender is not null)
        {
            _engine.FrameReady -= OnEngineFrameReady;
            _engine.AudioFrameReady -= OnEngineAudioFrameReady;
        }

        _engine.Dispose();
        _ndiSender?.Dispose();
    }
}
