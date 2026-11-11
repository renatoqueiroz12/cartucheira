using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CartucheiraVideo.Configuration;
using CartucheiraVideo.Core;
using CartucheiraVideo.Infrastructure.Livewire;
using CartucheiraVideo.Ndi;
using CartucheiraVideo.Presentation.Mvvm;
using CartucheiraVideo.Presentation.Views;
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
///
/// Sprint Livewire-4 (2026-09-14): <see cref="Core.Deck.AudioDeviceId"/> ("Dispositivo de áudio
/// (Play)" em Configurações) passa a aceitar, além de um dispositivo Windows de verdade, uma Saída
/// Livewire NATIVA da Cartucheira (valor prefixado <c>"livewire:{Id}"</c>, mesma convenção já usada
/// por <see cref="AudioBankViewModel.SetOutputDevice"/> pra Vinhetas/Trilhas) — pedido direto do
/// usuário depois que ligar o Firewall (Sprint NDI-Firewall-Livewire-fix-4, v1.0.27) quebrou o
/// caminho antigo (Windows tocando pelo driver WDM da Axia IP-Driver, que o hardware Axia converte
/// em Livewire de verdade por fora do programa). Quando essa opção é escolhida,
/// <see cref="SetAudioOutputDevice"/> NÃO manda o texto prefixado pro LibVLC (que não saberia o que
/// fazer com ele) — em vez disso abre/mantém um <see cref="LivewireSender"/> próprio deste Deck
/// (<see cref="UpdateLivewireSender"/>) e passa a alimentá-lo, a cada bloco de áudio que o Engine já
/// decodifica de forma independente pro NDI (<see cref="Engine.PlaybackEngine.AudioFrameReady"/>,
/// mesmo pipeline NAudio da Sprint NDI-audio — nunca o áudio interno do LibVLC/MediaPlayer, que
/// continua controlando só o monitor local deste computador). Ver
/// <see cref="ForwardAudioToLivewire"/> pro reempacotamento de tamanho de bloco.
///
/// Sprint Arte-de-Fundo (2026-11-10): quando <see cref="Core.Deck.UseBackgroundArt"/> estiver
/// ligado (aba Resolume da Configuração deste Deck) e <see cref="State"/> estiver "parado"
/// (Cue/Stopped/Finished — ver <see cref="IsBackgroundArtIdleState"/>), a saída NDI deste Deck
/// passa a mandar continuamente a arte de fundo ATIVA (Configurações → Arte de Fundo,
/// <see cref="BackgroundArtRenderer"/>) em vez de simplesmente parar de mandar quadro nenhum
/// (comportamento de sempre antes desta sprint, ainda usado quando o toggle está desligado ou
/// nenhuma arte resolve/carrega). Pedido do usuário: o Deck que vai pro Resolume/telões (hoje, o
/// Deck 2 — o Deck 1 vai pro vMix) deve mostrar uma arte de espera entre um VT e outro, em vez de
/// ficar com o último quadro congelado ou sem sinal. Mecanismo é um <see cref="Timer"/> simples
/// (não <see cref="System.Windows.Threading.DispatcherTimer"/> — mesmo motivo de
/// <see cref="OnEngineFrameReady"/> já não usar a UI thread: <see cref="Ndi.NdiSender.SendFrame"/>
/// já é seguro fora dela) reenviando o MESMO buffer nativo a cada tick — ver
/// <see cref="UpdateBackgroundArtIdleSender"/> pro porquê de um buffer só (sem ping-pong) ser
/// seguro aqui, diferente do vídeo real.
///
/// Sprint Telas-Saida-Direta (2026-11-10): quando <see cref="Core.Deck.SendToScreens"/> estiver
/// ligado (aba Telas da Configuração deste Deck), o vídeo (e, quando ocioso, a MESMA arte de fundo
/// ativa acima) também é desenhado direto numa <see cref="Views.ScreenOutputWindow"/> (sem borda,
/// sempre no topo) por monitor do Windows escolhido em <see cref="Core.Deck.ScreenOutputDeviceNames"/>
/// — pedido do usuário: ele ligou uma placa de vídeo com várias saídas neste PC e quer mandar o
/// Deck 2 direto pros telões conectados nela, sem depender do Resolume. Reaproveita o MESMO buffer
/// UYVY já usado pro NDI (nunca um segundo <see cref="LibVLC"/>/<see cref="MediaPlayer"/>
/// independente) — decisão deliberada pra não repetir a história desta base de bugs sérios de
/// sincronismo entre players independentes (drift, trava de clock de áudio/vídeo, etc.); a única
/// conversão nova é UYVY→BGRA32 (<see cref="UyvyBgraConverter"/>), feita na mesma thread de callback
/// do Engine que já alimenta o NDI (<see cref="ForwardFrameToScreens"/>) — só o <c>WritePixels</c>
/// final do <see cref="System.Windows.Media.Imaging.WriteableBitmap"/> compartilhado precisa da UI
/// thread (afinidade de thread), via <see cref="Dispatcher.BeginInvoke(Delegate)"/> com descarte de
/// quadro se a UI thread estiver ocupada (<see cref="_screenFrameInFlight"/>). Deliberadamente
/// desacoplado da saída NDI estar disponível (<see cref="_ndiSender"/> pode ser nulo) — ver
/// comentário do construtor sobre <c>_engine.FrameReady</c> ter saído do try/catch do NDI.
/// </summary>
public sealed class DeckViewModel : ViewModelBase, IDisposable
{
    private readonly Action<DeckViewModel> _onPlay;
    private readonly Action<DeckViewModel> _onPause;
    private readonly Action<DeckViewModel> _onStop;
    private readonly Action<DeckViewModel> _onMediaEnded;
    private readonly Action<DeckViewModel> _onPreview;
    private readonly Action<DeckViewModel> _onPreviewEnd;
    private readonly Action<DeckViewModel, string> _onError;
    private readonly Action<string> _onStatus;
    private readonly Engine.PlaybackEngine _engine;
    private readonly NdiSender? _ndiSender;
    private readonly Func<string, LivewireOutputSettings?> _resolveLivewireOutput;
    private readonly Func<string?> _getLivewireNetworkInterfaceIp;
    private readonly Func<string?> _getActiveBackgroundArtPath;
    private LivewireSender? _livewireSender;
    private string? _livewireOutputId;
    private string? _livewireNetworkInterfaceIp;

    /// <summary>
    /// Sprint Arte-de-Fundo (2026-11-10): mecanismo de "quadro ocioso" — ver
    /// <see cref="UpdateBackgroundArtIdleSender"/> pra condição completa de quando isto liga.
    /// Buffer nativo ÚNICO (não ping-pong como <see cref="Engine.PlaybackEngine"/> usa pro vídeo
    /// real) — decisão deliberada: o conteúdo nunca muda (é uma imagem estática, sempre os mesmos
    /// bytes a cada envio), então não existe o risco de sobrescrever um buffer que a NDI SDK ainda
    /// esteja lendo de um envio assíncrono anterior (esse risco só existe quando o CONTEÚDO muda
    /// quadro a quadro, caso do vídeo real — ver comentário de <see cref="OnEngineFrameReady"/>).
    /// <see cref="_backgroundArtGate"/> protege só a troca do buffer (<see cref="ReplaceBackgroundArtBuffer"/>/
    /// <see cref="StopBackgroundArtTimer"/>, sempre na UI thread) contra o próprio
    /// <see cref="System.Threading.Timer"/> (thread do ThreadPool) tentando ler o ponteiro no meio
    /// de uma troca.
    /// </summary>
    private readonly object _backgroundArtGate = new();
    private Timer? _backgroundArtTimer;
    private IntPtr _backgroundArtBuffer = IntPtr.Zero;
    private volatile bool _backgroundArtSending;

    /// <summary>
    /// Sprint Telas-Saida-Direta (2026-11-10): uma <see cref="ScreenOutputWindow"/> aberta por
    /// monitor atualmente selecionado em <see cref="Core.Deck.ScreenOutputDeviceNames"/> (só quando
    /// <see cref="Core.Deck.SendToScreens"/> está ligado) — ver <see cref="RefreshScreenOutputs"/>
    /// pra como esta lista é (re)montada. Todas compartilham o MESMO <see cref="_screenOutputBitmap"/>
    /// — escrever nele uma vez atualiza todas as telas ao mesmo tempo, sem laço nenhum aqui.
    /// </summary>
    private readonly List<ScreenOutputWindow> _screenOutputWindows = new();

    /// <summary>
    /// Bitmap ÚNICO (1920x1080, Bgra32), criado sob demanda na UI thread, compartilhado por todas
    /// as <see cref="_screenOutputWindows"/> deste Deck — mesmo canvas fixo que
    /// <see cref="BackgroundArtRenderer"/> já usa. <see cref="WriteableBitmap"/> tem afinidade de
    /// thread (só pode ser escrito pela thread que o criou); por isso só é criado/escrito dentro de
    /// <see cref="Dispatcher.BeginInvoke(Delegate)"/> (sempre a UI thread), nunca direto na thread do
    /// callback do Engine.
    /// </summary>
    private WriteableBitmap? _screenOutputBitmap;

    /// <summary>Buffer BGRA32 reaproveitado a cada quadro por <see cref="UyvyBgraConverter.ConvertToBgra32"/>, pra não alocar nada por quadro no caminho quente de <see cref="OnEngineFrameReady"/>.</summary>
    private byte[]? _screenOutputBgraBuffer;

    /// <summary>Cópia administrada reaproveitada do buffer nativo (<see cref="Engine.EngineFrameReadyEventArgs.Data"/>, um <see cref="IntPtr"/>) de cada quadro — precisa existir porque <see cref="UyvyBgraConverter.ConvertToBgra32"/> trabalha sobre <c>byte[]</c>, nunca indexando um ponteiro nativo diretamente (lento demais chamado por quadro).</summary>
    private byte[]? _screenOutputUyvyBuffer;

    /// <summary>
    /// Guarda de "descarte de quadro" — verdadeiro enquanto um <see cref="Dispatcher.BeginInvoke(Delegate)"/>
    /// anterior ainda não rodou na UI thread. Se a UI thread estiver ocupada/lenta, o próximo quadro
    /// do callback do Engine simplesmente NÃO despacha (em vez de empilhar despachos sem limite) —
    /// mesmo espírito de "nunca deixar uma fila crescer sem limite" já preocupação documentada deste
    /// projeto (ver pendência histórica "Fila de áudio NDI saturada" em versao-atual.md).
    /// </summary>
    private volatile bool _screenFrameInFlight;

    private string _loadedFileName = "Vazio";
    private string? _loadedFilePath;
    private DeckPlaybackState _state = DeckPlaybackState.Cue;
    private double _progress;
    private string _currentTime = "00:00";

    /// <summary>
    /// Sprint Deck-Seek-Bar-Fix (2026-09-15): verdadeiro enquanto o operador estiver com o botão do
    /// mouse pressionado sobre a barra de execução (<see cref="BeginSeekDrag"/>/
    /// <see cref="EndSeekDrag"/>, chamados pelo code-behind em MainWindow.xaml.cs). Usado só por
    /// <see cref="OnEnginePositionChanged"/> pra CONGELAR Progress/CurrentTime/RemainingTime enquanto
    /// dura o arraste — causa raiz real do bug reportado ("a barra não deixa voltar"): o Value da
    /// barra é ligado (Mode=OneWay) direto em Progress, e um tick real de posição chegando NO MEIO
    /// do arraste sobrescrevia a posição que o operador estava escolhendo com a posição real de
    /// reprodução (que só anda pra frente) — empurrando a barra de volta pra frente e brigando com
    /// qualquer arrasto pra TRÁS especificamente (um arrasto pra frente era mascarado pelo mesmo
    /// empurrão, por isso o sintoma só aparecia indo pra trás).
    /// </summary>
    private bool _isSeekDragging;
    private string _remainingTime = "--:--";

    /// <summary>
    /// Sprint Deck-Seek-Bar (2026-09-12): duracao total conhecida do arquivo carregado — vem do
    /// MESMO <see cref="Engine.EnginePositionChangedEventArgs.Duration"/> que ja alimentava
    /// <see cref="RemainingTime"/>, so que guardada aqui tambem (nao existia como campo antes desta
    /// sprint) pra <see cref="SeekToProgress"/> poder converter um percentual (0-100, vindo do
    /// controle deslizante da Presentation) de volta pra um <see cref="TimeSpan"/> concreto sem
    /// precisar esperar o proximo evento do Engine. Nula enquanto a duracao real nao for conhecida
    /// (arquivo ainda carregando, ou sem duracao valida) — mesmo estado "desconhecido" que
    /// <see cref="RemainingTime"/> ja tratava como "--:--".
    /// </summary>
    private TimeSpan? _duration;

    private string _durationDisplay = "--:--";
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

    /// <summary>
    /// Sprint Áudio-Livewire-Duplo-Fix (2026-09-15): guarda o último valor que ESTE ViewModel mandou
    /// pra <see cref="Engine.PlaybackEngine.SetLocalAudioMuted"/>, pra <see cref="SetAudioOutputDevice"/>
    /// e <see cref="Preview"/> só chamarem o Engine de novo quando o valor REALMENTE muda — nunca a
    /// cada Play/Preview redundantemente. Motivo: o usuário reportou que o áudio local do Preview
    /// passou a engasgar (corta e volta, umas 4 vezes) logo no começo depois da correção do mudo
    /// (v1.0.30) — suspeita forte é que chamar <c>MediaPlayer.Mute = false</c> (mesmo quando já era
    /// false, um no-op do ponto de vista do valor) bem no instante em que o LibVLC ainda está
    /// inicializando o "aout" (mesma janela de instabilidade já documentada em
    /// <see cref="Engine.PlaybackEngine.SetAudioOutputDevice"/>, pesquisa externa do issue #1119 do
    /// vlcj) pode empurrar o subsistema de áudio a reconfigurar/reabrir o fluxo, causando esse
    /// engasgo — mesmo sem NENHUMA mudança real de valor. Reduzir as chamadas ao estritamente
    /// necessário (só quando o mudo precisa mesmo mudar) é a correção mais segura e conservadora,
    /// sem tocar em nada mais do mecanismo já existente. Começa em falso (estado real do LibVLC antes
    /// de qualquer Play/Preview desta instância).
    /// </summary>
    private bool _localAudioMuted;

    /// <summary>
    /// Sprint Livewire-4 (2026-09-14): <paramref name="resolveLivewireOutput"/> e
    /// <paramref name="getLivewireNetworkInterfaceIp"/> são novos — o MainViewModel os passa (mesmas
    /// duas funções já injetadas em cada <see cref="AudioBankViewModel"/>) pra este Deck poder
    /// resolver uma Saída Livewire selecionada sem precisar conhecer a lista completa configurada em
    /// Configurações nem a interface de rede escolhida; ver <see cref="UpdateLivewireSender"/>.
    ///
    /// Sprint Arte-de-Fundo (2026-11-10): <paramref name="getActiveBackgroundArtPath"/> é novo,
    /// mesmo padrão dos dois acima (função injetada pelo MainViewModel, lê sempre a configuração
    /// persistida mais recente) — resolve o caminho do arquivo da arte de fundo ATIVA agora
    /// (Configurações → Arte de Fundo), ou nulo se nenhuma estiver ativa. Ver
    /// <see cref="UpdateBackgroundArtIdleSender"/>/<see cref="RefreshBackgroundArt"/>.
    /// </summary>
    public DeckViewModel(
        string name,
        LibVLC libVlc,
        Action<DeckViewModel> onPlay,
        Action<DeckViewModel> onPause,
        Action<DeckViewModel> onStop,
        Action<DeckViewModel> onMediaEnded,
        Action<DeckViewModel, string> onError,
        Action<string> onStatus,
        Func<string, LivewireOutputSettings?> resolveLivewireOutput,
        Func<string?> getLivewireNetworkInterfaceIp,
        Func<string?> getActiveBackgroundArtPath,
        Action<DeckViewModel>? onPreview = null,
        Action<DeckViewModel>? onPreviewEnd = null)
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

        // Sprint Áudio-Unificado (2026-09-15): onPreview/onPreviewEnd são opcionais (default no-op)
        // pra não quebrar nenhum outro lugar que ainda construa um DeckViewModel sem eles — o
        // MainViewModel de verdade sempre passa os dois (ver construtor), pra mandar os avisos UDP
        // novos de "Preview iniciou"/"Preview terminou" pra mesa/console externa.
        _onPreview = onPreview ?? (_ => { });
        _onPreviewEnd = onPreviewEnd ?? (_ => { });
        _resolveLivewireOutput = resolveLivewireOutput ?? throw new ArgumentNullException(nameof(resolveLivewireOutput));
        _getLivewireNetworkInterfaceIp = getLivewireNetworkInterfaceIp ?? throw new ArgumentNullException(nameof(getLivewireNetworkInterfaceIp));
        _getActiveBackgroundArtPath = getActiveBackgroundArtPath ?? throw new ArgumentNullException(nameof(getActiveBackgroundArtPath));

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

        // Sprint NDI-audio (2026-08-11): mesmo canal NDI, agora tambem carregando audio — mesma
        // ligacao direta (sem RunOnUiThread) do FrameReady acima, pelo mesmo motivo de latencia.
        //
        // Sprint Livewire-4 (2026-09-14): esta assinatura SAIU do try/catch acima de proposito —
        // AudioFrameReady agora tem DOIS consumidores possiveis dentro de OnEngineAudioFrameReady, o
        // NdiSender (so quando NDI estiver disponivel, ver acima) e o LivewireSender deste Deck (so
        // quando uma Saida Livewire estiver selecionada, ver UpdateLivewireSender) — os dois ja sao
        // null-safe sozinhos ali dentro. Antes desta sprint, uma maquina sem NDI instalado tambem
        // ficava sem Livewire nativo no Deck, sem nenhum aviso honesto sobre o motivo — bug real
        // corrigido junto com esta sprint, nao so uma limpeza cosmetica.
        _engine.AudioFrameReady += OnEngineAudioFrameReady;

        // Sprint Telas-Saida-Direta (2026-11-10): FrameReady (video) SAIU do try/catch de NDI acima
        // de proposito, mesmo motivo exato da correcao do AudioFrameReady na Sprint Livewire-4 (ver
        // comentario acima) — agora OnEngineFrameReady tambem tem DOIS consumidores possiveis
        // (_ndiSender?.SendFrame, so quando NDI estiver disponivel, e ForwardFrameToScreens, so
        // quando este Deck tiver SendToScreens ligado — os dois ja sao null-safe/condicionais
        // sozinhos ali dentro). Sem esta mudanca, uma maquina SEM NDI instalado tambem ficaria sem
        // conseguir mandar video pras Telas — justamente o cenario que o usuario descreveu querer
        // ("nao vou mais mandar pro resolume, vou mandar pra essas telas"), que não deveria depender
        // da NDI SDK estar instalada pra funcionar.
        _engine.FrameReady += OnEngineFrameReady;

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

        // Preview (mockup v8, notas #8/#10; reescrito na Sprint Preview-Isolado, 2026-08-15, e de
        // novo na Sprint Áudio-Unificado, 2026-09-15): usa a MESMA area de video que ja existe neste
        // card (a que o comentario original chamava de "tela cheia deste Deck") — nao existe janela
        // separada, decisao confirmada com o usuario. O que mudou de verdade e o que acontece por
        // tras: Preview agora e um metodo proprio (ver Preview()), NAO chama mais Play() — nao
        // alimenta o player de captura NDI, nao dispara o callback onPlay (logo, nao pausa a
        // Cartucheira, nao espelha pro Deck 2). Desde a Sprint Áudio-Unificado, Preview usa a MESMA
        // placa de audio do Play (o campo separado PreviewAudioDeviceId foi removido) e manda seu
        // proprio aviso UDP dedicado (onPreview/onPreviewEnd) pra mesa/console externa. E a distincao
        // real "so conferir x ir ao ar" que o comentario antigo dizia so fazer sentido quando o NDI
        // existisse — o NDI existe desde a Sprint NDI, e o Preview continua de fato fora dele.
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
                UpdateBackgroundArtIdleSender();
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

    /// <summary>
    /// Sprint Deck-Seek-Bar (2026-09-12): duracao TOTAL do arquivo carregado, formatada "mm:ss" —
    /// "--:--" enquanto desconhecida (mesmo padrao ja usado por <see cref="CurrentTime"/>/
    /// <see cref="RemainingTime"/>). Usuario pediu explicitamente pra "mostrar tambem o timeframe"
    /// junto com a barra de avanco nova — antes desta sprint so existia tempo decorrido e tempo
    /// restante, nunca a duracao total do arquivo.
    /// </summary>
    public string DurationDisplay
    {
        get => _durationDisplay;
        private set => SetProperty(ref _durationDisplay, value);
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

        // Sprint NDI-Frame-Rate-Real (2026-09-16): repassa o frame rate REAL detectado pelo Engine
        // (ComputeLetterboxSize, mesmo parse ja usado pra resolucao/orientacao) pro NdiSender deste
        // Deck — antes desta sprint, TODO Load mandava pro NDI o metadado fixo 30000/1000 (30,000fps
        // exatos), errado pra qualquer arquivo que nao fosse exatamente isso (a maioria do conteudo
        // de transmissao real e 29,97/59,94fps). Sem efeito se este Deck nao tiver NdiSender (NDI
        // indisponivel nesta maquina, ver construtor).
        var (frameRateNum, frameRateDen) = _engine.VideoFrameRate;
        _ndiSender?.SetFrameRate(frameRateNum, frameRateDen);
    }

    /// <summary>
    /// Sprint Áudio-Dispositivo-Deck (2026-08-11): direciona o áudio local/preview deste Deck (o
    /// vídeo tocando na tela) pro dispositivo de áudio do Windows escolhido em Configurações — mesma
    /// ideia já usada pela Cartucheira (<see cref="AudioBankViewModel.SetOutputDevice"/>), aqui só
    /// repassando pro Engine (<see cref="Engine.PlaybackEngine.SetAudioOutputDevice"/>, que também
    /// documenta a limitação honesta de reverter pro padrão do sistema). Nunca afeta o áudio que já
    /// vai pro NDI — caminho totalmente separado.
    ///
    /// Sprint Livewire-4 (2026-09-14): <paramref name="deviceId"/> agora também aceita o valor
    /// prefixado <c>"livewire:{Id}"</c> de uma Saída Livewire nativa (ver comentário de classe) — esse
    /// caso NUNCA chega no LibVLC (<see cref="Engine.PlaybackEngine.SetAudioOutputDevice"/> não sabe
    /// interpretar o prefixo, e mandar pra lá arriscaria um comportamento não documentado da libvlc
    /// com um id de dispositivo inexistente); em vez disso só abre/atualiza
    /// <see cref="_livewireSender"/> via <see cref="UpdateLivewireSender"/>. Trocar de uma Saída
    /// Livewire DE VOLTA pra um dispositivo Windows (ou "Padrão do sistema") tem a mesma limitação
    /// honesta já documentada acima — só some de verdade reiniciando o programa, já que o pedido
    /// Windows anterior (se houver) nunca chegou a ser desfeito no LibVLC.
    /// </summary>
    public void SetAudioOutputDevice(string? deviceId)
    {
        var isLivewire = deviceId is not null && deviceId.StartsWith(Engine.AudioEngine.ExternalOutputPrefix, StringComparison.Ordinal);

        UpdateLivewireSender(isLivewire ? deviceId : null);

        // Sprint Áudio-Livewire-Duplo (2026-09-15): quando a saída escolhida é uma Saída Livewire
        // nativa, o monitor local (alto-falantes/placa da própria máquina) precisa ficar MUDO — sem
        // isto, o operador ouve a MESMA reprodução duas vezes ao mesmo tempo: uma vez pelos
        // alto-falantes locais (o LibVLC nunca fica sabendo que devia ficar quieto, já que um id de
        // Saída Livewire nunca chega até ele, ver bloco abaixo) e outra pela mesa/console via
        // Livewire — o áudio de verdade, que deveria ser o único caminho. Ver
        // Engine.PlaybackEngine.SetLocalAudioMuted pro diagnóstico completo. Voltar pra um
        // dispositivo Windows normal (isLivewire = false) desliga o mudo de novo.
        //
        // Sprint Áudio-Livewire-Duplo-Fix (2026-09-15): SÓ chama o Engine quando o mudo REALMENTE
        // precisa mudar (ver comentário de _localAudioMuted) — evita empurrar
        // MediaPlayer.Mute a cada Play/Preview, mesmo quando o valor já é o mesmo de antes.
        SetLocalAudioMutedIfChanged(isLivewire);

        if (!isLivewire)
        {
            _engine.SetAudioOutputDevice(deviceId);
        }
    }

    /// <summary>
    /// Sprint Áudio-Livewire-Duplo-Fix (2026-09-15): ver comentário de <see cref="_localAudioMuted"/>
    /// pro diagnóstico completo — só repassa pro Engine quando o valor pedido é diferente do último
    /// que este ViewModel já mandou, pra nunca chamar <c>MediaPlayer.Mute</c> redundantemente durante
    /// a janela de inicialização do "aout" do LibVLC (suspeita da causa do engasgo reportado no
    /// Preview logo após a correção do mudo).
    /// </summary>
    private void SetLocalAudioMutedIfChanged(bool muted)
    {
        if (_localAudioMuted == muted)
        {
            return;
        }

        _localAudioMuted = muted;
        _engine.SetLocalAudioMuted(muted);
    }

    /// <summary>
    /// Sprint Livewire-4 (2026-09-14): cria/recria/desliga o <see cref="LivewireSender"/> deste Deck
    /// conforme o dispositivo configurado agora — mesmo padrão (e mesma lógica) já usado por
    /// <see cref="AudioBankViewModel.UpdateLivewireSender"/> pra Vinhetas/Trilhas. Só abre/fecha o
    /// socket; quem decide QUANDO mandar cada bloco é sempre <see cref="ForwardAudioToLivewire"/>,
    /// chamado de dentro de <see cref="OnEngineAudioFrameReady"/>.
    /// </summary>
    private void UpdateLivewireSender(string? livewireDeviceId)
    {
        if (livewireDeviceId is null)
        {
            DisposeLivewireSender();
            return;
        }

        var outputId = livewireDeviceId.Substring(Engine.AudioEngine.ExternalOutputPrefix.Length);
        var output = _resolveLivewireOutput(outputId);

        if (output is null)
        {
            // Saída referenciada não existe mais (apagada em Configurações depois de já selecionada
            // aqui) — fica honestamente sem transmitir nada em vez de adivinhar um canal.
            DisposeLivewireSender();
            _onStatus($"{Name}: a Saída Livewire selecionada não existe mais — nada será transmitido até escolher outra em Configurações.");
            return;
        }

        var networkInterfaceIp = _getLivewireNetworkInterfaceIp();

        if (_livewireSender is not null
            && _livewireOutputId == outputId
            && _livewireSender.Channel == output.Channel
            && _livewireNetworkInterfaceIp == networkInterfaceIp)
        {
            // Nada mudou desde o último Start — não vale a pena derrubar e reabrir o socket à toa
            // (ex.: Configurações salva de novo sem nenhuma edição real neste Deck).
            return;
        }

        DisposeLivewireSender();

        var sender = new LivewireSender(output.Channel);
        if (!sender.Start(networkInterfaceIp))
        {
            _onStatus($"{Name}: não foi possível iniciar a Saída Livewire \"{output.Name}\" (canal {output.Channel}): {sender.LastError}.");
            sender.Dispose();
            return;
        }

        _livewireSender = sender;
        _livewireOutputId = outputId;
        _livewireNetworkInterfaceIp = networkInterfaceIp;
    }

    private void DisposeLivewireSender()
    {
        _livewireSender?.Dispose();
        _livewireSender = null;
        _livewireOutputId = null;
        _livewireNetworkInterfaceIp = null;
    }

    private void Play()
    {
        if (!HasVideo)
        {
            return;
        }

        _isPreviewOnly = false;
        _audioNdiDiagnosticShown = false;
        _engine.Play();

        // Sprint Áudio-Dispositivo-Fix (2026-09-13): chamar SetAudioOutputDevice DEPOIS de Play()
        // (nao antes, como era ate a v1.0.13) foi o primeiro passo — mas o usuario confirmou que
        // resolvia so no PRIMEIRO Play da sessao; um Play seguinte (depois de um Stop) continuava
        // caindo na placa padrao do Windows. Causa real: o LibVLC destroi/recria seu objeto interno
        // de saida de audio ("aout") tambem no Stop, entao a mera ordem das chamadas aqui nunca foi
        // garantia nenhuma, so timing favoravel no primeiro ciclo. Corrigido de forma robusta DENTRO
        // de <see cref="Engine.PlaybackEngine.SetAudioOutputDevice"/> (Sprint Áudio-Dispositivo-Fix-2,
        // mesmo dia): o Engine guarda o pedido e reaplica sozinho a cada vez que a reproducao REALMENTE
        // comeca (evento <c>MediaPlayer.Playing</c>), nao importa quantos Stop/Play acontecerem. A
        // chamada aqui continua reafirmando a placa PRINCIPAL a cada Play — mesma razao de sempre
        // (Sprint Preview-Isolado, 2026-08-15): garantir que um Play real nunca "herda" a placa
        // configurada pro Preview — so que agora a garantia de que ela realmente "pega" vem do Engine,
        // nao da ordem desta chamada.
        //
        // Sprint Livewire-4 (2026-09-14): passou a chamar SetAudioOutputDevice (o wrapper deste
        // ViewModel, de baixo) em vez de _engine.SetAudioOutputDevice direto — precisa passar pelo
        // wrapper pra tambem reabrir/confirmar o LivewireSender a cada novo Play, quando
        // DomainDeck.AudioDeviceId apontar pra uma Saida Livewire nativa (ver UpdateLivewireSender,
        // que ja e barato/idempotente se nada mudou desde o Play anterior).
        SetAudioOutputDevice(DomainDeck.AudioDeviceId);

        _onPlay(this);
    }

    /// <summary>
    /// Sprint Preview-Isolado (2026-08-15), revisada na Sprint Áudio-Unificado (2026-09-15): conferir
    /// um arquivo carregado sem alimentar o NDI — toca SO no player de tela
    /// (<see cref="Engine.PlaybackEngine.PlayPreviewOnly"/>) e NUNCA chama <see cref="_onPlay"/> —
    /// logo nunca pausa a Cartucheira nem espelha pro Deck 2 (isso mora dentro do callback onPlay do
    /// MainViewModel). <see cref="_isPreviewOnly"/> registra que esta sessao foi assim, pra
    /// <see cref="Stop"/>/<see cref="Clear"/>/<see cref="OnEngineMediaEnded"/> saberem que devem
    /// notificar <see cref="_onPreviewEnd"/> (nao <see cref="_onStop"/>/<see cref="_onMediaEnded"/>)
    /// quando ela acabar.
    ///
    /// Sprint Áudio-Unificado (2026-09-15): o campo separado <c>PreviewAudioDeviceId</c> foi REMOVIDO
    /// (ver comentario completo em <see cref="Core.Deck.AudioDeviceId"/>) — Preview agora usa a MESMA
    /// placa configurada pro Play, atraves do MESMO wrapper <see cref="SetAudioOutputDevice"/> que o
    /// Play usa (antes chamava <c>_engine.SetAudioOutputDevice</c> direto, ignorando o wrapper de
    /// proposito). Duas consequencias diretas da decisao do usuario: (1) se a placa unica configurada
    /// for uma Saida Livewire nativa, o Preview agora sai por ela tambem — deixa de ser 100% isolado
    /// do ar nesse caso especifico; (2) some a classe inteira de bugs de "toca nas duas placas ao
    /// mesmo tempo" que motivou o revert de emergencia da v1.0.33 — Play e Preview nunca mais pedem
    /// dispositivos DIFERENTES ao Engine, entao a tarefa de reforco assincrona
    /// (Engine.PlaybackEngine.ReapplyAudioOutputDeviceAsync) nunca mais reaplica um id de dispositivo
    /// "velho" de uma sessao anterior por cima da sessao atual (ela desiste sozinha quando o id pedido
    /// muda — ver o guard <c>_pendingAudioDeviceId != deviceId</c> la dentro). <see cref="_onPreview"/>
    /// (novo) avisa o MainViewModel que um Preview comecou, so pra mandar o aviso UDP dedicado pra
    /// mesa/console externa (nunca pausa Cartucheira, nunca espelha Deck 2, nunca alimenta NDI).
    /// </summary>
    private void Preview()
    {
        if (!HasVideo)
        {
            return;
        }

        _isPreviewOnly = true;
        _audioNdiDiagnosticShown = false;
        _engine.PlayPreviewOnly();

        // Sprint Áudio-Unificado (2026-09-15): passa pelo MESMO wrapper que Play() usa — decide mudo/
        // Livewire/dispositivo Windows a partir da MESMA placa unica configurada (DomainDeck.AudioDeviceId),
        // em vez de chamar _engine.SetAudioOutputDevice direto com uma placa separada de Preview.
        SetAudioOutputDevice(DomainDeck.AudioDeviceId);

        _onStatus($"{Name}: preview de \"{LoadedFileName}\" (local — sem NDI).");
        _onPreview(this);
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
            // (MainViewModel), que retoma a Cartucheira e manda o aviso UDP de Fim pra mesa. Sprint
            // Áudio-Unificado (2026-09-15): _onPreviewEnd (novo) manda so o aviso UDP dedicado de
            // "Preview terminou" — nunca retoma Cartucheira, nunca e o mesmo aviso do Play/Fim.
            _onStatus($"{Name}: preview parado.");
            _onPreviewEnd(this);
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
        else
        {
            // Sprint Áudio-Unificado (2026-09-15): Clear no meio de um Preview tambem conta como
            // "Preview terminou" pra mesa/console externa — mesma ressalva de Stop acima.
            _onPreviewEnd(this);
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

        // Sprint Deck-Seek-Bar (2026-09-12): sem isso, um Deck que ja tocou e voltou pro Cue
        // manteria a duracao do arquivo anterior guardada, permitindo um SeekToProgress "fantasma"
        // (calculando uma posicao dentro de uma duracao que nao é mais a do que esta carregado agora
        // — na pratica so importa entre um Stop e o proximo PositionChanged real, mas honesto reset
        // por completo em vez de deixar estado obsoleto por ai).
        _duration = null;
        DurationDisplay = "--:--";
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
            _duration = e.Duration;

            if (_isSeekDragging)
            {
                // Sprint Deck-Seek-Bar-Fix (2026-09-15): ignora a posição real enquanto o operador
                // estiver arrastando a barra — ver comentário de _isSeekDragging. _duration acima
                // continua atualizado normalmente (não muda com o arraste, e SeekToProgress precisa
                // dele pra converter percentual em tempo assim que o arraste terminar).
                return;
            }

            UpdatePositionDisplay(e.Position, e.Duration);
        });
    }

    /// <summary>
    /// Sprint Deck-Seek-Bar-Fix (2026-09-15): chamado pelo code-behind quando o operador PRESSIONA o
    /// botão do mouse sobre a barra de execução — começa a congelar as atualizações reais de posição
    /// (ver <see cref="_isSeekDragging"/>) até <see cref="EndSeekDrag"/>.
    /// </summary>
    public void BeginSeekDrag() => _isSeekDragging = true;

    /// <summary>
    /// Encerra o congelamento iniciado por <see cref="BeginSeekDrag"/>. Chamado pelo code-behind
    /// tanto no caminho normal (depois de <see cref="SeekToProgress"/> já ter aplicado o novo ponto
    /// de forma otimista) quanto como rede de segurança se o arraste for interrompido de forma
    /// anormal (ex.: a janela perde o foco no meio do arraste, <c>LostMouseCapture</c>) — nesse
    /// segundo caso nenhum Seek é aplicado, a barra só volta a refletir a posição real de reprodução,
    /// nunca fica presa congelada pra sempre.
    /// </summary>
    public void EndSeekDrag() => _isSeekDragging = false;

    /// <summary>
    /// Sprint Deck-Seek-Bar (2026-09-12): logica de formatacao/calculo compartilhada entre
    /// <see cref="OnEnginePositionChanged"/> (posicao real, reportada pelo Engine) e
    /// <see cref="SeekToProgress"/> (posicao OTIMISTA, calculada localmente logo apos um Seek — ver
    /// comentario la sobre por que nao esperar o proximo evento do Engine). Sempre chamada na UI
    /// thread (responsabilidade de quem chama).
    /// </summary>
    private void UpdatePositionDisplay(TimeSpan position, TimeSpan? duration)
    {
        CurrentTime = position.ToString(@"mm\:ss");

        if (duration is { } d && d > TimeSpan.Zero)
        {
            var remaining = d - position;
            RemainingTime = (remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero).ToString(@"mm\:ss");
            DurationDisplay = d.ToString(@"mm\:ss");
            Progress = Math.Clamp(position.TotalMilliseconds / d.TotalMilliseconds * 100, 0, 100);
        }
        else
        {
            RemainingTime = "--:--";
            DurationDisplay = "--:--";
        }
    }

    /// <summary>
    /// Sprint Deck-Seek-Bar (2026-09-12): avanca/retrocede a reproducao pra um ponto do arquivo,
    /// dado como PERCENTUAL (0-100) — o controle deslizante novo na Presentation (MainWindow.xaml,
    /// code-behind em MainWindow.xaml.cs) le sua propria posicao apos o usuario clicar/arrastar e
    /// chama este metodo direto (mesmo padrao ja usado por IsDragOver/Load: code-behind chamando a
    /// ViewModel do Deck certo, resolvido via DataContext). Sem efeito honesto se nao houver video
    /// carregado ou a duracao ainda for desconhecida (mesma guarda ja usada por
    /// <see cref="Engine.PlaybackEngine.Seek"/>, reforcada aqui pra nao tentar calcular uma posicao
    /// sem duracao valida pra converter percentual em tempo).
    ///
    /// Atualiza <see cref="Progress"/>/<see cref="CurrentTime"/>/<see cref="RemainingTime"/> de forma
    /// OTIMISTA, na hora, em vez de esperar o proximo <see cref="Engine.PlaybackEngine.PositionChanged"/>
    /// (que so chega no proximo tick do LibVLC, alguns quadros depois) — sem isso, a barra ficaria
    /// visualmente "voltando" pro ponto antigo por um instante ate o Engine confirmar o novo, um
    /// piscar perceptivel que a prioridade #1 do projeto (Latencia, inclusive percebida na UI) pede
    /// pra evitar.
    /// </summary>
    public void SeekToProgress(double percent)
    {
        if (!HasVideo || _duration is not { } duration || duration <= TimeSpan.Zero)
        {
            return;
        }

        var clampedPercent = Math.Clamp(percent, 0, 100);
        var target = TimeSpan.FromMilliseconds(duration.TotalMilliseconds * clampedPercent / 100);

        _engine.Seek(target);
        UpdatePositionDisplay(target, duration);
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
                // Sprint Áudio-Unificado (2026-09-15): fim natural de um Preview tambem manda o aviso
                // UDP dedicado (_onPreviewEnd) — mesma ressalva de Stop/Clear acima.
                _onStatus($"{Name}: preview terminou.");
                _onPreviewEnd(this);
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
    /// contra a prioridade #1 do projeto.
    ///
    /// Sprint NDI-video-async (2026-09-11): NdiSender.SendFrame agora usa a variante ASSINCRONA da
    /// NDI SDK (NDIlib_send_send_video_async_v2) — ela retorna antes de terminar de processar/enviar
    /// o quadro, entao NAO copia o buffer de forma sincrona como a variante antiga fazia. A
    /// seguranca aqui vem do Engine, nao mais desta chamada: <see cref="Engine.PlaybackEngine"/>
    /// alterna entre dois buffers a cada quadro (ver <see cref="Engine.EngineFrameReadyEventArgs"/>),
    /// entao o buffer que acabou de ser repassado pro NdiSender so volta a ser escrito pelo LibVLC
    /// dois quadros depois — folga suficiente pro envio assincrono terminar (e, mesmo que nao tenha
    /// terminado, a propria NDI SDK sincroniza sozinha na chamada seguinte).
    /// </summary>
    private void OnEngineFrameReady(object? sender, Engine.EngineFrameReadyEventArgs e)
    {
        _ndiSender?.SendFrame(e.Data, e.Width, e.Height, e.StrideBytes);
        ForwardFrameToScreens(e);
    }

    /// <summary>
    /// Sprint Telas-Saida-Direta (2026-11-10): espelha o MESMO quadro UYVY que acabou de ir pro NDI
    /// direto nas telas abertas deste Deck (<see cref="_screenOutputWindows"/>), se
    /// <see cref="Core.Deck.SendToScreens"/> estiver ligado e houver pelo menos uma. Chamado na
    /// MESMA thread de callback do Engine que <see cref="OnEngineFrameReady"/> (nunca a UI thread,
    /// mesmo motivo de latência documentado ali) — a conversão UYVY→BGRA32
    /// (<see cref="UyvyBgraConverter"/>) acontece AQUI, nesta thread, pra deixar o trabalho que de
    /// fato precisa da UI thread (o <c>WritePixels</c> do <see cref="WriteableBitmap"/>, que tem
    /// afinidade de thread) o mais leve possível. <see cref="_screenFrameInFlight"/> garante que, se
    /// a UI thread estiver lenta/ocupada, o próximo quadro simplesmente é descartado em vez de
    /// empilhar despachos sem limite (mesmo "nunca deixar fila crescer" já preocupação documentada
    /// deste projeto).
    /// </summary>
    private void ForwardFrameToScreens(Engine.EngineFrameReadyEventArgs e)
    {
        if (!DomainDeck.SendToScreens || _screenOutputWindows.Count == 0 || _screenFrameInFlight)
        {
            return;
        }

        _screenFrameInFlight = true;

        try
        {
            var width = e.Width;
            var height = e.Height;
            var stride = e.StrideBytes;
            var byteCount = height * stride;

            if (_screenOutputUyvyBuffer is null || _screenOutputUyvyBuffer.Length < byteCount)
            {
                _screenOutputUyvyBuffer = new byte[byteCount];
            }

            Marshal.Copy(e.Data, _screenOutputUyvyBuffer, 0, byteCount);

            _screenOutputBgraBuffer ??= new byte[BackgroundArtRenderer.FrameWidth * BackgroundArtRenderer.FrameHeight * 4];
            UyvyBgraConverter.ConvertToBgra32(_screenOutputUyvyBuffer, width, height, stride, _screenOutputBgraBuffer);

            var bgra = _screenOutputBgraBuffer;
            Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                try
                {
                    WriteScreenOutputBitmap(bgra, width, height);
                }
                finally
                {
                    _screenFrameInFlight = false;
                }
            });
        }
        catch (Exception)
        {
            // Falha honesta — qualquer problema aqui (buffer inesperado, etc.) nunca pode derrubar
            // o caminho principal do NDI, que já rodou acima com sucesso.
            _screenFrameInFlight = false;
        }
    }

    /// <summary>Escreve um buffer BGRA32 já convertido no <see cref="_screenOutputBitmap"/> compartilhado — SEMPRE na UI thread (afinidade de thread do <see cref="WriteableBitmap"/>).</summary>
    private void WriteScreenOutputBitmap(byte[] bgra, int width, int height)
    {
        if (_screenOutputBitmap is null)
        {
            return;
        }

        _screenOutputBitmap.WritePixels(new Int32Rect(0, 0, width, height), bgra, width * 4, 0);
    }

    private static bool IsBackgroundArtIdleState(DeckPlaybackState state) =>
        state is DeckPlaybackState.Cue or DeckPlaybackState.Stopped or DeckPlaybackState.Finished;

    /// <summary>
    /// Sprint Arte-de-Fundo (2026-11-10): decide se a saída NDI deste Deck deve estar mandando a
    /// arte de fundo ativa agora — chamado sempre que <see cref="State"/> muda (ver setter acima) e
    /// por <see cref="RefreshBackgroundArt"/> (Configurações → Salvar, e na inicialização do
    /// programa — ver <c>MainViewModel</c>). Liga só quando TODAS forem verdadeiras: existe pelo
    /// menos UM destino pra mandar a arte (saída NDI disponível, <see cref="_ndiSender"/> não nulo,
    /// OU pelo menos uma tela aberta — <see cref="Core.Deck.SendToScreens"/> ligado e
    /// <see cref="_screenOutputWindows"/> não vazia), <see cref="Core.Deck.UseBackgroundArt"/> deste
    /// Deck, e o Deck estiver numa das três situações de <see cref="IsBackgroundArtIdleState"/>
    /// (Cue/Stopped/Finished). Deliberadamente NÃO inclui Paused — um Deck pausado continua
    /// mostrando o último quadro real do próprio VT (congelado), não a arte de espera, mesmo
    /// comportamento visual de sempre nesse estado.
    ///
    /// Sprint Telas-Saida-Direta (2026-11-10): a condição de destino deixou de exigir só
    /// <see cref="_ndiSender"/> não nulo — antes desta sprint, uma máquina sem NDI instalado também
    /// ficava sem conseguir mostrar a arte de fundo nas Telas (mesmo bug de acoplamento indevido já
    /// corrigido pro áudio na Sprint Livewire-4, ver comentário do construtor), o que quebraria
    /// justamente o cenário que motivou esta sprint (usuário migrando do Resolume pras Telas). Se
    /// NENHUM dos dois destinos existir, nem vale a pena decodificar a imagem — mesma economia de
    /// CPU de antes.
    ///
    /// Qualquer combinação que não bata (toggle desligado, nenhuma arte ativa resolvendo/carregando,
    /// nenhum destino disponível, ou tocando/pausado/erro/etc.) para o timer e libera o buffer
    /// nativo — a saída volta a não mandar quadro nenhum, falha honesta em vez de adivinhar.
    /// </summary>
    private void UpdateBackgroundArtIdleSender()
    {
        var hasAnyBackgroundArtDestination = _ndiSender is not null || (DomainDeck.SendToScreens && _screenOutputWindows.Count > 0);
        var shouldSend = hasAnyBackgroundArtDestination && DomainDeck.UseBackgroundArt && IsBackgroundArtIdleState(State);

        if (!shouldSend || !TryRenderActiveBackgroundArt(out var frame))
        {
            StopBackgroundArtTimer();
            return;
        }

        ReplaceBackgroundArtBuffer(frame);
        StartBackgroundArtTimer();
    }

    /// <summary>Resolve (<see cref="_getActiveBackgroundArtPath"/>) e decodifica (<see cref="BackgroundArtRenderer"/>) a arte ativa agora. Falso em qualquer falha honesta — nenhuma arte ativa, arquivo ausente, decodificação falhou.</summary>
    private bool TryRenderActiveBackgroundArt(out byte[] frame)
    {
        var rendered = BackgroundArtRenderer.RenderToUyvy(_getActiveBackgroundArtPath());
        frame = rendered ?? Array.Empty<byte>();
        return rendered is not null;
    }

    private void ReplaceBackgroundArtBuffer(byte[] frame)
    {
        lock (_backgroundArtGate)
        {
            if (_backgroundArtBuffer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(_backgroundArtBuffer);
                _backgroundArtBuffer = IntPtr.Zero;
            }

            _backgroundArtBuffer = Marshal.AllocHGlobal(frame.Length);
            Marshal.Copy(frame, 0, _backgroundArtBuffer, frame.Length);
        }

        WriteBackgroundArtFrameToScreens(frame);
    }

    /// <summary>
    /// Sprint Telas-Saida-Direta (2026-11-10): espelha a MESMA arte de fundo que acabou de ser
    /// resolvida pra saída NDI (chamado só de dentro de <see cref="ReplaceBackgroundArtBuffer"/>,
    /// nunca direto) nas telas diretas deste Deck, se alguma estiver aberta — pergunta "Arte
    /// parado" da Sprint Telas-Saida-Direta: o usuário confirmou que quer a mesma arte ativa também
    /// nas telas, não um comportamento diferente. Diferente do vídeo real
    /// (<see cref="ForwardFrameToScreens"/>), o conteúdo aqui NUNCA muda entre chamadas (é sempre a
    /// mesma imagem estática) — por isso basta UMA conversão/escrita por troca de arte, nunca a
    /// cada tick do <see cref="_backgroundArtTimer"/> (200ms) que só reenvia pro NDI. Sempre chamado
    /// na UI thread (mesma thread de quem chama <see cref="ReplaceBackgroundArtBuffer"/> — ver
    /// comentário de <see cref="UpdateBackgroundArtIdleSender"/>), então escreve direto no
    /// <see cref="WriteableBitmap"/> sem precisar de <see cref="Dispatcher.BeginInvoke(Delegate)"/>.
    /// </summary>
    private void WriteBackgroundArtFrameToScreens(byte[] frame)
    {
        if (!DomainDeck.SendToScreens || _screenOutputWindows.Count == 0 || _screenOutputBitmap is null)
        {
            return;
        }

        _screenOutputBgraBuffer ??= new byte[BackgroundArtRenderer.FrameWidth * BackgroundArtRenderer.FrameHeight * 4];
        UyvyBgraConverter.ConvertToBgra32(frame, BackgroundArtRenderer.FrameWidth, BackgroundArtRenderer.FrameHeight, BackgroundArtRenderer.StrideBytes, _screenOutputBgraBuffer);
        WriteScreenOutputBitmap(_screenOutputBgraBuffer, BackgroundArtRenderer.FrameWidth, BackgroundArtRenderer.FrameHeight);
    }

    private void StartBackgroundArtTimer()
    {
        _backgroundArtSending = true;

        // Sprint Arte-de-Fundo (2026-11-10): 5 quadros/s — baixo de propósito (a imagem nunca
        // muda, só reenvia os mesmos bytes), o suficiente pra manter o Resolume/telões "vendo" uma
        // fonte NDI viva em vez de uma fonte sem atualização; sem precedente nenhum nesta base de
        // código pra calibrar contra, então este valor é um palpite honesto, não uma medição real —
        // ajuste aqui se algum receptor precisar de mais.
        const int periodMs = 200;

        if (_backgroundArtTimer is null)
        {
            _backgroundArtTimer = new Timer(_ => SendBackgroundArtFrame(), null, 0, periodMs);
        }
        else
        {
            _backgroundArtTimer.Change(0, periodMs);
        }
    }

    private void StopBackgroundArtTimer()
    {
        _backgroundArtSending = false;
        _backgroundArtTimer?.Change(Timeout.Infinite, Timeout.Infinite);

        lock (_backgroundArtGate)
        {
            if (_backgroundArtBuffer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(_backgroundArtBuffer);
                _backgroundArtBuffer = IntPtr.Zero;
            }
        }
    }

    private void SendBackgroundArtFrame()
    {
        if (!_backgroundArtSending)
        {
            return;
        }

        lock (_backgroundArtGate)
        {
            if (_backgroundArtBuffer == IntPtr.Zero)
            {
                return;
            }

            _ndiSender?.SendFrame(_backgroundArtBuffer, BackgroundArtRenderer.FrameWidth, BackgroundArtRenderer.FrameHeight, BackgroundArtRenderer.StrideBytes);
        }
    }

    /// <summary>
    /// Sprint Arte-de-Fundo (2026-11-10): reavalia a arte de fundo deste Deck do zero — chamado pelo
    /// <c>MainViewModel</c> na inicialização (depois de <c>SettingsMapper.ApplyToDeck</c> aplicar
    /// <see cref="Core.Deck.UseBackgroundArt"/>) e pelo <see cref="SettingsViewModel"/> ao Salvar (a
    /// lista de artes/qual está ativa podem ter mudado, mesmo sem este Deck ter trocado de Estado —
    /// <see cref="State"/> sozinho não cobre isso). Seguro chamar a qualquer momento, inclusive
    /// enquanto já está mandando uma arte antiga — troca o buffer nativo sem sair do ar (o Resolume
    /// só vê o quadro novo a partir do próximo tick do timer, no máximo 200ms depois).
    /// </summary>
    public void RefreshBackgroundArt() => UpdateBackgroundArtIdleSender();

    /// <summary>
    /// Sprint Telas-Saida-Direta (2026-11-10): abre/fecha as <see cref="ScreenOutputWindow"/> deste
    /// Deck pra bater com <see cref="Core.Deck.SendToScreens"/>/<see cref="Core.Deck.ScreenOutputDeviceNames"/>
    /// contra os monitores que o Windows reconhece AGORA (<c>System.Windows.Forms.Screen.AllScreens</c>)
    /// — chamado pelo <c>MainViewModel</c> na inicialização (depois de <c>SettingsMapper.ApplyToDeck</c>)
    /// e pelo <see cref="SettingsViewModel"/> ao Salvar, mesmo papel/pontos de chamada de
    /// <see cref="RefreshBackgroundArt"/> pra arte de fundo. Sempre chamado na UI thread (mesmos dois
    /// chamadores) — cria/fecha <see cref="Window"/> de verdade, que só pode acontecer ali. Um
    /// monitor escolhido que não está conectado agora é simplesmente ignorado aqui (não há janela
    /// nenhuma pra abrir sobre um monitor que não existe) — a seleção continua salva, ver
    /// <see cref="ScreenOptionViewModel.IsCurrentlyConnected"/> pra onde isso é mostrado ao operador.
    /// Seguro chamar a qualquer momento: só fecha as janelas que deixaram de fazer sentido e só
    /// abre as que faltam, nunca fecha/reabre tudo à toa.
    /// </summary>
    public void RefreshScreenOutputs()
    {
        var desiredDeviceNames = DomainDeck.SendToScreens
            ? new HashSet<string>(DomainDeck.ScreenOutputDeviceNames)
            : new HashSet<string>();

        // Incidente real (2026-11-11): o usuário marcou, sem perceber, o MESMO monitor onde roda a
        // janela principal da Cartucheira como uma "Tela" de saída — a ScreenOutputWindow (sem
        // borda, sempre no topo, maximizada) cobriu a tela inteira sem nenhuma forma de fechar
        // (sem botão, sem ícone na barra de tarefas), travando o operador fora do próprio programa
        // até precisar encerrar a sessão do Windows. Corrigido com uma segunda camada de segurança,
        // além de qualquer cuidado na UI de seleção: NUNCA abrir uma ScreenOutputWindow no monitor
        // que hospeda alguma janela de verdade da própria Cartucheira agora (MainWindow,
        // SettingsWindow etc.) — essa checagem roda aqui, no ponto único que de fato abre as
        // janelas, então vale tanto pra seleção feita antes desta sprint existir (configuração
        // antiga/editada à mão) quanto pra qualquer seleção futura.
        var operatorDeviceNames = GetOperatorScreenDeviceNames();

        var targetScreens = System.Windows.Forms.Screen.AllScreens
            .Where(s => desiredDeviceNames.Contains(s.DeviceName) && !operatorDeviceNames.Contains(s.DeviceName))
            .ToList();
        var targetDeviceNames = new HashSet<string>(targetScreens.Select(s => s.DeviceName));

        var skippedForSafety = desiredDeviceNames.Intersect(operatorDeviceNames).Any();
        if (skippedForSafety)
        {
            _onStatus("Aviso: uma tela marcada em \"Telas\" não foi aberta porque é a mesma tela onde a Cartucheira está sendo usada agora — isso evita travar sua tela. Desmarque essa tela em Configurações, ou use-a só em outro monitor.");
        }

        for (var i = _screenOutputWindows.Count - 1; i >= 0; i--)
        {
            var window = _screenOutputWindows[i];
            if (!targetDeviceNames.Contains(window.DeviceName))
            {
                _screenOutputWindows.RemoveAt(i);
                window.Close();
            }
        }

        if (targetScreens.Count == 0)
        {
            return;
        }

        var bitmap = EnsureScreenOutputBitmap();
        var alreadyOpenDeviceNames = new HashSet<string>(_screenOutputWindows.Select(w => w.DeviceName));

        foreach (var screen in targetScreens)
        {
            if (alreadyOpenDeviceNames.Contains(screen.DeviceName))
            {
                continue;
            }

            var window = new ScreenOutputWindow(screen.DeviceName);
            window.SetSource(bitmap);
            window.PositionOnScreen(screen);
            // Rede de segurança adicional (mesmo incidente do comentário acima): Ctrl+Shift+Esc
            // dentro desta janela fecha ela na hora, mesmo que a checagem de monitor-do-operador
            // acima não tenha pego o caso (ex.: o operador move a janela principal pra esta tela
            // DEPOIS que a Cartucheira já abriu a saída aqui). Não mexe na configuração salva —
            // só dá uma saída imediata; a tela volta a abrir no próximo Refresh enquanto a seleção
            // continuar marcada, dando tempo do operador ir em Configurações corrigir de verdade.
            window.PreviewKeyDown += (_, e) =>
            {
                if (e.Key == System.Windows.Input.Key.Escape && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
                {
                    window.Close();
                }
            };
            window.Closed += (_, _) => _screenOutputWindows.Remove(window);
            window.Show();
            _screenOutputWindows.Add(window);
        }
    }

    /// <summary>
    /// Nomes de dispositivo (<see cref="System.Windows.Forms.Screen.DeviceName"/>) de todo monitor
    /// que hospeda AGORA alguma janela de verdade da própria Cartucheira (MainWindow, SettingsWindow,
    /// diálogos etc.) — nunca as próprias <see cref="ScreenOutputWindow"/> (seria sempre verdade e
    /// anularia a checagem). Falha honesta: qualquer problema ao resolver a tela de uma janela
    /// específica apenas ignora aquela janela, nunca derruba o <see cref="RefreshScreenOutputs"/>
    /// inteiro.
    /// </summary>
    private static HashSet<string> GetOperatorScreenDeviceNames()
    {
        var result = new HashSet<string>();

        if (System.Windows.Application.Current is null)
        {
            return result;
        }

        foreach (Window window in System.Windows.Application.Current.Windows)
        {
            if (window is ScreenOutputWindow || !window.IsVisible)
            {
                continue;
            }

            try
            {
                var handle = new WindowInteropHelper(window).Handle;
                if (handle == IntPtr.Zero)
                {
                    continue;
                }

                var screen = System.Windows.Forms.Screen.FromHandle(handle);
                result.Add(screen.DeviceName);
            }
            catch (Exception)
            {
                // Falha honesta — ver comentário de resumo do método.
            }
        }

        return result;
    }

    /// <summary>Cria (uma única vez, sob demanda) o <see cref="WriteableBitmap"/> 1920x1080 compartilhado por todas as telas deste Deck — mesmo canvas fixo que <see cref="BackgroundArtRenderer"/> já usa.</summary>
    private WriteableBitmap EnsureScreenOutputBitmap()
    {
        if (_screenOutputBitmap is null)
        {
            _screenOutputBitmap = new WriteableBitmap(BackgroundArtRenderer.FrameWidth, BackgroundArtRenderer.FrameHeight, 96, 96, PixelFormats.Bgra32, null);
        }

        return _screenOutputBitmap;
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
        ForwardAudioToLivewire(e);
    }

    /// <summary>
    /// Sprint Livewire-4 (2026-09-14): reempacota o mesmo bloco de áudio (480 amostras/canal, ver
    /// <c>Engine.NdiAudioCapture.BlockSamplesPerChannel</c>, o mesmo pipeline NAudio independente que
    /// já alimenta <see cref="OnEngineAudioFrameReady"/>) em dois pacotes Livewire de
    /// <see cref="LivewireRtpFraming.FramesPerPacket"/> (240) quadros cada — 480 é exatamente o dobro
    /// de 240, então a divisão é sempre exata, sem sobra pra guardar entre chamadas. Sem efeito
    /// honesto se este Deck não tiver Saída Livewire selecionada agora (<see cref="_livewireSender"/>
    /// nulo) ou se o bloco não vier no formato esperado (estéreo, tamanho par) — nunca deveria
    /// acontecer com o NdiAudioCapture de hoje, mas é melhor ignorar silenciosamente do que montar um
    /// pacote errado. Chamado na MESMA thread do Engine que dispara
    /// <see cref="Engine.PlaybackEngine.AudioFrameReady"/> (nunca a UI thread, mesma razão de latência
    /// já documentada em <see cref="OnEngineFrameReady"/>).
    /// </summary>
    private void ForwardAudioToLivewire(Engine.EngineAudioFrameReadyEventArgs e)
    {
        var sender = _livewireSender;
        if (sender is null || e.Channels != 2 || e.SampleCount <= 0 || e.SampleCount % 2 != 0)
        {
            return;
        }

        var half = e.SampleCount / 2;

        var left1 = new float[half];
        var right1 = new float[half];
        for (var i = 0; i < half; i++)
        {
            left1[i] = e.Data[i * 2];
            right1[i] = e.Data[i * 2 + 1];
        }
        sender.SendFrame(left1, right1);

        var left2 = new float[half];
        var right2 = new float[half];
        for (var i = 0; i < half; i++)
        {
            var srcIndex = (half + i) * 2;
            left2[i] = e.Data[srcIndex];
            right2[i] = e.Data[srcIndex + 1];
        }
        sender.SendFrame(left2, right2);
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

        // Sprint Livewire-4 (2026-09-14): AudioFrameReady agora e assinado sempre (ver comentario no
        // construtor), entao desassina sempre tambem.
        _engine.AudioFrameReady -= OnEngineAudioFrameReady;

        // Sprint Telas-Saida-Direta (2026-11-10): FrameReady (video) agora tambem e assinado sempre
        // (ver comentario no construtor — dois consumidores possiveis, NDI e Telas, cada um
        // null-safe/condicional sozinho), entao desassina sempre tambem, sem condicionar a
        // _ndiSender existir.
        _engine.FrameReady -= OnEngineFrameReady;

        // Sprint Arte-de-Fundo (2026-11-10): para o timer e libera o buffer nativo ANTES de
        // descartar o NdiSender — StopBackgroundArtTimer já zera _backgroundArtSending primeiro
        // (checado por SendBackgroundArtFrame antes de qualquer chamada ao NdiSender), o que reduz
        // bastante a janela de uma tick do timer (thread do ThreadPool) chamar SendFrame num
        // NdiSender já descartado, mas não a elimina por completo — mesmo risco residual, inerente
        // ao próprio design, que _engine.Dispose() abaixo já aceita contra callbacks nativos em voo.
        StopBackgroundArtTimer();
        _backgroundArtTimer?.Dispose();
        _backgroundArtTimer = null;

        // Sprint Telas-Saida-Direta (2026-11-10): fecha todas as janelas de telão abertas deste
        // Deck — nunca deixar uma janela sem borda/sempre no topo pra trás depois que o Deck (ou o
        // programa inteiro, ao fechar) é descartado.
        foreach (var window in _screenOutputWindows)
        {
            window.Close();
        }

        _screenOutputWindows.Clear();

        _engine.Dispose();
        _ndiSender?.Dispose();
        DisposeLivewireSender();
    }
}
