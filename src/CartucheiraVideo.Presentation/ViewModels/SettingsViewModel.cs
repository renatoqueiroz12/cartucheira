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
/// </summary>
public sealed class SettingsViewModel : ViewModelBase
{
    private enum Section
    {
        Deck1,
        Deck2,
        Cartridges
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
        Deck1Settings = new DeckSettingsViewModel(deckA, currentSnapshot.DeckA, SharedUdpPort);
        Deck2Settings = new DeckSettingsViewModel(deckB, currentSnapshot.DeckB, SharedUdpPort);
        CartridgeSettings = new CartridgeSettingsViewModel(currentSnapshot.Cartridges, SharedUdpPort);

        SelectDeck1Command = new RelayCommand(_ => SelectedSection = Section.Deck1);
        SelectDeck2Command = new RelayCommand(_ => SelectedSection = Section.Deck2);
        SelectCartridgesCommand = new RelayCommand(_ => SelectedSection = Section.Cartridges);

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
            }
        }
    }

    public bool IsDeck1Selected => SelectedSection == Section.Deck1;

    public bool IsDeck2Selected => SelectedSection == Section.Deck2;

    public bool IsCartridgesSelected => SelectedSection == Section.Cartridges;

    public ICommand SelectDeck1Command { get; }

    public ICommand SelectDeck2Command { get; }

    public ICommand SelectCartridgesCommand { get; }

    /// <summary>Aplica os dois painéis de Deck e a Cartucheira nos objetos reais, grava o snapshot inteiro no disco e avisa o operador — sem fechar a janela.</summary>
    public ICommand SaveCommand { get; }

    private void Save()
    {
        Deck1Settings.ApplyTo(_deckA);
        Deck2Settings.ApplyTo(_deckB);

        _currentSnapshot.DeckA = SettingsMapper.ToSnapshot(Deck1Settings);
        _currentSnapshot.DeckB = SettingsMapper.ToSnapshot(Deck2Settings);
        CartridgeSettings.ApplyTo(_currentSnapshot.Cartridges);
        _currentSnapshot.UdpTriggerPort = SharedUdpPort.Port;

        _onStatus(_settingsStore.Save(_currentSnapshot)
            ? "Configurações salvas."
            : "Não foi possível salvar as configurações em disco (verifique permissão/espaço) — os valores continuam aplicados nesta sessão.");
    }
}
