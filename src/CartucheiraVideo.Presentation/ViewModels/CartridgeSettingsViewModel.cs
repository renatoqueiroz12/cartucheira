using System.Collections.Generic;
using System.Linq;
using System.Windows.Input;
using CartucheiraVideo.Api;
using CartucheiraVideo.Configuration;
using CartucheiraVideo.Engine;
using CartucheiraVideo.Infrastructure;
using CartucheiraVideo.Presentation.Mvvm;
using Microsoft.Win32;

namespace CartucheiraVideo.Presentation.ViewModels;

/// <summary>
/// ViewModel da seção "Cartucheira" dentro da janela única de Configurações — Sprint Cartucheira
/// (2026-08-09, parte 2 do item 2 do roteiro). Cobre só o que o motor de áudio real precisa pra
/// funcionar: um dispositivo de saída de áudio do Windows por Cartucheira (Vinhetas e Trilhas
/// independentes — decisão do usuário) e a pasta de onde os cartuchos daquela Cartucheira são
/// lidos. Cartucho de vídeo (arquitetura §14.5) continua fora do escopo.
///
/// Sprint Comando Externo (2026-08-10): as propriedades de <see cref="VinhetasExternalTriggerEnabled"/>/
/// <see cref="TrilhasExternalTriggerEnabled"/> em diante cobrem TRÊS canais de disparo externo por
/// Cartucheira, todos controlados de ponta a ponta por este projeto (sem bloqueio de protocolo de
/// terceiro) — teclado (atalho global Win32, <see cref="CartucheiraVideo.Infrastructure.GlobalHotkeyService"/>),
/// Stream Deck/Companion (HTTP local, <see cref="CartucheiraVideo.Api.LocalTriggerApiHost"/>) e,
/// desde a correção pós-entrega desta mesma data, a mesa de áudio real (UDP local,
/// <see cref="CartucheiraVideo.Api.UdpTriggerListener"/>) — os três já disparam de verdade
/// (<c>MainViewModel.ApplyExternalTriggerSettings</c>/<c>MainViewModel.TryTriggerExternalUdpCommand</c>),
/// nenhum é placeholder. <see cref="VinhetasEndpointUrl"/>/<see cref="TrilhasEndpointUrl"/> são só
/// exibição (somente leitura) da URL que o operador cola no Stream Deck/Companion. A porta UDP da
/// mesa é <see cref="SharedUdpPort"/> — UMA porta só, compartilhada por Cartucheira E Deck (quem
/// diferencia é sempre o texto do comando, nunca a porta, confirmado pela tela de configuração
/// real da mesa que o usuário mandou); correção de bug 2026-08-13: era exposta via
/// <c>RelativeSource AncestorType=Window</c>, que não resolvia em tempo de execução — virou objeto
/// compartilhado injetado direto (ver <see cref="SettingsViewModel"/>).
///
/// Mesmo padrão de "editar em memória, só grava quando o Salvar global do SettingsWindow for
/// clicado" já usado por <see cref="DeckSettingsViewModel"/> — ver <see cref="ApplyTo"/>.
///
/// Sprint Crossfade v2 (2026-08-11): <see cref="VinhetasCrossfadeMs"/>/<see cref="TrilhasCrossfadeMs"/>
/// controlam a duração da mistura entre um cartucho terminando e o próximo começando
/// (<c>AudioEngine.Play</c>/<c>AudioBankViewModel.PlayPad</c>) — 0 (padrão) desliga, corte seco de
/// sempre. Reintroduzido depois de uma v1 (mesmo dia) ter sido revertida por instabilidade real —
/// ver o comentário completo em <c>CartucheiraVideo.Engine.AudioEngine</c> sobre a causa raiz e a
/// troca de motor (LibVLCSharp → NAudio) que tornou esta segunda tentativa segura. Também não
/// depende mais de <c>LibVLC</c> nenhuma — <see cref="AudioEngine.EnumerateOutputDevices"/> agora é
/// autossuficiente (NAudio enumera os dispositivos direto).
/// </summary>
public sealed class CartridgeSettingsViewModel : ViewModelBase
{
    private string? _vinhetasAudioDeviceId;
    private string _vinhetasFolderPath;
    private string? _trilhasAudioDeviceId;
    private string _trilhasFolderPath;
    private bool _vinhetasExternalTriggerEnabled;
    private string? _vinhetasKeyboardShortcut;
    private bool _trilhasExternalTriggerEnabled;
    private string? _trilhasKeyboardShortcut;
    private string _vinhetasUdpTriggerOnCommand;
    private string _vinhetasUdpTriggerOffCommand;
    private string _trilhasUdpTriggerOnCommand;
    private string _trilhasUdpTriggerOffCommand;
    private int _vinhetasCrossfadeMs;
    private int _trilhasCrossfadeMs;

    public CartridgeSettingsViewModel(CartridgeSettingsSnapshot snapshot, UdpPortSettingsViewModel sharedUdpPort)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(sharedUdpPort);

        SharedUdpPort = sharedUdpPort;

        var devices = new List<ComboOption<string?>>
        {
            new() { Value = null, Label = "Padrão do sistema" }
        };
        devices.AddRange(AudioEngine.EnumerateOutputDevices()
            .Select(d => new ComboOption<string?> { Value = d.DeviceId, Label = d.Description }));
        AudioDevices = devices;

        var shortcuts = new List<ComboOption<string?>>
        {
            new() { Value = null, Label = "Nenhuma" }
        };
        shortcuts.AddRange(Enum.GetNames<GlobalHotkeyKey>()
            .Select(name => new ComboOption<string?>
            {
                Value = name,
                // Correção pós-entrega (2026-08-10): Tab passou a exigir Ctrl junto
                // (GlobalHotkeyService.Register) — o rótulo precisa deixar isso explícito, senão o
                // operador aperta só Tab (como o nome sozinho sugere) e o atalho não dispara.
                Label = name == nameof(GlobalHotkeyKey.Tab) ? "Ctrl+Tab" : name
            }));
        KeyboardShortcuts = shortcuts;

        _vinhetasAudioDeviceId = snapshot.VinhetasAudioDeviceId;
        _vinhetasFolderPath = snapshot.VinhetasFolderPath ?? string.Empty;
        _trilhasAudioDeviceId = snapshot.TrilhasAudioDeviceId;
        _trilhasFolderPath = snapshot.TrilhasFolderPath ?? string.Empty;
        _vinhetasExternalTriggerEnabled = snapshot.VinhetasExternalTriggerEnabled;
        _vinhetasKeyboardShortcut = snapshot.VinhetasKeyboardShortcut;
        _trilhasExternalTriggerEnabled = snapshot.TrilhasExternalTriggerEnabled;
        _trilhasKeyboardShortcut = snapshot.TrilhasKeyboardShortcut;
        _vinhetasUdpTriggerOnCommand = snapshot.VinhetasUdpTriggerOnCommand;
        _vinhetasUdpTriggerOffCommand = snapshot.VinhetasUdpTriggerOffCommand;
        _trilhasUdpTriggerOnCommand = snapshot.TrilhasUdpTriggerOnCommand;
        _trilhasUdpTriggerOffCommand = snapshot.TrilhasUdpTriggerOffCommand;
        _vinhetasCrossfadeMs = snapshot.VinhetasCrossfadeMs;
        _trilhasCrossfadeMs = snapshot.TrilhasCrossfadeMs;

        BrowseVinhetasFolderCommand = new RelayCommand(_ => BrowseFolder("Escolher pasta de Vinhetas", path => VinhetasFolderPath = path));
        BrowseTrilhasFolderCommand = new RelayCommand(_ => BrowseFolder("Escolher pasta de Trilhas", path => TrilhasFolderPath = path));
    }

    /// <summary>
    /// Porta UDP única (Cartucheira + Deck) — MESMA instância injetada nos dois
    /// <see cref="DeckSettingsViewModel"/> por <see cref="SettingsViewModel"/> (correção de bug
    /// 2026-08-13: antes tentava alcançar isso via <c>RelativeSource AncestorType=Window</c>, que
    /// não resolvia em tempo de execução). A XAML de <c>CartridgeSettingsPanel</c> faz um Binding
    /// local de dois níveis (<c>SharedUdpPort.Port</c>), sem precisar sair do próprio DataContext.
    /// </summary>
    public UdpPortSettingsViewModel SharedUdpPort { get; }

    /// <summary>Dispositivos de saída de áudio do Windows (mmdevice) + a opção "Padrão do sistema" (Value nulo) no topo.</summary>
    public IReadOnlyList<ComboOption<string?>> AudioDevices { get; }

    /// <summary>Teclas de atalho disponíveis (Tab, F1–F24, ver <see cref="GlobalHotkeyKey"/>) + a opção "Nenhuma" (Value nulo) no topo — mesma lista pra Vinhetas e Trilhas.</summary>
    public IReadOnlyList<ComboOption<string?>> KeyboardShortcuts { get; }

    public string? VinhetasAudioDeviceId
    {
        get => _vinhetasAudioDeviceId;
        set => SetProperty(ref _vinhetasAudioDeviceId, value);
    }

    public string VinhetasFolderPath
    {
        get => _vinhetasFolderPath;
        set => SetProperty(ref _vinhetasFolderPath, value);
    }

    public string? TrilhasAudioDeviceId
    {
        get => _trilhasAudioDeviceId;
        set => SetProperty(ref _trilhasAudioDeviceId, value);
    }

    public string TrilhasFolderPath
    {
        get => _trilhasFolderPath;
        set => SetProperty(ref _trilhasFolderPath, value);
    }

    /// <summary>Verdadeiro = Vinhetas recebe comando externo de verdade por teclado, Stream Deck/Companion e mesa de áudio (UDP) — Sprint Comando Externo (2026-08-10).</summary>
    public bool VinhetasExternalTriggerEnabled
    {
        get => _vinhetasExternalTriggerEnabled;
        set => SetProperty(ref _vinhetasExternalTriggerEnabled, value);
    }

    /// <summary>Tecla do atalho global de Vinhetas — nome de <see cref="GlobalHotkeyKey"/> (ex.: "F1", "Tab"), ou nulo (nenhuma escolhida).</summary>
    public string? VinhetasKeyboardShortcut
    {
        get => _vinhetasKeyboardShortcut;
        set => SetProperty(ref _vinhetasKeyboardShortcut, value);
    }

    /// <summary>Verdadeiro = Trilhas recebe comando externo de verdade por teclado, Stream Deck/Companion e mesa de áudio.</summary>
    public bool TrilhasExternalTriggerEnabled
    {
        get => _trilhasExternalTriggerEnabled;
        set => SetProperty(ref _trilhasExternalTriggerEnabled, value);
    }

    /// <summary>Tecla do atalho global de Trilhas — mesma regra de <see cref="VinhetasKeyboardShortcut"/>.</summary>
    public string? TrilhasKeyboardShortcut
    {
        get => _trilhasKeyboardShortcut;
        set => SetProperty(ref _trilhasKeyboardShortcut, value);
    }

    /// <summary>URL completa (somente leitura) que o operador cola na ação "Website"/"HTTP Request" do Stream Deck, ou no Bitfocus Companion, pra disparar Vinhetas — POST, sem corpo.</summary>
    public string VinhetasEndpointUrl => $"http://localhost:{LocalTriggerApiHost.DefaultPort}{LocalTriggerApiHost.VinhetasRoutePath}";

    /// <summary>Mesma ideia de <see cref="VinhetasEndpointUrl"/>, pra Trilhas.</summary>
    public string TrilhasEndpointUrl => $"http://localhost:{LocalTriggerApiHost.DefaultPort}{LocalTriggerApiHost.TrilhasRoutePath}";

    /// <summary>Texto que a mesa manda por UDP pra DISPARAR Vinhetas (coluna "Trigger ON" da mesa) — Sprint Comando Externo, correção pós-entrega (2026-08-10).</summary>
    public string VinhetasUdpTriggerOnCommand
    {
        get => _vinhetasUdpTriggerOnCommand;
        set => SetProperty(ref _vinhetasUdpTriggerOnCommand, value);
    }

    /// <summary>Texto que a mesa manda por UDP pra PARAR Vinhetas (coluna "Trigger OFF" da mesa).</summary>
    public string VinhetasUdpTriggerOffCommand
    {
        get => _vinhetasUdpTriggerOffCommand;
        set => SetProperty(ref _vinhetasUdpTriggerOffCommand, value);
    }

    /// <summary>Mesma ideia de <see cref="VinhetasUdpTriggerOnCommand"/>, pra Trilhas.</summary>
    public string TrilhasUdpTriggerOnCommand
    {
        get => _trilhasUdpTriggerOnCommand;
        set => SetProperty(ref _trilhasUdpTriggerOnCommand, value);
    }

    /// <summary>Mesma ideia de <see cref="VinhetasUdpTriggerOffCommand"/>, pra Trilhas.</summary>
    public string TrilhasUdpTriggerOffCommand
    {
        get => _trilhasUdpTriggerOffCommand;
        set => SetProperty(ref _trilhasUdpTriggerOffCommand, value);
    }

    /// <summary>
    /// Duração (ms) do crossfade entre um cartucho de Vinhetas terminando e o próximo começando —
    /// Sprint Crossfade v2 (2026-08-11). Zero (padrão) = desligado, corte seco de sempre. Faixa
    /// limitada a 0–3000 ms (3 segundos) — acima disso já não é um "crossfade" no sentido que o
    /// usuário pediu ("mixagem de milisegundos"), é os dois cartuchos tocando juntos por tempo
    /// longo demais; o clamp evita um valor digitado errado (ex.: 30000) travar a troca de cartucho
    /// por segundos sem querer.
    /// </summary>
    public int VinhetasCrossfadeMs
    {
        get => _vinhetasCrossfadeMs;
        set => SetProperty(ref _vinhetasCrossfadeMs, Math.Clamp(value, 0, 3000));
    }

    /// <summary>Mesma ideia de <see cref="VinhetasCrossfadeMs"/>, pra Trilhas.</summary>
    public int TrilhasCrossfadeMs
    {
        get => _trilhasCrossfadeMs;
        set => SetProperty(ref _trilhasCrossfadeMs, Math.Clamp(value, 0, 3000));
    }

    public ICommand BrowseVinhetasFolderCommand { get; }

    public ICommand BrowseTrilhasFolderCommand { get; }

    /// <summary>Escreve os valores editados de volta no snapshot real — chamado só quando o Salvar global do SettingsWindow é clicado (mesmo padrão do DeckSettingsViewModel.ApplyTo).</summary>
    public void ApplyTo(CartridgeSettingsSnapshot snapshot)
    {
        snapshot.VinhetasAudioDeviceId = VinhetasAudioDeviceId;
        snapshot.VinhetasFolderPath = string.IsNullOrWhiteSpace(VinhetasFolderPath) ? null : VinhetasFolderPath.Trim();
        snapshot.TrilhasAudioDeviceId = TrilhasAudioDeviceId;
        snapshot.TrilhasFolderPath = string.IsNullOrWhiteSpace(TrilhasFolderPath) ? null : TrilhasFolderPath.Trim();
        snapshot.VinhetasExternalTriggerEnabled = VinhetasExternalTriggerEnabled;
        snapshot.VinhetasKeyboardShortcut = VinhetasKeyboardShortcut;
        snapshot.TrilhasExternalTriggerEnabled = TrilhasExternalTriggerEnabled;
        snapshot.TrilhasKeyboardShortcut = TrilhasKeyboardShortcut;
        snapshot.VinhetasUdpTriggerOnCommand = VinhetasUdpTriggerOnCommand?.Trim() ?? string.Empty;
        snapshot.VinhetasUdpTriggerOffCommand = VinhetasUdpTriggerOffCommand?.Trim() ?? string.Empty;
        snapshot.TrilhasUdpTriggerOnCommand = TrilhasUdpTriggerOnCommand?.Trim() ?? string.Empty;
        snapshot.TrilhasUdpTriggerOffCommand = TrilhasUdpTriggerOffCommand?.Trim() ?? string.Empty;
        snapshot.VinhetasCrossfadeMs = VinhetasCrossfadeMs;
        snapshot.TrilhasCrossfadeMs = TrilhasCrossfadeMs;
    }

    /// <summary>OpenFolderDialog (Microsoft.Win32, novo no .NET 8) — nenhum pacote extra necessário, já que o projeto é net8.0-windows/WPF.</summary>
    private static void BrowseFolder(string title, Action<string> onSelected)
    {
        var dialog = new OpenFolderDialog { Title = title };
        if (dialog.ShowDialog() == true)
        {
            onSelected(dialog.FolderName);
        }
    }
}
