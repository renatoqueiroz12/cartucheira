using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Input;
using CartucheiraVideo.Configuration;
using CartucheiraVideo.Presentation.Mvvm;
using Microsoft.Win32;

namespace CartucheiraVideo.Presentation.ViewModels;

/// <summary>
/// Sprint Arte-de-Fundo (2026-11-10): ViewModel da nova seção "Arte de Fundo" na janela de
/// Configurações — sexta entrada na sidebar, ao lado de Deck 1/Deck 2/Cartucheira/Saídas
/// Livewire/Rede NDI. Pedido literal do usuário: "criar um menuzinho ali pra eu colocar a arte de
/// espera que fique mandando pras telas [...] a gente pode criar um preset, arte 1, arte 2, arte
/// 3, e aí selecionar qual que é pra usar".
///
/// Duas responsabilidades, as duas globais (não por Deck — quem decide SE um Deck usa isto é o
/// próprio Deck, toggle <see cref="DeckSettingsViewModel.UseBackgroundArt"/> na aba Resolume dele):
/// 1. Gerenciar a lista de artes cadastradas (<see cref="Presets"/>) — adicionar (escolhe um
///    arquivo de imagem), remover, renomear.
/// 2. Escolher qual delas está ATIVA agora (<see cref="ActivePresetId"/>) — só uma por vez, mesmo
///    padrão de combo "Nenhuma"/"escolha explícita" já usado pela placa de rede Livewire
///    (<see cref="LivewireOutputsSettingsViewModel.NetworkInterfaces"/>).
///
/// Mesmo padrão de "editar em memória, só grava quando o Salvar global do SettingsWindow for
/// clicado" já usado pelo resto da tela — ver <see cref="ApplyTo"/>. Nenhuma cópia de arquivo é
/// feita pra dentro do programa nesta sprint — <see cref="BackgroundArtItemViewModel.FilePath"/>
/// aponta direto pro arquivo original escolhido; se ele for movido/apagado depois, a arte
/// simplesmente para de carregar (falha honesta, ver <c>BackgroundArtRenderer</c>).
/// </summary>
public sealed class BackgroundArtSettingsViewModel : ViewModelBase
{
    private BackgroundArtItemViewModel? _selectedPreset;
    private string? _activePresetId;

    public BackgroundArtSettingsViewModel(AppSettingsSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        foreach (var preset in snapshot.BackgroundArtPresets)
        {
            Presets.Add(new BackgroundArtItemViewModel(preset));
        }

        _activePresetId = snapshot.ActiveBackgroundArtId;
        RebuildActiveOptions();
        Presets.CollectionChanged += (_, _) => RebuildActiveOptions();

        AddCommand = new RelayCommand(_ => Add());
        RemoveCommand = new RelayCommand(_ => Remove(), _ => SelectedPreset is not null);
    }

    /// <summary>Artes cadastradas — cada linha é editável (só o nome) na própria lista da tela.</summary>
    public ObservableCollection<BackgroundArtItemViewModel> Presets { get; } = new();

    /// <summary>Linha selecionada na lista — controla se <see cref="RemoveCommand"/> pode rodar.</summary>
    public BackgroundArtItemViewModel? SelectedPreset
    {
        get => _selectedPreset;
        set
        {
            if (SetProperty(ref _selectedPreset, value))
            {
                ((RelayCommand)RemoveCommand).RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>Opções do combo "Arte ativa agora" — "Nenhuma" (Value nulo) no topo + uma por preset cadastrado, mesmo padrão de <see cref="LivewireOutputsSettingsViewModel.NetworkInterfaces"/>.</summary>
    public IReadOnlyList<ComboOption<string?>> ActiveOptions { get; private set; } = Array.Empty<ComboOption<string?>>();

    /// <summary><see cref="BackgroundArtPreset.Id"/> da arte ativa agora, ou nulo = nenhuma (Decks marcados com "usar arte de fundo" voltam ao comportamento de sempre — param de mandar quadro quando não tocam).</summary>
    public string? ActivePresetId
    {
        get => _activePresetId;
        set => SetProperty(ref _activePresetId, value);
    }

    /// <summary>Abre um diálogo de arquivo (imagem) e cadastra uma arte nova com o nome do arquivo (sem extensão) como nome inicial — editável na hora depois, igual Renomear.</summary>
    public ICommand AddCommand { get; }

    /// <summary>Remove <see cref="SelectedPreset"/> da lista — se ela for a arte ativa agora, <see cref="ActivePresetId"/> volta pra "Nenhuma" (nunca fica apontando pra um Id que não existe mais na lista em memória; <see cref="ApplyTo"/> já é honesto mesmo que isso não acontecesse, mas evitar deixa a tela consistente também).</summary>
    public ICommand RemoveCommand { get; }

    private void Add()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Escolher imagem da arte de fundo",
            Filter = "Imagens (*.png;*.jpg;*.jpeg;*.bmp)|*.png;*.jpg;*.jpeg;*.bmp|Todos os arquivos (*.*)|*.*",
            CheckFileExists = true
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        var suggestedName = System.IO.Path.GetFileNameWithoutExtension(dialog.FileName);
        var item = new BackgroundArtItemViewModel(new BackgroundArtPreset
        {
            Name = string.IsNullOrWhiteSpace(suggestedName) ? $"Arte {Presets.Count + 1}" : suggestedName,
            FilePath = dialog.FileName
        });

        Presets.Add(item);
        SelectedPreset = item;
    }

    private void Remove()
    {
        var target = SelectedPreset;
        if (target is null)
        {
            return;
        }

        if (ActivePresetId == target.Id)
        {
            ActivePresetId = null;
        }

        Presets.Remove(target);
        SelectedPreset = null;
    }

    private void RebuildActiveOptions()
    {
        var options = new List<ComboOption<string?>>
        {
            new() { Value = null, Label = "Nenhuma (fica sem sinal/preto quando não tocar, como hoje)" }
        };
        options.AddRange(Presets.Select(p => new ComboOption<string?> { Value = p.Id, Label = p.Name }));
        ActiveOptions = options;
        OnPropertyChanged(nameof(ActiveOptions));
    }

    /// <summary>Escreve os valores editados de volta no snapshot real — chamado só quando o Salvar global do SettingsWindow é clicado (mesmo padrão de <see cref="LivewireOutputsSettingsViewModel.ApplyTo"/>).</summary>
    public void ApplyTo(AppSettingsSnapshot snapshot)
    {
        snapshot.BackgroundArtPresets = Presets
            .Select(p => new BackgroundArtPreset
            {
                Id = p.Id,
                Name = string.IsNullOrWhiteSpace(p.Name) ? "Arte" : p.Name.Trim(),
                FilePath = p.FilePath,
            })
            .ToList();

        snapshot.ActiveBackgroundArtId = ActivePresetId;
    }
}
