using System.Linq;
using CartucheiraVideo.Configuration;
using CartucheiraVideo.Engine;
using CartucheiraVideo.Presentation.Mvvm;

namespace CartucheiraVideo.Presentation.ViewModels;

/// <summary>
/// ViewModel da oitava seção da janela de Configurações — "Teleprompter" (Sprint Teleprompter,
/// 2026-11-16, pedido literal do usuário: "você cria uma abinha teleprompter, saída de vídeo, e
/// modo invertido ou não [...] vai ter uma configuração lá para input de áudio"). Mesmo padrão
/// simples de <see cref="BackgroundArtSettingsViewModel"/>/<see cref="PlaylistSettingsViewModel"/> —
/// só o <see cref="AppSettingsSnapshot"/>, sem lista compartilhada com outra seção.
///
/// Diferente da aba "Telas" de cada Deck (<see cref="DeckSettingsViewModel.AvailableScreens"/>, uma
/// lista de checkboxes — vários monitores ao mesmo tempo): aqui <see cref="SelectedScreenDeviceName"/>
/// é SELEÇÃO ÚNICA, porque só existe UMA janela de saída do Teleprompter (ver
/// <see cref="TeleprompterViewModel"/>). Mesmo princípio de "falha honesta" de
/// <see cref="DeckSettingsViewModel.BuildAvailableScreens"/> pra um monitor salvo que não está mais
/// conectado — a seleção continua lá, só marcada como "não detectada agora".
/// </summary>
public sealed class TeleprompterSettingsViewModel : ViewModelBase
{
    private string? _selectedScreenDeviceName;
    private bool _mirrorMode;
    private string? _selectedAudioInputDeviceId;
    private bool _voiceTrackingEnabled;
    private TeleprompterSourceDeck _sourceDeckSetting;
    private double _fontSizePt;
    private bool _ndiEnabled;
    private string _ndiChannelName;

    public TeleprompterSettingsViewModel(AppSettingsSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var teleprompter = snapshot.Teleprompter;

        ScreenOptions = BuildScreenOptions(teleprompter.ScreenOutputDeviceName);
        _selectedScreenDeviceName = teleprompter.ScreenOutputDeviceName;

        AudioInputOptions = BuildAudioInputOptions();
        _selectedAudioInputDeviceId = teleprompter.AudioInputDeviceId;

        _mirrorMode = teleprompter.MirrorMode;
        _voiceTrackingEnabled = teleprompter.VoiceTrackingEnabled;
        _sourceDeckSetting = teleprompter.SourceDeckSetting;
        _fontSizePt = teleprompter.FontSizePt;
        _ndiEnabled = teleprompter.NdiEnabled;
        _ndiChannelName = teleprompter.NdiChannelName ?? string.Empty;
    }

    /// <summary>Opções do combo "Tela de saída" — "Nenhuma" (Value nulo, fica sem janela de Teleprompter nenhuma) no topo + uma por monitor detectado agora, mesmo padrão de combo "Nenhuma"/escolha explícita já usado por <see cref="BackgroundArtSettingsViewModel.ActiveOptions"/>.</summary>
    public IReadOnlyList<ComboOption<string?>> ScreenOptions { get; }

    /// <summary><c>System.Windows.Forms.Screen.DeviceName</c> da tela escolhida pro Teleprompter, ou nulo = nenhuma (sem janela de saída).</summary>
    public string? SelectedScreenDeviceName
    {
        get => _selectedScreenDeviceName;
        set => SetProperty(ref _selectedScreenDeviceName, value);
    }

    /// <summary>"Modo invertido" — espelha o texto horizontalmente, só faz sentido com vidro/espelho de 45° (beam splitter) na frente da câmera. Ver comentário completo em <see cref="TeleprompterSettingsSnapshot.MirrorMode"/>.</summary>
    public bool MirrorMode
    {
        get => _mirrorMode;
        set => SetProperty(ref _mirrorMode, value);
    }

    /// <summary>Opções do combo "Entrada de áudio" — "Padrão do sistema" (Value nulo) no topo + um microfone por dispositivo WASAPI de captura detectado agora, mesmo padrão de <see cref="DeckSettingsViewModel.AudioDevices"/> (lá são saídas; aqui é a única entrada deste projeto).</summary>
    public IReadOnlyList<ComboOption<string?>> AudioInputOptions { get; }

    /// <summary>Id do microfone (WASAPI) usado pro acompanhamento automático por voz — nulo = padrão do sistema.</summary>
    public string? SelectedAudioInputDeviceId
    {
        get => _selectedAudioInputDeviceId;
        set => SetProperty(ref _selectedAudioInputDeviceId, value);
    }

    /// <summary>Liga/desliga o acompanhamento automático por voz — o controle manual (seta/roda do mouse na janela de saída) continua disponível nos dois estados, nunca só isto. Ver comentário completo em <see cref="TeleprompterSettingsSnapshot.VoiceTrackingEnabled"/>.</summary>
    public bool VoiceTrackingEnabled
    {
        get => _voiceTrackingEnabled;
        set => SetProperty(ref _voiceTrackingEnabled, value);
    }

    /// <summary>Opções do combo "Deck fonte" — "Deck 1"/"Deck 2", mesmo vocabulário já usado pelo resto da Configuração (<c>Deck1Settings</c>/<c>Deck2Settings</c>).</summary>
    public IReadOnlyList<ComboOption<TeleprompterSourceDeck>> SourceDeckOptions { get; } = new[]
    {
        new ComboOption<TeleprompterSourceDeck> { Value = TeleprompterSourceDeck.DeckA, Label = "Deck 1" },
        new ComboOption<TeleprompterSourceDeck> { Value = TeleprompterSourceDeck.DeckB, Label = "Deck 2" }
    };

    /// <summary>Qual Deck é "o apresentador" — sempre fixo (decisão confirmada com o usuário: nunca troca sozinho qual Deck o Teleprompter segue, mesmo com os dois Decks tocando ao mesmo tempo).</summary>
    public TeleprompterSourceDeck SourceDeckSetting
    {
        get => _sourceDeckSetting;
        set => SetProperty(ref _sourceDeckSetting, value);
    }

    /// <summary>Tamanho da fonte (pontos) do texto na janela de saída — clamp defensivo (12-200), mesmo padrão já usado por outras portas/campos numéricos deste programa.</summary>
    public double FontSizePt
    {
        get => _fontSizePt;
        set => SetProperty(ref _fontSizePt, Math.Clamp(value, 12, 200));
    }

    /// <summary>
    /// Sprint Teleprompter-NDI (2026-11-17) — liga/desliga a saída NDI do Teleprompter,
    /// independente da <see cref="SelectedScreenDeviceName"/> (ver comentário completo em
    /// <see cref="TeleprompterSettingsSnapshot.NdiEnabled"/>). Pensado pra quem não tem mais saída
    /// de monitor livre na placa de vídeo.
    /// </summary>
    public bool NdiEnabled
    {
        get => _ndiEnabled;
        set => SetProperty(ref _ndiEnabled, value);
    }

    /// <summary>Nome do canal NDI — vazio usa o padrão "Teleprompter" (ver <see cref="TeleprompterSettingsSnapshot.NdiChannelName"/>).</summary>
    public string NdiChannelName
    {
        get => _ndiChannelName;
        set => SetProperty(ref _ndiChannelName, value);
    }

    /// <summary>Escreve os valores editados de volta no snapshot real — chamado só quando o Salvar global do SettingsWindow é clicado, mesmo padrão de <see cref="BackgroundArtSettingsViewModel.ApplyTo"/>.</summary>
    public void ApplyTo(AppSettingsSnapshot snapshot)
    {
        var teleprompter = snapshot.Teleprompter;
        teleprompter.ScreenOutputDeviceName = SelectedScreenDeviceName;
        teleprompter.MirrorMode = MirrorMode;
        teleprompter.AudioInputDeviceId = SelectedAudioInputDeviceId;
        teleprompter.VoiceTrackingEnabled = VoiceTrackingEnabled;
        teleprompter.SourceDeckSetting = SourceDeckSetting;
        teleprompter.FontSizePt = FontSizePt;
        teleprompter.NdiEnabled = NdiEnabled;
        teleprompter.NdiChannelName = string.IsNullOrWhiteSpace(NdiChannelName) ? null : NdiChannelName.Trim();
    }

    private static List<ComboOption<string?>> BuildScreenOptions(string? persistedDeviceName)
    {
        var options = new List<ComboOption<string?>>
        {
            new() { Value = null, Label = "Nenhuma (sem janela de Teleprompter)" }
        };

        var detectedNames = new HashSet<string>();
        var detectedScreens = System.Windows.Forms.Screen.AllScreens;
        for (var i = 0; i < detectedScreens.Length; i++)
        {
            var screen = detectedScreens[i];
            detectedNames.Add(screen.DeviceName);
            options.Add(new ComboOption<string?>
            {
                Value = screen.DeviceName,
                Label = $"Tela {i + 1} — {screen.Bounds.Width}x{screen.Bounds.Height} em ({screen.Bounds.X},{screen.Bounds.Y})"
            });
        }

        if (!string.IsNullOrEmpty(persistedDeviceName) && !detectedNames.Contains(persistedDeviceName))
        {
            options.Add(new ComboOption<string?> { Value = persistedDeviceName, Label = $"{persistedDeviceName} (não detectada agora)" });
        }

        return options;
    }

    private static List<ComboOption<string?>> BuildAudioInputOptions()
    {
        var options = new List<ComboOption<string?>>
        {
            new() { Value = null, Label = "Padrão do sistema" }
        };

        options.AddRange(AudioEngine.EnumerateInputDevices().Select(device => new ComboOption<string?> { Value = device.DeviceId, Label = device.Description }));

        return options;
    }
}
