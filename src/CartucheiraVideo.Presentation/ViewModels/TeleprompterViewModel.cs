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

        _currentScriptText = tpText ?? string.Empty;
        _window?.SetText(_currentScriptText);
        _voiceEngine.UpdateScript(_currentScriptText);
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

    private void RefreshVoiceEngine(TeleprompterSettingsSnapshot settings)
    {
        _voiceEngine.Stop();

        if (!settings.VoiceTrackingEnabled)
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
