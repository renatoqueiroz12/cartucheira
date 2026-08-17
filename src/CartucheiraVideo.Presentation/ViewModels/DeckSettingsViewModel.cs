using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Input;
using CartucheiraVideo.Configuration;
using CartucheiraVideo.Core;
using CartucheiraVideo.Engine;
using CartucheiraVideo.Presentation.Mvvm;

namespace CartucheiraVideo.Presentation.ViewModels;

/// <summary>
/// ViewModel das 4 abas de Configuração de um Deck (arquitetura §10: "Geral, vMix, Resolume,
/// Automação") — o conteúdo é renderizado pelo <see cref="Views.DeckSettingsPanel"/> (UserControl),
/// hospedado dentro da janela única de Configurações (<see cref="Views.SettingsWindow"/>, uma
/// instância por Deck). Sprint Config (2026-08-08): primeira sprint desta ViewModel, quando ainda
/// era aberta sozinha num diálogo modal por Deck (DeckSettingsWindow, hoje removido). Sprint
/// Config Geral, parte 1 (2026-08-09): reaproveitada tal e qual, só o "container" visual mudou.
///
/// Esta ViewModel copia os valores atuais do <see cref="DeckViewModel.DomainDeck"/> na construção,
/// o operador edita à vontade, e só quando o rodapé global "Salvar" do SettingsWindow é clicado é
/// que <see cref="ApplyTo"/> escreve de volta no Deck de verdade — fechar a janela sem salvar
/// descarta as edições em memória sem tocar no Deck.
///
/// Os campos de vMix/Resolume/Automação SALVAM de verdade (não são fictícios), mas ainda não
/// disparam nenhuma chamada de rede — não existe cliente TCP/HTTP ainda (Frame Pipeline → NDI
/// Sender → vMix/Resolume Controller são os próximos passos do roteiro, status-implementacao.md).
/// </summary>
public sealed class DeckSettingsViewModel : ViewModelBase
{
    private enum SettingsTab
    {
        Geral,
        VMix,
        Resolume,
        Automacao
    }

    private SettingsTab _selectedTab = SettingsTab.Geral;

    private string _ndiChannelName;
    private string? _audioDeviceId;
    private string? _previewAudioDeviceId;
    private bool _vMixEnabled;
    private string _vMixHost;
    private string _vMixPort;
    private string _vMixInput;
    private bool _resolumeEnabled;
    private string _resolumeHost;
    private string _resolumePort;
    private string _resolumeLayer;
    private bool _externalTriggerEnabled;
    private string _udpTriggerPlayCommand;
    private string _udpTriggerPauseCommand;
    private bool _outboundUdpEnabled;
    private string _outboundUdpHost;
    private int _outboundUdpPort;
    private string _outboundUdpOnPlayCommand;
    private string _outboundUdpOnEndCommand;

    /// <summary>
    /// Sprint Comando Externo Decks (2026-08-12): recebe também o <see cref="DeckSettingsSnapshot"/>
    /// persistido deste Deck, além do <see cref="DeckViewModel"/> — os três campos de comando
    /// externo (<see cref="ExternalTriggerEnabled"/>/<see cref="UdpTriggerPlayCommand"/>/
    /// <see cref="UdpTriggerPauseCommand"/>) não são estado do domínio (<c>Core.Deck</c> não sabe
    /// nada de UDP, mesmo desacoplamento já usado pela Cartucheira: <c>AudioBankViewModel</c>
    /// também não guarda essas configurações) — só existem no snapshot persistido, consultado
    /// direto por <c>MainViewModel</c> a cada pacote recebido, então a única fonte pra semear estes
    /// três campos na tela é o snapshot, não o <see cref="DeckViewModel"/>.
    /// </summary>
    public DeckSettingsViewModel(DeckViewModel deck, DeckSettingsSnapshot snapshot, UdpPortSettingsViewModel sharedUdpPort)
    {
        ArgumentNullException.ThrowIfNull(deck);
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(sharedUdpPort);

        SharedUdpPort = sharedUdpPort;
        DeckName = deck.Name;
        _ndiChannelName = deck.DomainDeck.NdiChannelName ?? string.Empty;
        _audioDeviceId = deck.DomainDeck.AudioDeviceId;
        _previewAudioDeviceId = deck.DomainDeck.PreviewAudioDeviceId;
        _externalTriggerEnabled = snapshot.ExternalTriggerEnabled;
        _udpTriggerPlayCommand = snapshot.UdpTriggerPlayCommand;
        _udpTriggerPauseCommand = snapshot.UdpTriggerPauseCommand;
        _outboundUdpEnabled = snapshot.OutboundUdpEnabled;
        _outboundUdpHost = snapshot.OutboundUdpHost;
        _outboundUdpPort = snapshot.OutboundUdpPort;
        _outboundUdpOnPlayCommand = snapshot.OutboundUdpOnPlayCommand;
        _outboundUdpOnEndCommand = snapshot.OutboundUdpOnEndCommand;

        var devices = new List<ComboOption<string?>>
        {
            new() { Value = null, Label = "Padrão do sistema" }
        };
        devices.AddRange(AudioEngine.EnumerateOutputDevices()
            .Select(d => new ComboOption<string?> { Value = d.DeviceId, Label = d.Description }));
        AudioDevices = devices;

        var vmix = deck.DomainDeck.OutputRoutes.First(r => r.OutputType == OutputRouteType.VMix);
        _vMixEnabled = vmix.Enabled;
        _vMixHost = GetConfig(vmix, "Host");
        _vMixPort = GetConfig(vmix, "Port");
        _vMixInput = GetConfig(vmix, "Input");

        var resolume = deck.DomainDeck.OutputRoutes.First(r => r.OutputType == OutputRouteType.Resolume);
        _resolumeEnabled = resolume.Enabled;
        _resolumeHost = GetConfig(resolume, "Host");
        _resolumePort = GetConfig(resolume, "Port");
        _resolumeLayer = GetConfig(resolume, "Layer");

        OnPlayActions = new ObservableCollection<AutomationActionViewModel>(
            deck.DomainDeck.AutomationActions
                .Where(a => a.Trigger == AutomationTrigger.OnPlay)
                .Select(a => new AutomationActionViewModel(a)));

        OnEndActions = new ObservableCollection<AutomationActionViewModel>(
            deck.DomainDeck.AutomationActions
                .Where(a => a.Trigger == AutomationTrigger.OnEnd)
                .Select(a => new AutomationActionViewModel(a)));

        SelectGeneralTabCommand = new RelayCommand(_ => SelectedTab = SettingsTab.Geral);
        SelectVMixTabCommand = new RelayCommand(_ => SelectedTab = SettingsTab.VMix);
        SelectResolumeTabCommand = new RelayCommand(_ => SelectedTab = SettingsTab.Resolume);
        SelectAutomationTabCommand = new RelayCommand(_ => SelectedTab = SettingsTab.Automacao);

        AddOnPlayActionCommand = new RelayCommand(_ => OnPlayActions.Add(CreateAction(AutomationTrigger.OnPlay)));
        RemoveOnPlayActionCommand = new RelayCommand(p => RemoveAction(OnPlayActions, p));
        AddOnEndActionCommand = new RelayCommand(_ => OnEndActions.Add(CreateAction(AutomationTrigger.OnEnd)));
        RemoveOnEndActionCommand = new RelayCommand(p => RemoveAction(OnEndActions, p));
    }

    public string DeckName { get; }

    /// <summary>
    /// Porta UDP única (Cartucheira + Deck) — MESMA instância injetada em
    /// <see cref="CartridgeSettingsViewModel"/> e no outro Deck por <see cref="SettingsViewModel"/>
    /// (correção de bug 2026-08-13: antes tentava alcançar isso via
    /// <c>RelativeSource AncestorType=Window</c>, que não resolvia em tempo de execução — campo
    /// ficava vazio na tela e a edição não ia pra lugar nenhum). A XAML de
    /// <see cref="Views.DeckSettingsPanel"/> agora faz um Binding local de dois níveis
    /// (<c>SharedUdpPort.Port</c>).
    /// </summary>
    public UdpPortSettingsViewModel SharedUdpPort { get; }

    private SettingsTab SelectedTab
    {
        get => _selectedTab;
        set
        {
            if (SetProperty(ref _selectedTab, value))
            {
                OnPropertyChanged(nameof(IsGeneralTabSelected));
                OnPropertyChanged(nameof(IsVMixTabSelected));
                OnPropertyChanged(nameof(IsResolumeTabSelected));
                OnPropertyChanged(nameof(IsAutomationTabSelected));
            }
        }
    }

    public bool IsGeneralTabSelected => SelectedTab == SettingsTab.Geral;

    public bool IsVMixTabSelected => SelectedTab == SettingsTab.VMix;

    public bool IsResolumeTabSelected => SelectedTab == SettingsTab.Resolume;

    public bool IsAutomationTabSelected => SelectedTab == SettingsTab.Automacao;

    public ICommand SelectGeneralTabCommand { get; }

    public ICommand SelectVMixTabCommand { get; }

    public ICommand SelectResolumeTabCommand { get; }

    public ICommand SelectAutomationTabCommand { get; }

    /// <summary>Nome do canal NDI que este Deck vai gerar (arquitetura §13.1) — consumido de verdade quando o NDI Sender existir (roteiro, passo 3).</summary>
    public string NdiChannelName
    {
        get => _ndiChannelName;
        set => SetProperty(ref _ndiChannelName, value);
    }

    /// <summary>Dispositivos de saída de áudio do Windows (mmdevice) + a opção "Padrão do sistema" (Value nulo) no topo — mesma lista/fonte já usada em Configurações → Cartucheira.</summary>
    public IReadOnlyList<ComboOption<string?>> AudioDevices { get; }

    /// <summary>
    /// Dispositivo de saída de áudio do Windows usado pelo áudio local/preview deste Deck (o vídeo
    /// tocando na tela) — Sprint Áudio-Dispositivo-Deck (2026-08-11). Nulo = padrão do sistema. Não
    /// afeta o áudio já enviado pelo NDI (caminho separado, ver <see cref="Engine.PlaybackEngine.SetAudioOutputDevice"/>).
    /// </summary>
    public string? AudioDeviceId
    {
        get => _audioDeviceId;
        set => SetProperty(ref _audioDeviceId, value);
    }

    /// <summary>
    /// Dispositivo de saída de áudio do Windows usado SÓ pelo botão Preview deste Deck (Sprint
    /// Preview-Isolado, 2026-08-15) — mesma lista de <see cref="AudioDevices"/>, campo separado de
    /// propósito (conferir um clipe sem que o áudio saia pela placa principal/transmissão). Nulo =
    /// padrão do sistema.
    /// </summary>
    public string? PreviewAudioDeviceId
    {
        get => _previewAudioDeviceId;
        set => SetProperty(ref _previewAudioDeviceId, value);
    }

    public bool VMixEnabled
    {
        get => _vMixEnabled;
        set => SetProperty(ref _vMixEnabled, value);
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

    /// <summary>Input do vMix que recebe a fonte NDI deste Deck (arquitetura §13.3: "cortar pro PGM no canal NDI do Deck").</summary>
    public string VMixInput
    {
        get => _vMixInput;
        set => SetProperty(ref _vMixInput, value);
    }

    public bool ResolumeEnabled
    {
        get => _resolumeEnabled;
        set => SetProperty(ref _resolumeEnabled, value);
    }

    public string ResolumeHost
    {
        get => _resolumeHost;
        set => SetProperty(ref _resolumeHost, value);
    }

    public string ResolumePort
    {
        get => _resolumePort;
        set => SetProperty(ref _resolumePort, value);
    }

    /// <summary>Layer do Resolume que recebe a fonte NDI deste Deck (arquitetura §13.3).</summary>
    public string ResolumeLayer
    {
        get => _resolumeLayer;
        set => SetProperty(ref _resolumeLayer, value);
    }

    /// <summary>Verdadeiro = este Deck recebe comando externo de verdade por UDP (mesa de áudio) — Sprint Comando Externo Decks (2026-08-12), mesmo padrão do toggle único já usado pela Cartucheira.</summary>
    public bool ExternalTriggerEnabled
    {
        get => _externalTriggerEnabled;
        set => SetProperty(ref _externalTriggerEnabled, value);
    }

    /// <summary>Texto que a mesa manda por UDP pra dar PLAY neste Deck.</summary>
    public string UdpTriggerPlayCommand
    {
        get => _udpTriggerPlayCommand;
        set => SetProperty(ref _udpTriggerPlayCommand, value);
    }

    /// <summary>Texto que a mesa manda por UDP pra dar PAUSE neste Deck.</summary>
    public string UdpTriggerPauseCommand
    {
        get => _udpTriggerPauseCommand;
        set => SetProperty(ref _udpTriggerPauseCommand, value);
    }

    /// <summary>
    /// Verdadeiro = este Deck AVISA por UDP (direção contrária de <see cref="ExternalTriggerEnabled"/>)
    /// um destino externo quando dá Play e quando termina — Sprint Comando Externo Deck→Mesa
    /// (2026-08-13). Um toggle só liga os dois disparos juntos (decisão explícita do usuário).
    /// </summary>
    public bool OutboundUdpEnabled
    {
        get => _outboundUdpEnabled;
        set => SetProperty(ref _outboundUdpEnabled, value);
    }

    /// <summary>IP (ou host) de destino do aviso — independente por Deck, diferente da porta única de entrada.</summary>
    public string OutboundUdpHost
    {
        get => _outboundUdpHost;
        set => SetProperty(ref _outboundUdpHost, value);
    }

    /// <summary>Porta do destino acima — clamp defensivo (1–65535), mesmo padrão já usado pelas outras portas UDP deste programa.</summary>
    public int OutboundUdpPort
    {
        get => _outboundUdpPort;
        set => SetProperty(ref _outboundUdpPort, Math.Clamp(value, 1, 65535));
    }

    /// <summary>Texto mandado pro destino acima quando este Deck dá Play.</summary>
    public string OutboundUdpOnPlayCommand
    {
        get => _outboundUdpOnPlayCommand;
        set => SetProperty(ref _outboundUdpOnPlayCommand, value);
    }

    /// <summary>Texto mandado pro destino acima quando este Deck termina — fim natural OU Stop manual, os dois convergem aqui.</summary>
    public string OutboundUdpOnEndCommand
    {
        get => _outboundUdpOnEndCommand;
        set => SetProperty(ref _outboundUdpOnEndCommand, value);
    }

    /// <summary>Ações "On Play" (arquitetura §10) — disparo real depende do vMix/Resolume Controller, ainda não implementado.</summary>
    public ObservableCollection<AutomationActionViewModel> OnPlayActions { get; }

    /// <summary>Ações "On End" — mesmo mecanismo do Stop manual (arquitetura §10, "Comportamento do Stop").</summary>
    public ObservableCollection<AutomationActionViewModel> OnEndActions { get; }

    /// <summary>Opções de Destino da lista "Ao terminar" — vMix/Resolume só (ver <see cref="OnPlayDestinationOptions"/> pro porquê a Cartucheira não aparece aqui).</summary>
    public IReadOnlyList<ComboOption<OutputRouteType>> DestinationOptions { get; } = new[]
    {
        new ComboOption<OutputRouteType> { Value = OutputRouteType.VMix, Label = "vMix" },
        new ComboOption<OutputRouteType> { Value = OutputRouteType.Resolume, Label = "Resolume" }
    };

    /// <summary>
    /// Sprint Deck-pausa-Cartucheira v2 (2026-08-11): opções de Destino só da lista "Ao dar Play" —
    /// ganha "Cartucheira" além de vMix/Resolume. Diferente dos outros dois (ainda só dado salvo,
    /// sem disparo real — ver <c>AutomationAction</c>), uma linha "Ao dar Play → Cartucheira" JÁ
    /// FUNCIONA DE VERDADE: pausa as duas Cartucheiras (Vinhetas/Trilhas) assim que este Deck começa
    /// a tocar (ver <c>MainViewModel.PauseCartucheiraFor</c>). Deliberadamente NÃO oferecida também
    /// na lista "Ao terminar" (<see cref="DestinationOptions"/>): o retorno da Cartucheira é
    /// automático (fim natural do vídeo OU Stop manual, confirmado explicitamente pelo usuário)
    /// sempre que este Deck a pausou — não é uma segunda ação independente pra configurar. Oferecer
    /// aqui também sugeriria um controle que não existe, com risco real de a Cartucheira ficar presa
    /// pausada se o operador removesse só a linha "Ao terminar" sem perceber que o retorno dependia
    /// dela.
    /// </summary>
    public IReadOnlyList<ComboOption<OutputRouteType>> OnPlayDestinationOptions { get; } = new[]
    {
        new ComboOption<OutputRouteType> { Value = OutputRouteType.VMix, Label = "vMix" },
        new ComboOption<OutputRouteType> { Value = OutputRouteType.Resolume, Label = "Resolume" },
        new ComboOption<OutputRouteType> { Value = OutputRouteType.Cartucheira, Label = "Cartucheira" }
    };

    public IReadOnlyList<ComboOption<TransitionType>> TransitionOptions { get; } = new[]
    {
        new ComboOption<TransitionType> { Value = TransitionType.Cut, Label = "Corte seco" },
        new ComboOption<TransitionType> { Value = TransitionType.Fade, Label = "Fade" }
    };

    public ICommand AddOnPlayActionCommand { get; }

    public ICommand RemoveOnPlayActionCommand { get; }

    public ICommand AddOnEndActionCommand { get; }

    public ICommand RemoveOnEndActionCommand { get; }

    /// <summary>
    /// Escreve os valores editados de volta no Deck de verdade — chamado só depois que a janela
    /// fecha com DialogResult=true (Salvar). SendToVMix/SendToResolume passam pelo setter público
    /// do DeckViewModel (não direto no OutputRoute) pra disparar as notificações de IsOnAir/
    /// IsLocalOnly que a "pill" do cabeçalho do Deck já escuta.
    /// </summary>
    public void ApplyTo(DeckViewModel deck)
    {
        deck.DomainDeck.NdiChannelName = string.IsNullOrWhiteSpace(NdiChannelName) ? null : NdiChannelName.Trim();

        deck.DomainDeck.AudioDeviceId = AudioDeviceId;
        deck.SetAudioOutputDevice(AudioDeviceId);

        // PreviewAudioDeviceId NÃO é empurrado pro Engine aqui de propósito — ver comentário em
        // SettingsMapper.ApplyToDeck (mesma decisão): só é aplicado no instante de um Preview real.
        deck.DomainDeck.PreviewAudioDeviceId = PreviewAudioDeviceId;

        deck.SendToVMix = VMixEnabled;
        var vmix = deck.DomainDeck.OutputRoutes.First(r => r.OutputType == OutputRouteType.VMix);
        vmix.Configuration["Host"] = VMixHost.Trim();
        vmix.Configuration["Port"] = VMixPort.Trim();
        vmix.Configuration["Input"] = VMixInput.Trim();

        deck.SendToResolume = ResolumeEnabled;
        var resolume = deck.DomainDeck.OutputRoutes.First(r => r.OutputType == OutputRouteType.Resolume);
        resolume.Configuration["Host"] = ResolumeHost.Trim();
        resolume.Configuration["Port"] = ResolumePort.Trim();
        resolume.Configuration["Layer"] = ResolumeLayer.Trim();

        deck.DomainDeck.AutomationActions.Clear();
        foreach (var actionViewModel in OnPlayActions.Concat(OnEndActions))
        {
            actionViewModel.ApplyToModel();
            deck.DomainDeck.AutomationActions.Add(actionViewModel.Model);
        }
    }

    private static AutomationActionViewModel CreateAction(AutomationTrigger trigger) =>
        new(new AutomationAction { Trigger = trigger, Destination = OutputRouteType.VMix, Transition = TransitionType.Cut });

    private static void RemoveAction(ObservableCollection<AutomationActionViewModel> list, object? parameter)
    {
        if (parameter is AutomationActionViewModel item)
        {
            list.Remove(item);
        }
    }

    /// <summary>IDictionary&lt;string,string&gt; não tem GetValueOrDefault (essa extensão só existe pra IReadOnlyDictionary) — helper manual em vez de forçar um cast.</summary>
    private static string GetConfig(OutputRoute route, string key) =>
        route.Configuration.TryGetValue(key, out var value) ? value : string.Empty;
}
