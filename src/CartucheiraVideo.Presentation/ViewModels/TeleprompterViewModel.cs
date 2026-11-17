using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using CartucheiraVideo.Configuration;
using CartucheiraVideo.Engine;
using CartucheiraVideo.Ndi;
using CartucheiraVideo.Presentation.Views;

namespace CartucheiraVideo.Presentation.ViewModels;

/// <summary>
/// Orquestrador do módulo Teleprompter — Sprint Teleprompter (2026-11-16). Papel equivalente ao que
/// <see cref="DeckViewModel.RefreshScreenOutputs"/> tem sobre <see cref="ScreenOutputWindow"/>, só
/// que pra uma única janela (<see cref="TeleprompterOutputWindow"/>) e com um motor de voz
/// (<see cref="TeleprompterVoiceEngine"/>) por cima. Três responsabilidades:
///
/// 1. <see cref="ApplySettings"/> — abre/fecha/reposiciona a janela de saída e liga/desliga/
///    reconfigura o motor de voz, a partir do <see cref="TeleprompterSettingsSnapshot"/> salvo.
///    Chamado pelo <c>MainViewModel</c> na inicialização e por <see cref="SettingsViewModel.Save"/>
///    — mesmos dois pontos de chamada de <see cref="DeckViewModel.RefreshScreenOutputs"/>/
///    <see cref="DeckViewModel.RefreshBackgroundArt"/>.
/// 2. <see cref="NotifyDeckLoaded"/> — chamado pelo <c>MainViewModel</c> nos MESMOS pontos que já
///    chamam <c>PlaylistViewModel.MarkLoadedInDeck</c> (carga manual por duplo clique E avanço
///    automático): se o Deck que acabou de carregar uma Lauda for o Deck fonte configurado
///    (<see cref="TeleprompterSettingsSnapshot.SourceDeckSetting"/>), troca o texto exibido pro TP
///    (<c>Core.PlaylistLauda.TpText</c>) daquela Lauda. Deliberadamente NÃO reage a Play/Stop/Pause
///    do Deck — só a "carregar uma Lauda nova", pedido literal do usuário ("quando ela estiver
///    carregada ao deck, no teleprompter já aparece aquele texto").
/// 3. Ponte entre <see cref="TeleprompterVoiceEngine.ScriptPositionChanged"/> (nasce na thread de
///    captura do NAudio) e <see cref="TeleprompterOutputWindow.SetScriptPosition"/> (precisa rodar
///    na UI thread) — marshala com <see cref="System.Windows.Threading.Dispatcher.BeginInvoke(System.Delegate)"/>,
///    mesmo contrato de sempre deste projeto pra eventos de thread de áudio (ver
///    <c>Engine.AudioEngine</c>, "quem ouve precisa marshalar pra UI thread sozinho").
/// 4. <see cref="RefreshNdi"/> (Sprint Teleprompter-NDI, 2026-11-17) — liga/desliga/renomeia uma
///    fonte NDI própria do Teleprompter, pra quem não tem mais saída de monitor livre na placa de
///    vídeo ("ainda preciso colocar placa de vídeo com mais saídas [...] vamos colocar para o
///    teleprompter gerar tb em ndi"). Reaproveita a MESMA janela de saída (<see cref="RefreshWindow"/>
///    agora também a mantém viva, escondida fora de qualquer monitor real, só por causa do NDI) e
///    um <see cref="System.Windows.Threading.DispatcherTimer"/> pra renderizá-la periodicamente
///    (<see cref="TeleprompterOutputWindow.RenderToUyvy"/>) e mandar pro <c>Ndi.NdiSender</c> deste
///    módulo.
/// 5. <see cref="SetManualOverride"/>/<see cref="SetManualText"/> (Sprint Controle-Remoto-TP,
///    2026-11-17, pedido do usuário "controle manual do tp para caso necessario desabiliiatr a
///    funcçao auto [...] e controle de voz [...] isso precisa ser feito de outro pc, nao onde esta
///    a cartucheira, para issso um outro software ou url que va controlando"): chamados pelo
///    <c>PlaylistDashboardCoordinator</c> a partir das rotas <c>POST /api/teleprompter/manual</c>/
///    <c>POST /api/teleprompter/text</c> da dashboard web — NUNCA por nenhuma tela da própria
///    Cartucheira desktop, de propósito (o pedido explícito foi controlar de OUTRO computador, não
///    local). Estado é só de runtime (<see cref="_isManualOverride"/> nunca é persistido em
///    settings.json, mesmo princípio do "disjuntor" <c>PlaylistViewModel.AutoModeEnabled</c>/
///    <c>DeckViewModel.IsAutoMode</c>): sempre volta a desligado (automático) ao reiniciar o
///    programa. Decisão confirmada com o usuário: ligar o manual desliga a voz junto, sem chave
///    separada — ver <see cref="RefreshVoiceEngine"/>. <see cref="StateChanged"/> (parte 2,
///    2026-11-17, pedido "2 segundos e muito, precisamos pensar outra alternativa" ao polling da
///    dashboard web) dispara sempre que o estado muda de verdade, pro <c>PlaylistDashboardCoordinator</c>
///    empurrar via Server-Sent Events em vez da dashboard ficar perguntando de novo e de novo —
///    mesma sprint que corrigiu <see cref="SetManualOverride"/>/<see cref="SetManualText"/> pra
///    marshalar pra UI thread (<c>Dispatcher.Invoke</c>), porque só nessa parte 2 ficou claro que
///    os dois são chamados de uma thread de requisição HTTP em segundo plano, nunca da UI thread.
/// </summary>
public sealed class TeleprompterViewModel : IDisposable
{
    private readonly DeckViewModel _deckA;
    private readonly DeckViewModel _deckB;
    private readonly Action<string> _onStatus;
    private readonly TeleprompterVoiceEngine _voiceEngine = new();

    private TeleprompterOutputWindow? _window;
    private string _currentScriptText = string.Empty;
    private TeleprompterSourceDeck _sourceDeckSetting = TeleprompterSourceDeck.DeckA;
    private bool _disposed;

    /// <summary>
    /// Sprint Teleprompter-NDI (2026-11-17): <see cref="TeleprompterOutputWindow.DeviceName"/>
    /// "artificial" usado quando a janela existe só pra alimentar o NDI, sem nenhuma tela física
    /// escolhida/conectada (ver <see cref="RefreshWindow"/>) — nunca colide com um
    /// <c>System.Windows.Forms.Screen.DeviceName</c> real (todos começam com <c>"\\.\DISPLAY"</c>).
    /// </summary>
    private const string OffScreenDeviceMarker = "(ndi-sem-tela)";

    /// <summary>
    /// Sprint Teleprompter-NDI (2026-11-17): 10 quadros/s — bem abaixo de vídeo real (não há
    /// nenhuma animação contínua aqui, só rolagem de texto por evento: palavra casada pela voz,
    /// seta/roda do mouse), mas suficiente pra uma rolagem de texto parecer fluida num
    /// vMix/OBS recebendo o canal. Sem precedente nesta base pra calibrar contra (primeira fonte
    /// NDI deste projeto que renderiza UI em vez de decodificar vídeo/imagem estática) — palpite
    /// honesto, ajustável aqui se o usuário testar e achar pouco.
    /// </summary>
    private static readonly TimeSpan NdiFrameInterval = TimeSpan.FromMilliseconds(100);

    private NdiSender? _ndiSender;
    private string? _ndiChannelName;
    private DispatcherTimer? _ndiTimer;

    /// <summary>
    /// Sprint Controle-Remoto-TP (2026-11-17): liga/desliga por <see cref="SetManualOverride"/>, só
    /// de runtime (nunca persistido — ver comentário de classe). Enquanto ligado, <see cref="NotifyDeckLoaded"/>
    /// deixa de empurrar texto pra janela/voz automaticamente (só atualiza <see cref="_autoScriptText"/>
    /// por baixo, pra ter o que restaurar quando desligar) e <see cref="RefreshVoiceEngine"/> nunca
    /// liga o motor de voz, mesmo que a configuração salva peça.
    /// </summary>
    private bool _isManualOverride;

    /// <summary>
    /// Sprint Controle-Remoto-TP (2026-11-17): último texto recebido pelo caminho automático
    /// (<see cref="NotifyDeckLoaded"/>, Playlist/Lauda) — guardado SEPARADO de <see cref="_currentScriptText"/>
    /// (o que está exibido agora, que pode ser texto manual) justamente pra poder restaurar o texto
    /// automático mais recente quando o controle manual for desligado, mesmo que a Playlist tenha
    /// avançado pra outra Lauda enquanto o manual estava ligado.
    /// </summary>
    private string _autoScriptText = string.Empty;

    /// <summary>
    /// Sprint Controle-Remoto-TP (2026-11-17): última configuração aplicada por <see cref="ApplySettings"/>
    /// — guardada pra <see cref="SetManualOverride"/> poder religar a voz (se a configuração salva
    /// pedir) ao desligar o manual, sem precisar que o <c>MainViewModel</c> chame <see cref="ApplySettings"/>
    /// de novo só pra isso.
    /// </summary>
    private TeleprompterSettingsSnapshot? _lastSettings;

    /// <summary>
    /// Sprint Controle-Remoto-TP, parte 3 (2026-11-17, pedido do usuário "ter os controles de tp
    /// [...] pesquise sobre software de tp e controles pra colocar nele pra poder controlar
    /// manual"): true enquanto o Play remoto estiver rolando o texto automaticamente por TEMPO (ver
    /// <see cref="SetAutoScrollPlaying"/>) — só de runtime, mesmo princípio de
    /// <see cref="_isManualOverride"/> (nunca persistido, sempre volta a pausado ao reiniciar o
    /// programa). Só tem efeito com o manual ligado — não existe "Play automático" fora do controle
    /// manual, porque no automático é a Playlist/voz que decide a rolagem.
    /// </summary>
    private bool _autoScrollPlaying;

    /// <summary>
    /// Sprint Controle-Remoto-TP, parte 3 (2026-11-17): escala simples 1-10 (decisão confirmada com
    /// o usuário, em vez de palavras-por-minuto — "mais rápido de ajustar sob pressão no ar"), 5 é o
    /// padrão. Convertido pra pixels/segundo em <see cref="PixelsPerSecondForSpeed"/> — palpite
    /// honesto sem nenhum precedente nesta base pra calibrar contra (primeira rolagem automática por
    /// TEMPO deste projeto; a existente, por voz, rola por PALAVRA CASADA, não por tempo), ajustável
    /// se o usuário testar e achar rápido/lento demais.
    /// </summary>
    private int _autoScrollSpeed = 5;

    /// <summary>Sprint Controle-Remoto-TP, parte 3 (2026-11-17) — tick a 20Hz (suave o bastante pra rolagem de texto parecer contínua, sem sobrecarregar a UI thread) enquanto <see cref="_autoScrollPlaying"/> estiver ligado.</summary>
    private static readonly TimeSpan AutoScrollTickInterval = TimeSpan.FromMilliseconds(50);

    private DispatcherTimer? _autoScrollTimer;

    /// <summary>
    /// Sprint Controle-Remoto-TP, parte 3 (2026-11-17): tick mais espaçado (~7Hz) que só CHECA se a
    /// posição de rolagem mudou desde a última vez (por QUALQUER motivo — voz, teclado/roda do mouse
    /// local, Play automático remoto, passo/arrasto remoto) e dispara <see cref="RaiseStateChanged"/>
    /// só quando mudou de verdade — garante que o espelho da dashboard web acompanha a rolagem mesmo
    /// quando ela vem de um caminho que não chama <c>RaiseStateChanged</c> diretamente (ex.:
    /// <see cref="OnScriptPositionChanged"/>, a rolagem por voz, ou as teclas/roda do mouse na
    /// própria janela local). Vive enquanto <see cref="_window"/> existir (ver <see cref="RefreshWindow"/>),
    /// não precisa do NDI ligado — mesmo <see cref="DispatcherTimer"/> do NDI, mas mais barato (só lê
    /// um double, nunca renderiza nada) e seguro de deixar sempre rodando.
    /// </summary>
    private static readonly TimeSpan ScrollSyncInterval = TimeSpan.FromMilliseconds(150);

    private DispatcherTimer? _scrollSyncTimer;
    private double _lastKnownScrollFraction;

    /// <summary>Sprint Controle-Remoto-TP (2026-11-17) — exposto pro <c>PlaylistDashboardCoordinator</c> relatar o estado atual (GET /api/teleprompter/state).</summary>
    public bool IsManualOverride => _isManualOverride;

    /// <summary>Sprint Controle-Remoto-TP (2026-11-17) — texto exibido agora (automático ou manual), idem.</summary>
    public string CurrentText => _currentScriptText;

    /// <summary>Sprint Controle-Remoto-TP, parte 3 (2026-11-17) — idem, se o Play automático remoto está rolando agora.</summary>
    public bool IsAutoScrollPlaying => _autoScrollPlaying;

    /// <summary>Sprint Controle-Remoto-TP, parte 3 (2026-11-17) — idem, velocidade atual (1-10) do Play automático remoto.</summary>
    public int AutoScrollSpeed => _autoScrollSpeed;

    /// <summary>Sprint Controle-Remoto-TP, parte 3 (2026-11-17) — posição de rolagem atual (0..1), lida direto da janela de saída (ver <see cref="TeleprompterOutputWindow.GetScrollFraction"/>) — zero se não houver janela nenhuma aberta agora.</summary>
    public double ScrollFraction => _window?.GetScrollFraction() ?? 0;

    /// <summary>Sprint Controle-Remoto-TP, parte 3 (2026-11-17) — tamanho de fonte ATUAL da janela de saída, pro espelho da dashboard web escalar o texto na mesma proporção (ver <see cref="TeleprompterOutputWindow.CurrentFontSize"/>). Zero se não houver janela nenhuma aberta agora.</summary>
    public double FontSizePt => _window?.CurrentFontSize ?? 0;

    /// <summary>Sprint Controle-Remoto-TP, parte 3 (2026-11-17) — espelhamento ATUAL da janela de saída, idem (ver <see cref="TeleprompterOutputWindow.IsMirrored"/>).</summary>
    public bool IsMirrored => _window?.IsMirrored ?? false;

    /// <summary>
    /// Sprint Controle-Remoto-TP, parte 2 (2026-11-17, pedido do usuário "2 segundos e muito,
    /// precisamos pensar outra alternativa" ao polling de 2s da dashboard web): disparado sempre que
    /// <see cref="IsManualOverride"/> ou <see cref="CurrentText"/> muda de verdade (nunca em chamadas
    /// que acabam sendo um no-op, ex.: <see cref="SetManualOverride"/> repetindo o mesmo estado) — o
    /// <c>PlaylistDashboardCoordinator</c> assina este evento uma única vez (construtor) e manda pro
    /// <c>PlaylistSseChannel</c> da rota <c>/api/teleprompter/stream</c>, pra dashboard ficar sabendo
    /// na hora, sem precisar perguntar de novo e de novo. Pode disparar em qualquer thread (ver
    /// <see cref="SetManualOverride"/>/<see cref="SetManualText"/>, chamados de uma thread de
    /// requisição HTTP, dentro de um <c>Dispatcher.Invoke</c> — então este evento também dispara na
    /// UI thread nesses casos); quem assina nunca deve tocar em nada de WPF aqui dentro, só
    /// serializar/mandar pela rede (ver <c>PlaylistDashboardCoordinator.OnTeleprompterStateChanged</c>).
    /// </summary>
    public event Action? StateChanged;

    private void RaiseStateChanged() => StateChanged?.Invoke();

    public TeleprompterViewModel(DeckViewModel deckA, DeckViewModel deckB, Action<string> onStatus)
    {
        _deckA = deckA ?? throw new ArgumentNullException(nameof(deckA));
        _deckB = deckB ?? throw new ArgumentNullException(nameof(deckB));
        _onStatus = onStatus ?? throw new ArgumentNullException(nameof(onStatus));

        _voiceEngine.ScriptPositionChanged += OnScriptPositionChanged;
        _voiceEngine.StatusChanged += OnVoiceStatusChanged;
    }

    /// <summary>
    /// Abre/fecha/reposiciona a janela de saída e liga/desliga/reconfigura o motor de voz — seguro
    /// chamar a qualquer momento (idempotente, mesmo princípio de <see cref="DeckViewModel.RefreshScreenOutputs"/>):
    /// só faz o trabalho pesado (religar microfone, reposicionar janela) quando algo relevante
    /// realmente mudou desde a última chamada.
    /// </summary>
    public void ApplySettings(TeleprompterSettingsSnapshot settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        _lastSettings = settings;
        _sourceDeckSetting = settings.SourceDeckSetting;

        RefreshWindow(settings);
        RefreshNdi(settings);
        RefreshVoiceEngine(settings);
    }

    /// <summary>
    /// Chamado pelo <c>MainViewModel</c> toda vez que uma Lauda é carregada num Deck (manual ou
    /// avanço automático, mesmos pontos que chamam <c>PlaylistViewModel.MarkLoadedInDeck</c>) — só
    /// tem efeito se <paramref name="deck"/> for o Deck fonte configurado (<see cref="ApplySettings"/>);
    /// carga no outro Deck é ignorada de propósito (decisão confirmada com o usuário: Deck fonte
    /// sempre fixo, nunca segue automaticamente "quem está no ar agora").
    /// </summary>
    public void NotifyDeckLoaded(DeckViewModel deck, string? tpText)
    {
        if (!ReferenceEquals(deck, ResolveSourceDeck()))
        {
            return;
        }

        _autoScriptText = tpText ?? string.Empty;

        if (_isManualOverride)
        {
            // Sprint Controle-Remoto-TP (2026-11-17): enquanto o controle manual estiver ligado (de
            // outro PC, via dashboard), o texto automático da Playlist/Lauda só fica guardado em
            // _autoScriptText pra quando o manual for desligado (ver SetManualOverride) — nunca
            // sobrescreve o que o produtor está controlando manualmente agora, mesmo que outra Lauda
            // seja carregada nesse meio tempo.
            return;
        }

        _currentScriptText = _autoScriptText;
        _window?.SetText(_currentScriptText);
        _voiceEngine.UpdateScript(_currentScriptText);
        RaiseStateChanged();
    }

    /// <summary>
    /// Sprint Controle-Remoto-TP (2026-11-17) — liga/desliga o controle manual remoto, chamado pelo
    /// <c>PlaylistDashboardCoordinator</c> (POST /api/teleprompter/manual da dashboard web), nunca
    /// por nenhuma tela da própria Cartucheira desktop (pedido explícito do usuário: controle de
    /// OUTRO computador). Idempotente (repetir o mesmo estado não faz nada). Decisão confirmada com
    /// o usuário: ligar o manual desliga o acompanhamento por voz junto, automaticamente, sem chave
    /// separada; desligar o manual restaura o texto automático mais recente (<see cref="_autoScriptText"/>)
    /// e religa a voz se a configuração salva pedir (<see cref="RefreshVoiceEngine"/>).
    ///
    /// <see cref="Application.Current"/>?.<c>Dispatcher.Invoke</c> envolvendo o corpo inteiro —
    /// achado AO IMPLEMENTAR a parte 2 desta sprint (canal SSE, 2026-11-17): este método é chamado
    /// pelo <c>PlaylistDashboardCoordinator</c> direto de uma thread de requisição HTTP em segundo
    /// plano (ver <c>PlaylistDashboardHost.ListenLoopAsync</c>/<c>Task.Run</c>), nunca da UI thread —
    /// mas toca em <see cref="_window"/> (<c>TeleprompterOutputWindow.SetText</c>, um objeto WPF só
    /// pode ser tocado pela thread que o criou) e, indiretamente, em <c>_onStatus</c> (atualiza uma
    /// propriedade vinculada por binding). Mesmo contrato de sempre deste projeto pra chamadas que
    /// chegam de fora da UI thread (ver <see cref="OnScriptPositionChanged"/>/
    /// <see cref="OnVoiceStatusChanged"/>) — <c>Invoke</c> (não <c>BeginInvoke</c>) de propósito:
    /// bloqueia a thread HTTP até a UI thread terminar, porque o chamador (<c>HandleSetTeleprompterManual</c>)
    /// precisa ler <see cref="IsManualOverride"/>/<see cref="CurrentText"/> JÁ atualizados pra montar
    /// a resposta HTTP.
    /// </summary>
    public void SetManualOverride(bool enabled)
    {
        Application.Current?.Dispatcher.Invoke(() =>
        {
            if (_isManualOverride == enabled)
            {
                return;
            }

            _isManualOverride = enabled;

            if (enabled)
            {
                _voiceEngine.Stop();
                _onStatus("Teleprompter: controle manual ligado (remoto) — acompanhamento automático da Playlist e por voz desligados até desligar o manual.");
            }
            else
            {
                _currentScriptText = _autoScriptText;
                _window?.SetText(_currentScriptText);
                _voiceEngine.UpdateScript(_currentScriptText);

                if (_lastSettings is not null)
                {
                    RefreshVoiceEngine(_lastSettings);
                }

                _onStatus("Teleprompter: controle manual desligado — voltou ao automático (Playlist/Lauda e voz, conforme Configurações).");
            }

            RaiseStateChanged();
        });
    }

    /// <summary>
    /// Sprint Controle-Remoto-TP (2026-11-17) — define o texto exibido manualmente, chamado pelo
    /// <c>PlaylistDashboardCoordinator</c> (POST /api/teleprompter/text). Só tem efeito se o
    /// controle manual estiver ligado (<see cref="SetManualOverride"/>) — ignorado de propósito fora
    /// desse estado, pra uma requisição atrasada/fora de ordem nunca sobrescrever o texto automático
    /// por engano. Mesmo <c>Dispatcher.Invoke</c> de <see cref="SetManualOverride"/> e pelo mesmo
    /// motivo — chamado de uma thread de requisição HTTP, toca em <see cref="_window"/>.
    /// </summary>
    public void SetManualText(string text)
    {
        Application.Current?.Dispatcher.Invoke(() =>
        {
            if (!_isManualOverride)
            {
                return;
            }

            _currentScriptText = text ?? string.Empty;
            _window?.SetText(_currentScriptText);
            RaiseStateChanged();
        });
    }

    /// <summary>
    /// Sprint Controle-Remoto-TP, parte 3 (2026-11-17, pedido do usuário "pesquise sobre software de
    /// tp e controles pra colocar nele pra poder controlar manual" — pesquisa confirmou Play/Pause
    /// com velocidade como o controle mais básico/universal de qualquer teleprompter remoto,
    /// RemoteCue/PromptSmart Pro/TeleCuePro) — liga/desliga a rolagem automática por TEMPO (não por
    /// voz nem Lauda), controlada pela dashboard web. Só tem efeito com o manual ligado (mesmo
    /// motivo de <see cref="SetManualText"/>: fora do manual é a Playlist/voz que decide a rolagem,
    /// nunca isto) — ignorado silenciosamente fora desse estado. Desligar o manual
    /// (<see cref="SetManualOverride"/>) também para isto automaticamente (via <see cref="StopAutoScrollTimer"/>
    /// chamado de dentro de <see cref="RefreshWindow"/>/<see cref="Dispose"/>), pra nunca ficar um
    /// Play rolando sozinho depois que o automático volta — ver <see cref="OnAutoScrollTick"/> pro
    /// auto-pause ao chegar no fim do texto.
    /// </summary>
    public void SetAutoScrollPlaying(bool playing)
    {
        Application.Current?.Dispatcher.Invoke(() =>
        {
            if (!_isManualOverride || _autoScrollPlaying == playing)
            {
                return;
            }

            _autoScrollPlaying = playing;

            if (playing)
            {
                StartAutoScrollTimer();
            }
            else
            {
                StopAutoScrollTimer();
            }

            RaiseStateChanged();
        });
    }

    /// <summary>Sprint Controle-Remoto-TP, parte 3 (2026-11-17) — troca a velocidade (1-10, ver <see cref="_autoScrollSpeed"/>); tem efeito mesmo com o Play pausado (só aplica no próximo tick, se/quando o Play for ligado) — sem gate de manual, trocar um número guardado não tem nenhum efeito colateral por si só.</summary>
    public void SetAutoScrollSpeed(int speed)
    {
        Application.Current?.Dispatcher.Invoke(() =>
        {
            var clamped = Math.Clamp(speed, 1, 10);
            if (_autoScrollSpeed == clamped)
            {
                return;
            }

            _autoScrollSpeed = clamped;
            RaiseStateChanged();
        });
    }

    /// <summary>Sprint Controle-Remoto-TP, parte 3 (2026-11-17) — passo manual pra cima (mesma ação da seta/roda do mouse locais, ver <see cref="TeleprompterOutputWindow.StepScrollUp"/>), só com o manual ligado.</summary>
    public void StepScrollUp() => StepScroll(up: true);

    /// <summary>Idem, passo manual pra baixo.</summary>
    public void StepScrollDown() => StepScroll(up: false);

    private void StepScroll(bool up)
    {
        Application.Current?.Dispatcher.Invoke(() =>
        {
            if (!_isManualOverride || _window is null)
            {
                return;
            }

            if (up)
            {
                _window.StepScrollUp();
            }
            else
            {
                _window.StepScrollDown();
            }

            RaiseStateChanged();
        });
    }

    /// <summary>Sprint Controle-Remoto-TP, parte 3 (2026-11-17) — volta pro início do texto (mesma ação da tecla Home local), só com o manual ligado.</summary>
    public void JumpToStart()
    {
        Application.Current?.Dispatcher.Invoke(() =>
        {
            if (!_isManualOverride || _window is null)
            {
                return;
            }

            _window.ScrollToStart();
            RaiseStateChanged();
        });
    }

    /// <summary>Sprint Controle-Remoto-TP, parte 3 (2026-11-17) — pula direto pra uma posição 0..1 do texto, usado pela barra arrastável (scrubber) do controle remoto, só com o manual ligado.</summary>
    public void SetScrollFractionRemote(double fraction)
    {
        Application.Current?.Dispatcher.Invoke(() =>
        {
            if (!_isManualOverride || _window is null)
            {
                return;
            }

            _window.SetScrollFraction(fraction);
            RaiseStateChanged();
        });
    }

    /// <summary>
    /// Sprint Controle-Remoto-TP, parte 3 (2026-11-17) — 1-10 pra pixels/segundo, linear (10px/s na
    /// velocidade 1, 145px/s na velocidade 10) — palpite honesto, sem nenhum precedente nesta base
    /// pra calibrar contra (ver comentário de <see cref="_autoScrollSpeed"/>).
    /// </summary>
    private static double PixelsPerSecondForSpeed(int speed) => 10 + ((speed - 1) * 15);

    private void StartAutoScrollTimer()
    {
        if (_autoScrollTimer is not null)
        {
            return;
        }

        _autoScrollTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = AutoScrollTickInterval };
        _autoScrollTimer.Tick += (_, _) => OnAutoScrollTick();
        _autoScrollTimer.Start();
    }

    private void StopAutoScrollTimer()
    {
        if (_autoScrollTimer is null)
        {
            return;
        }

        _autoScrollTimer.Stop();
        _autoScrollTimer = null;
    }

    /// <summary>
    /// Chamado a cada tick do <see cref="_autoScrollTimer"/> — avança a rolagem um pouquinho (ver
    /// <see cref="PixelsPerSecondForSpeed"/>) e pausa automaticamente ao chegar no fim do texto
    /// (<see cref="TeleprompterOutputWindow.GetScrollFraction"/> em 1.0) — sem isso o timer ficaria
    /// rodando pra sempre sem nenhum efeito visível depois do fim, e o Play remoto continuaria
    /// mostrando "tocando" mesmo parado de verdade. Chamar <see cref="SetAutoScrollPlaying"/> daqui
    /// dentro é seguro (sem deadlock): este tick já roda na UI thread (todo <c>DispatcherTimer.Tick</c>
    /// roda nela), e <c>Dispatcher.Invoke</c> chamado da própria thread do Dispatcher executa
    /// imediatamente, sem bloquear nem esperar nada.
    /// </summary>
    private void OnAutoScrollTick()
    {
        if (_window is null)
        {
            return;
        }

        var pixelsPerTick = PixelsPerSecondForSpeed(_autoScrollSpeed) * AutoScrollTickInterval.TotalSeconds;
        _window.AdvanceScrollByPixels(pixelsPerTick);

        if (_window.GetScrollFraction() >= 1.0)
        {
            SetAutoScrollPlaying(false);
        }
    }

    /// <summary>
    /// Sprint Controle-Remoto-TP, parte 3 (2026-11-17): liga junto com <see cref="_window"/> (ver
    /// <see cref="RefreshWindow"/>) — barato o bastante (só lê um double, nunca renderiza nada) pra
    /// ficar sempre rodando enquanto existir uma janela, independente do NDI/controle manual estarem
    /// ligados — é o que garante que o espelho da dashboard web acompanha QUALQUER rolagem (voz,
    /// teclado local, Play remoto, passo/arrasto remoto), não só as que já chamam
    /// <see cref="RaiseStateChanged"/> diretamente.
    /// </summary>
    private void StartScrollSyncTimer()
    {
        if (_scrollSyncTimer is not null)
        {
            return;
        }

        _lastKnownScrollFraction = _window?.GetScrollFraction() ?? 0;
        _scrollSyncTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = ScrollSyncInterval };
        _scrollSyncTimer.Tick += (_, _) => OnScrollSyncTick();
        _scrollSyncTimer.Start();
    }

    private void StopScrollSyncTimer()
    {
        if (_scrollSyncTimer is null)
        {
            return;
        }

        _scrollSyncTimer.Stop();
        _scrollSyncTimer = null;
    }

    private void OnScrollSyncTick()
    {
        var fraction = _window?.GetScrollFraction() ?? 0;
        if (Math.Abs(fraction - _lastKnownScrollFraction) <= 0.0005)
        {
            return;
        }

        _lastKnownScrollFraction = fraction;
        RaiseStateChanged();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _voiceEngine.ScriptPositionChanged -= OnScriptPositionChanged;
        _voiceEngine.StatusChanged -= OnVoiceStatusChanged;
        _voiceEngine.Dispose();

        StopNdi();
        StopAutoScrollTimer();
        StopScrollSyncTimer();

        _window?.Close();
        _window = null;
    }

    private DeckViewModel ResolveSourceDeck() => _sourceDeckSetting == TeleprompterSourceDeck.DeckA ? _deckA : _deckB;

    /// <summary>
    /// Mesma proteção de sempre (ver <see cref="DeckViewModel.RefreshScreenOutputs"/>): nunca abre
    /// a janela de Teleprompter no monitor onde roda alguma janela de verdade da própria Cartucheira
    /// agora (MainWindow, SettingsWindow etc.) — reaproveita
    /// <see cref="DeckViewModel.GetOperatorScreenDeviceNames"/>, mesma checagem, sem duplicar a
    /// lógica. Diferente de lá (vários monitores possíveis por Deck), aqui é no máximo UM monitor —
    /// só abre/fecha/reposiciona essa única janela.
    ///
    /// Sprint Teleprompter-NDI (2026-11-17): a janela deixou de existir só por causa de uma tela
    /// física escolhida — agora também existe (escondida, fora de qualquer monitor real, ver
    /// <see cref="TeleprompterOutputWindow.PositionOffScreen"/>) quando <see cref="TeleprompterSettingsSnapshot.NdiEnabled"/>
    /// está ligado, mesmo sem nenhuma tela escolhida/conectada — é exatamente o cenário que motivou
    /// o pedido ("ainda preciso colocar placa de vídeo com mais saídas [...] vamos colocar para o
    /// teleprompter gerar tb em ndi"): sem monitor físico sobrando, o NDI vira o único destino.
    /// Quando tela física E NDI estão ligados ao mesmo tempo, é a MESMA janela alimentando os dois —
    /// <see cref="RefreshNdi"/> (chamado depois deste método, ver <see cref="ApplySettings"/>) só
    /// lê o que já está desenhado aqui, nunca cria uma segunda janela.
    /// </summary>
    private void RefreshWindow(TeleprompterSettingsSnapshot settings)
    {
        var operatorDeviceNames = DeckViewModel.GetOperatorScreenDeviceNames();
        var wantedDeviceName = settings.ScreenOutputDeviceName;

        if (wantedDeviceName is not null && operatorDeviceNames.Contains(wantedDeviceName))
        {
            _onStatus("Aviso: a tela marcada pro Teleprompter não foi aberta porque é a mesma tela onde a Cartucheira está sendo usada agora — isso evita travar sua tela. Escolha outra tela em Configurações → Teleprompter.");
            wantedDeviceName = null;
        }

        var targetScreen = wantedDeviceName is null
            ? null
            : System.Windows.Forms.Screen.AllScreens.FirstOrDefault(s => s.DeviceName == wantedDeviceName);

        var needsWindow = targetScreen is not null || settings.NdiEnabled;

        if (!needsWindow)
        {
            // Nenhuma tela escolhida/conectada E NDI desligado — mesma "falha honesta" de sempre: a
            // seleção de tela continua salva, só não existe janela nenhuma aberta enquanto isso.
            // Sprint Controle-Remoto-TP, parte 3 (2026-11-17): sem janela não tem o que rolar nem o
            // que sincronizar — para os dois timers novos (autoscroll e scroll-sync) e reseta o
            // estado de "tocando", senão a dashboard remota ficaria mostrando Play ligado pra sempre
            // apontando pra uma janela que não existe mais.
            StopAutoScrollTimer();
            StopScrollSyncTimer();
            _autoScrollPlaying = false;
            _window?.Close();
            _window = null;
            return;
        }

        var wantedDeviceIdentity = targetScreen?.DeviceName ?? OffScreenDeviceMarker;

        if (_window is null || _window.DeviceName != wantedDeviceIdentity)
        {
            _window?.Close();
            _window = new TeleprompterOutputWindow(wantedDeviceIdentity);
            _window.Closed += (_, _) => _window = null;
            _window.SetText(_currentScriptText);
            _window.Show();

            if (targetScreen is not null)
            {
                _window.PositionOnScreen(targetScreen);
            }
            else
            {
                _window.PositionOffScreen();
            }

            // Sprint Controle-Remoto-TP, parte 3 (2026-11-17): janela nova = start do timer de
            // sincronismo de rolagem (ver comentário de <see cref="_scrollSyncTimer"/>) — sem isso o
            // espelho da dashboard nunca saberia que a posição de leitura mudou.
            StartScrollSyncTimer();
        }

        _window.SetMirror(settings.MirrorMode);
        _window.SetFontSize(settings.FontSizePt);
    }

    /// <summary>
    /// Sprint Teleprompter-NDI (2026-11-17): liga/desliga/renomeia a fonte NDI do Teleprompter e o
    /// timer que a alimenta — chamado depois de <see cref="RefreshWindow"/> (ver
    /// <see cref="ApplySettings"/>), porque depende da janela já existir/estar no tamanho certo
    /// quando o primeiro quadro for renderizado. Diferente do <c>NdiSender</c> de cada Deck (criado
    /// uma única vez no construtor, "sempre quente" — ver comentário de
    /// <see cref="DeckViewModel"/>, limitação documentada de exigir reiniciar o programa pra
    /// renomear o canal), este é recriado dinamicamente aqui sempre que o nome mudar — este módulo é
    /// opt-in (o operador liga/desliga quando quiser), não existe desde a inicialização do programa
    /// como o de cada Deck.
    /// </summary>
    private void RefreshNdi(TeleprompterSettingsSnapshot settings)
    {
        if (!settings.NdiEnabled)
        {
            StopNdi();
            return;
        }

        var wantedName = ResolveNdiChannelName(settings.NdiChannelName);

        if (_ndiSender is null || _ndiChannelName != wantedName)
        {
            StopNdi();

            try
            {
                _ndiSender = new NdiSender(wantedName);
                _ndiChannelName = wantedName;
            }
            catch (Exception ex)
            {
                // Mesma falha honesta de sempre (ver construtor de DeckViewModel): DLL nativa
                // ausente/NDI Runtime não instalado não pode derrubar o Teleprompter — ele continua
                // funcionando normalmente (tela física, se houver, controle manual/voz), só sem
                // saída NDI.
                _ndiSender = null;
                _ndiChannelName = null;
                _onStatus($"Teleprompter: NDI indisponível ({ex.Message})");
                return;
            }
        }

        StartNdiTimer();
    }

    private static string ResolveNdiChannelName(string? configured) =>
        string.IsNullOrWhiteSpace(configured) ? "Teleprompter" : configured.Trim();

    private void StartNdiTimer()
    {
        if (_ndiTimer is not null)
        {
            return;
        }

        // DispatcherTimer (não System.Threading.Timer, diferente do timer de Arte de Fundo do
        // Deck) de propósito: cada tick precisa chamar TeleprompterOutputWindow.RenderToUyvy, que
        // usa RenderTargetBitmap.Render — operação WPF normal, só funciona na UI thread da própria
        // janela. DispatcherPriority.Background: não compete com entrada de teclado/mouse/pintura
        // da UI do operador, só roda quando a UI thread está livre.
        _ndiTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = NdiFrameInterval };
        _ndiTimer.Tick += (_, _) => SendNdiFrame();
        _ndiTimer.Start();
    }

    private void StopNdi()
    {
        if (_ndiTimer is not null)
        {
            _ndiTimer.Stop();
            _ndiTimer = null;
        }

        _ndiSender?.Dispose();
        _ndiSender = null;
        _ndiChannelName = null;
    }

    /// <summary>
    /// Chamado a cada tick do <see cref="_ndiTimer"/> (ver <see cref="StartNdiTimer"/>) — renderiza
    /// o quadro atual da janela e manda pro <see cref="_ndiSender"/>. Falha honesta em qualquer
    /// etapa (janela ainda sem layout, sendo fechada no mesmo instante etc.): só pula este quadro,
    /// nunca derruba o timer nem o Teleprompter.
    ///
    /// Buffer nativo NOVO a cada chamada (nunca reusado) — mesmo princípio já usado em
    /// <see cref="Ndi.NdiSender.SendAudioFrame"/> (ver comentário de classe completo de
    /// <c>Ndi.NdiSender</c> sobre os sete crashes reais que motivaram essa escolha do lado do
    /// áudio, e os dois dumps nativos reais — v1.0.70/v1.0.72 — do lado do vídeo quando um buffer
    /// ÚNICO reusado foi liberado sem sincronizar primeiro). <see cref="Ndi.NdiSender.SyncPendingVideoFrame"/>
    /// logo depois do <see cref="Ndi.NdiSender.SendFrame"/> força a SDK a terminar de ler ESTE
    /// ponteiro antes deste método liberar a memória no <c>finally</c> — sacrifica parte do ganho de
    /// performance da variante assíncrona (bloqueia brevemente aqui dentro), aceitável porque este
    /// tick roda só ~10x/s (<see cref="NdiFrameInterval"/>), não é um caminho quente como o vídeo
    /// real do Engine (até 60x/s) que motivou a variante assíncrona originalmente.
    /// </summary>
    private void SendNdiFrame()
    {
        if (_ndiSender is null)
        {
            return;
        }

        var frame = _window?.RenderToUyvy();
        if (frame is null)
        {
            return;
        }

        var (uyvy, width, height) = frame.Value;
        var strideBytes = width * 2;

        var nativeBuffer = Marshal.AllocHGlobal(uyvy.Length);
        try
        {
            Marshal.Copy(uyvy, 0, nativeBuffer, uyvy.Length);
            _ndiSender.SendFrame(nativeBuffer, width, height, strideBytes);
            _ndiSender.SyncPendingVideoFrame();
        }
        finally
        {
            Marshal.FreeHGlobal(nativeBuffer);
        }
    }

    /// <summary>
    /// Sprint Controle-Remoto-TP (2026-11-17): <see cref="_isManualOverride"/> checado ANTES de
    /// <see cref="TeleprompterSettingsSnapshot.VoiceTrackingEnabled"/> — garante a decisão
    /// confirmada com o usuário ("manual desliga a voz junto") nos dois pontos que chamam este
    /// método (<see cref="ApplySettings"/>, quando a Configuração é salva enquanto o manual já está
    /// ligado; e <see cref="SetManualOverride"/>, ao desligar o manual), sem duplicar a checagem.
    /// </summary>
    private void RefreshVoiceEngine(TeleprompterSettingsSnapshot settings)
    {
        _voiceEngine.Stop();

        if (_isManualOverride || !settings.VoiceTrackingEnabled)
        {
            return;
        }

        var (ok, error) = _voiceEngine.Start(settings.AudioInputDeviceId, _currentScriptText);
        if (!ok && error is not null)
        {
            _onStatus($"Teleprompter: {error}");
        }
    }

    /// <summary>Nasce na thread de captura do NAudio (ver <see cref="TeleprompterVoiceEngine.ScriptPositionChanged"/>) — marshala pra UI thread antes de tocar na janela, mesmo padrão de sempre deste projeto (ver <c>MainViewModel</c>, vários pontos com <c>Application.Current?.Dispatcher.Invoke</c>).</summary>
    private void OnScriptPositionChanged(object? sender, int charOffset)
    {
        Application.Current?.Dispatcher.Invoke(() => _window?.SetScriptPosition(charOffset));
    }

    /// <summary>Idem — status textual do motor de voz vira mensagem na barra de status de sempre do programa.</summary>
    private void OnVoiceStatusChanged(object? sender, string status)
    {
        Application.Current?.Dispatcher.Invoke(() => _onStatus($"Teleprompter: {status}"));
    }
}
