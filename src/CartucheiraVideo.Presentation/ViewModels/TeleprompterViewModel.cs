using System.Linq;
using System.Windows;
using CartucheiraVideo.Configuration;
using CartucheiraVideo.Engine;
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

        if (targetScreen is null)
        {
            // Nenhuma tela escolhida, ou a escolhida não está conectada agora (placa desligada/
            // desconectada) — mesma "falha honesta" de sempre: a seleção continua salva, só não
            // existe janela nenhuma aberta enquanto isso.
            _window?.Close();
            _window = null;
            return;
        }

        if (_window is null || _window.DeviceName != targetScreen.DeviceName)
        {
            _window?.Close();
            _window = new TeleprompterOutputWindow(targetScreen.DeviceName);
            _window.Closed += (_, _) => _window = null;
            _window.SetText(_currentScriptText);
            _window.Show();
            _window.PositionOnScreen(targetScreen);
        }

        _window.SetMirror(settings.MirrorMode);
        _window.SetFontSize(settings.FontSizePt);
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
