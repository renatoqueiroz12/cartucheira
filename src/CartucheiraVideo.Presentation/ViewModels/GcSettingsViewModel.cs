using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Input;
using CartucheiraVideo.Configuration;
using CartucheiraVideo.Presentation.Mvvm;

namespace CartucheiraVideo.Presentation.ViewModels;

/// <summary>
/// ViewModel da nona seção da janela de Configurações — "GC" (Sprint GC, 2026-11-17). Mesmo padrão
/// simples de <see cref="TeleprompterSettingsViewModel"/> (só o <see cref="AppSettingsSnapshot"/>,
/// sem lista compartilhada com outra seção) pros campos de conexão vMix/Deck fonte, mais a lista
/// editável "Campos do GC" (<see cref="FieldLayout"/>) pro mockup configurável do monitoramento
/// remoto, mesmo padrão de lista de <see cref="BackgroundArtSettingsViewModel"/>.
/// </summary>
public sealed class GcSettingsViewModel : ViewModelBase
{
    private bool _enabled;
    private string _vMixHost;
    private string _vMixPort;
    private string _titleInput;
    private int _overlayChannel;
    private TeleprompterSourceDeck _sourceDeckSetting;
    private GcFieldLayoutItemViewModel? _selectedField;

    public GcSettingsViewModel(AppSettingsSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var gc = snapshot.Gc;
        _enabled = gc.Enabled;
        _vMixHost = gc.VMixHost ?? string.Empty;
        _vMixPort = gc.VMixPort;
        _titleInput = gc.TitleInput ?? string.Empty;
        _overlayChannel = gc.OverlayChannel;
        _sourceDeckSetting = gc.SourceDeckSetting;

        foreach (var layout in gc.FieldLayout)
        {
            FieldLayout.Add(new GcFieldLayoutItemViewModel(layout));
        }

        AddFieldCommand = new RelayCommand(_ => AddField());
        RemoveFieldCommand = new RelayCommand(_ => RemoveField(), _ => SelectedField is not null);
    }

    /// <summary>Liga/desliga o módulo GC inteiro — falso (padrão) não manda nenhum comando pro vMix.</summary>
    public bool Enabled
    {
        get => _enabled;
        set => SetProperty(ref _enabled, value);
    }

    public string VMixHost
    {
        get => _vMixHost;
        set => SetProperty(ref _vMixHost, value);
    }

    public string VMixPort
    {
        get => _vMixPort;
        set => SetProperty(ref _vMixPort, value);
    }

    /// <summary>Número ou nome (cadastrado no vMix) do Input de Título usado pro GC.</summary>
    public string TitleInput
    {
        get => _titleInput;
        set => SetProperty(ref _titleInput, value);
    }

    /// <summary>Canal de Overlay do vMix (1 a 8) onde o Título do GC entra/sai — clamp defensivo, mesmo padrão já usado por outros campos numéricos deste programa.</summary>
    public int OverlayChannel
    {
        get => _overlayChannel;
        set => SetProperty(ref _overlayChannel, Math.Clamp(value, 1, 8));
    }

    /// <summary>Opções do combo "Deck fonte" (pro GC de Laudas sem VT) — mesmo vocabulário de <see cref="TeleprompterSettingsViewModel.SourceDeckOptions"/>.</summary>
    public IReadOnlyList<ComboOption<TeleprompterSourceDeck>> SourceDeckOptions { get; } = new[]
    {
        new ComboOption<TeleprompterSourceDeck> { Value = TeleprompterSourceDeck.DeckA, Label = "Deck 1" },
        new ComboOption<TeleprompterSourceDeck> { Value = TeleprompterSourceDeck.DeckB, Label = "Deck 2" }
    };

    /// <summary>Qual Deck é "o apresentador" pro disparo automático de GC de Laudas SEM VT — ver decisão #2 em <see cref="GcViewModel"/>.</summary>
    public TeleprompterSourceDeck SourceDeckSetting
    {
        get => _sourceDeckSetting;
        set => SetProperty(ref _sourceDeckSetting, value);
    }

    /// <summary>Campos cadastrados do mockup configurável — cada linha é totalmente editável na própria lista da tela.</summary>
    public ObservableCollection<GcFieldLayoutItemViewModel> FieldLayout { get; } = new();

    public GcFieldLayoutItemViewModel? SelectedField
    {
        get => _selectedField;
        set
        {
            if (SetProperty(ref _selectedField, value))
            {
                ((RelayCommand)RemoveFieldCommand).RaiseCanExecuteChanged();
            }
        }
    }

    public ICommand AddFieldCommand { get; }

    public ICommand RemoveFieldCommand { get; }

    private void AddField()
    {
        var item = new GcFieldLayoutItemViewModel(new GcFieldLayout
        {
            Name = $"Campo {FieldLayout.Count + 1}",
            X = 80,
            Y = 850,
            Width = 700,
            Height = 90,
            FontSizePt = 28
        });

        FieldLayout.Add(item);
        SelectedField = item;
    }

    private void RemoveField()
    {
        if (SelectedField is null)
        {
            return;
        }

        FieldLayout.Remove(SelectedField);
        SelectedField = null;
    }

    /// <summary>Escreve os valores editados de volta no snapshot real — chamado só quando o Salvar global do SettingsWindow for clicado, mesmo padrão de <see cref="TeleprompterSettingsViewModel.ApplyTo"/>.</summary>
    public void ApplyTo(AppSettingsSnapshot snapshot)
    {
        var gc = snapshot.Gc;
        gc.Enabled = Enabled;
        gc.VMixHost = string.IsNullOrWhiteSpace(VMixHost) ? null : VMixHost.Trim();
        gc.VMixPort = string.IsNullOrWhiteSpace(VMixPort) ? "8088" : VMixPort.Trim();
        gc.TitleInput = string.IsNullOrWhiteSpace(TitleInput) ? null : TitleInput.Trim();
        gc.OverlayChannel = OverlayChannel;
        gc.SourceDeckSetting = SourceDeckSetting;
        gc.FieldLayout = FieldLayout
            .Where(f => !string.IsNullOrWhiteSpace(f.Name))
            .Select(f => new GcFieldLayout
            {
                Name = f.Name.Trim(),
                X = f.X,
                Y = f.Y,
                Width = f.Width,
                Height = f.Height,
                FontSizePt = f.FontSizePt
            })
            .ToList();
    }
}
