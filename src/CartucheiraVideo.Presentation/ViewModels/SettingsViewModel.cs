using System.Windows.Input;
using CartucheiraVideo.Configuration;
using CartucheiraVideo.Presentation.Mvvm;

namespace CartucheiraVideo.Presentation.ViewModels;

/// <summary>
/// ViewModel do <see cref="Views.SettingsWindow"/> — Sprint Config Geral, parte 1 (2026-08-09).
/// Janela não-modal (decisão do usuário: "o menu config abre em outra aba e nao fecha o software"),
/// então diferente do antigo diálogo por Deck (DialogResult=true/false), aqui Salvar aplica E
/// persiste em disco imediatamente, sem fechar a janela — o operador pode continuar ajustando e
/// salvar de novo quantas vezes quiser. Fechar (código-behind da janela) só descarta o que não foi
/// salvo ainda, sem desfazer o que já tiver sido salvo antes.
///
/// Sprint Cartucheira (2026-08-09, parte 2 do item 2 do roteiro): terceira seção na sidebar —
/// Cartucheira (<see cref="CartridgeSettingsViewModel"/>) — ao lado de Deck 1/Deck 2. A navegação
/// de "aba única selecionada" passou de um bool só (IsDeck1Selected/!IsDeck1Selected=IsDeck2Selected)
/// pra um enum interno de 3 valores, já que agora há 3 seções mutuamente exclusivas.
///
/// Sprint Livewire-2 (2026-09-13): quarta seção — "Saídas Livewire"
/// (<see cref="LivewireOutputsSettingsViewModel"/>), enum de <see cref="Section"/> vai pra 4
/// valores. <see cref="LivewireOutputsSettingsViewModel.Outputs"/> é passada por referência pro
/// construtor de <see cref="CartridgeSettingsViewModel"/> — as duas seções compartilham a MESMA
/// lista em memória, então criar/remover uma Saída Livewire aqui já aparece no seletor de
/// dispositivo de Vinhetas/Trilhas sem fechar/reabrir a janela.
///
/// Sprint NDI-Placa-de-Rede (2026-09-13): quinta seção — "Rede NDI"
/// (<see cref="NdiNetworkSettingsViewModel"/>), enum de <see cref="Section"/> vai pra 5 valores. Sem
/// nenhuma lista compartilhada com outra seção (diferente da Livewire acima) — o valor só é lido de
/// volta em <c>MainViewModel.ReapplyNdiNetworkAdapter</c> quando a janela de Configurações fecha.
/// </summary>
public sealed class SettingsViewModel : ViewModelBase
{
    private enum Section
    {
        Deck1,
        Deck2,
        Cartridges,
        LivewireOutputs,
        NdiNetwork
    }

    private readonly DeckViewModel _deckA;
    private readonly DeckViewModel _deckB;
    private readonly SettingsStore _settingsStore;
    private readonly AppSettingsSnapshot _currentSnapshot;
    private readonly Action<string> _onStatus;

    private Section _selectedSection = Section.Deck1;

    public SettingsViewModel(
        DeckViewModel deckA,
        DeckViewModel deckB,
        SettingsStore settingsStore,
        AppSettingsSnapshot currentSnapshot,
        Action<string> onStatus)
    {
        _deckA = deckA ?? throw new ArgumentNullException(nameof(deckA));
        _deckB = deckB ?? throw new ArgumentNullException(nameof(deckB));
        _settingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));
        _currentSnapshot = currentSnapshot ?? throw new ArgumentNullException(nameof(currentSnapshot));
        _onStatus = onStatus ?? throw new ArgumentNullException(nameof(onStatus));

        // Correção de bug real em teste (2026-08-13): a porta UDP única tentava alcançar esta
        // ViewModel via RelativeSource AncestorType=Window a partir das telas de Cartucheira/Deck —
        // não resolvia em tempo de execução (campo ficava vazio, edição não ia pra lugar nenhum,
        // TODO disparo externo parava de funcionar). Corrigido injetando a MESMA instância de
        // UdpPortSettingsViewModel nos três painéis (Cartucheira, Deck 1, Deck 2) — cada um expõe
        // um Binding local de dois níveis, sem sair do próprio DataContext.
        SharedUdpPort = new UdpPortSettingsViewModel(currentSnapshot.UdpTriggerPort);

        // Sprint Livewire-2 (2026-09-13)/Livewire-4 (2026-09-14): LivewireOutputsSettings.Outputs é
        // passada por referência tanto pra CartridgeSettingsViewModel quanto (desde a Livewire-4) pros
        // dois DeckSettingsViewModel abaixo — criar/remover uma Saída na aba nova já atualiza a lista
        // de dispositivos de Vinhetas/Trilhas/Deck 1/Deck 2 na hora, sem fechar/reabrir Configurações
        // (ver comentário completo em CartridgeSettingsViewModel.RebuildAudioDevices/
        // DeckSettingsViewModel.RebuildAudioDevices). Precisa ser criada ANTES dos dois
        // DeckSettingsViewModel agora, diferente da ordem original desta sprint anterior.
        LivewireOutputsSettings = new LivewireOutputsSettingsViewModel(currentSnapshot);

        Deck1Settings = new DeckSettingsViewModel(deckA, currentSnapshot.DeckA, SharedUdpPort, LivewireOutputsSettings.Outputs);
        Deck2Settings = new DeckSettingsViewModel(deckB, currentSnapshot.DeckB, SharedUdpPort, LivewireOutputsSettings.Outputs);

        CartridgeSettings = new CartridgeSettingsViewModel(currentSnapshot.Cartridges, SharedUdpPort, LivewireOutputsSettings.Outputs);

        // Sprint NDI-Placa-de-Rede (2026-09-13): sem lista compartilhada com nenhuma outra seção —
        // só o snapshot, mesmo padrão simples de CartridgeSettings/Deck1Settings/Deck2Settings.
        NdiNetworkSettings = new NdiNetworkSettingsViewModel(currentSnapshot);

        SelectDeck1Command = new RelayCommand(_ => SelectedSection = Section.Deck1);
        SelectDeck2Command = new RelayCommand(_ => SelectedSection = Section.Deck2);
        SelectCartridgesCommand = new RelayCommand(_ => SelectedSection = Section.Cartridges);
        SelectLivewireOutputsCommand = new RelayCommand(_ => SelectedSection = Section.LivewireOutputs);
        SelectNdiNetworkCommand = new RelayCommand(_ => SelectedSection = Section.NdiNetwork);

        SaveCommand = new RelayCommand(_ => Save());
    }

    /// <summary>Porta UDP única (Cartucheira + Deck) — ver comentário no construtor e em <see cref="UdpPortSettingsViewModel"/>.</summary>
    public UdpPortSettingsViewModel SharedUdpPort { get; }

    /// <summary>Configurações do Deck 1 (aba Geral/vMix/Resolume/Automação, arquitetura §10) — instância reaproveitada tal e qual da sprint anterior.</summary>
    public DeckSettingsViewModel Deck1Settings { get; }

    /// <summary>Configurações do Deck 2 — ver <see cref="Deck1Settings"/>.</summary>
    public DeckSettingsViewModel Deck2Settings { get; }

    /// <summary>Configurações da Cartucheira (Vinhetas/Trilhas) — dispositivo de áudio, pasta de origem, comando externo (Sprint Cartucheira).</summary>
    public CartridgeSettingsViewModel CartridgeSettings { get; }

    /// <summary>Sprint Livewire-2 (2026-09-13): gerenciador das "Saídas Livewire" (nome + canal) e da placa de rede usada pra transmiti-las.</summary>
    public LivewireOutputsSettingsViewModel LivewireOutputsSettings { get; }

    /// <summary>Sprint NDI-Placa-de-Rede (2026-09-13): placa de rede à qual o NDI desta máquina fica restrito.</summary>
    public NdiNetworkSettingsViewModel NdiNetworkSettings { get; }

    private Section SelectedSection
    {
        get => _selectedSection;
        set
        {
            if (SetProperty(ref _selectedSection, value))
            {
                OnPropertyChanged(nameof(IsDeck1Selected));
                OnPropertyChanged(nameof(IsDeck2Selected));
                OnPropertyChanged(nameof(IsCartridgesSelected));
                OnPropertyChanged(nameof(IsLivewireOutputsSelected));
                OnPropertyChanged(nameof(IsNdiNetworkSelected));
            }
        }
    }

    public bool IsDeck1Selected => SelectedSection == Section.Deck1;

    public bool IsDeck2Selected => SelectedSection == Section.Deck2;

    public bool IsCartridgesSelected => SelectedSection == Section.Cartridges;

    public bool IsLivewireOutputsSelected => SelectedSection == Section.LivewireOutputs;

    public bool IsNdiNetworkSelected => SelectedSection == Section.NdiNetwork;

    public ICommand SelectDeck1Command { get; }

    public ICommand SelectDeck2Command { get; }

    public ICommand SelectCartridgesCommand { get; }

    public ICommand SelectLivewireOutputsCommand { get; }

    public ICommand SelectNdiNetworkCommand { get; }

    /// <summary>Aplica os dois painéis de Deck e a Cartucheira nos objetos reais, grava o snapshot inteiro no disco e avisa o operador — sem fechar a janela.</summary>
    public ICommand SaveCommand { get; }

    private void Save()
    {
        Deck1Settings.ApplyTo(_deckA);
        Deck2Settings.ApplyTo(_deckB);

        _currentSnapshot.DeckA = SettingsMapper.ToSnapshot(Deck1Settings);
        _currentSnapshot.DeckB = SettingsMapper.ToSnapshot(Deck2Settings);
        CartridgeSettings.ApplyTo(_currentSnapshot.Cartridges);
        LivewireOutputsSettings.ApplyTo(_currentSnapshot);
        NdiNetworkSettings.ApplyTo(_currentSnapshot);
        _currentSnapshot.UdpTriggerPort = SharedUdpPort.Port;

        _onStatus(_settingsStore.Save(_currentSnapshot)
            ? "Configurações salvas."
            : "Não foi possível salvar as configurações em disco (verifique permissão/espaço) — os valores continuam aplicados nesta sessão.");
    }
}
