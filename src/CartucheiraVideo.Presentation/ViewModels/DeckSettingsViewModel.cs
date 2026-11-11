using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
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
///
/// Sprint Livewire-4 (2026-09-14): <see cref="AudioDevices"/> ("Dispositivo de áudio") ganha as
/// Saídas Livewire nativas configuradas em Configurações → Saídas Livewire, além dos dispositivos
/// Windows de sempre — mesmo padrão (mesmo <see cref="RebuildAudioDevices"/>/
/// <see cref="OnLivewireOutputsChanged"/>) já usado por <see cref="CartridgeSettingsViewModel"/> pra
/// Vinhetas/Trilhas.
///
/// Sprint Áudio-Unificado (2026-09-15): o seletor separado "Dispositivo de áudio (Preview)"
/// (<c>PreviewAudioDevices</c>/<c>PreviewAudioDeviceId</c>) foi REMOVIDO — Play e Preview agora usam
/// a MESMA placa (<see cref="AudioDeviceId"/>), decisão explícita do usuário depois de uma sequência
/// de bugs sérios causados pela dupla seleção (ver comentário completo em
/// <see cref="Core.Deck.AudioDeviceId"/>). Dois campos novos (<see cref="OutboundUdpOnPreviewCommand"/>/
/// <see cref="OutboundUdpOnPreviewEndCommand"/>) substituem a distinção que a placa separada dava —
/// agora é a mesa/console externa quem decide a rota física, avisada por UDP.
/// </summary>
public sealed class DeckSettingsViewModel : ViewModelBase
{
    private enum SettingsTab
    {
        Geral,
        VMix,
        Resolume,
        Telas,
        Automacao
    }

    private SettingsTab _selectedTab = SettingsTab.Geral;

    private string _ndiChannelName;
    private string? _audioDeviceId;
    private bool _vMixEnabled;
    private string _vMixHost;
    private string _vMixPort;
    private string _vMixInput;
    private bool _resolumeEnabled;
    private string _resolumeHost;
    private string _resolumePort;
    private string _resolumeLayer;
    private bool _useBackgroundArt;
    private bool _sendToScreensEnabled;
    private bool _externalTriggerEnabled;
    private string _udpTriggerPlayCommand;
    private string _udpTriggerPauseCommand;
    private bool _outboundUdpEnabled;
    private string _outboundUdpHost;
    private int _outboundUdpPort;
    private string _outboundUdpOnPlayCommand;
    private string _outboundUdpOnEndCommand;
    private string _outboundUdpOnPreviewCommand;
    private string _outboundUdpOnPreviewEndCommand;
    private readonly ObservableCollection<LivewireOutputItemViewModel> _livewireOutputs;
    private readonly ObservableCollection<ComboOption<string?>> _audioDevices = new();

    /// <summary>
    /// Sprint Comando Externo Decks (2026-08-12): recebe também o <see cref="DeckSettingsSnapshot"/>
    /// persistido deste Deck, além do <see cref="DeckViewModel"/> — os três campos de comando
    /// externo (<see cref="ExternalTriggerEnabled"/>/<see cref="UdpTriggerPlayCommand"/>/
    /// <see cref="UdpTriggerPauseCommand"/>) não são estado do domínio (<c>Core.Deck</c> não sabe
    /// nada de UDP, mesmo desacoplamento já usado pela Cartucheira: <c>AudioBankViewModel</c>
    /// também não guarda essas configurações) — só existem no snapshot persistido, consultado
    /// direto por <c>MainViewModel</c> a cada pacote recebido, então a única fonte pra semear estes
    /// três campos na tela é o snapshot, não o <see cref="DeckViewModel"/>.
    ///
    /// Sprint Livewire-4 (2026-09-14): <paramref name="livewireOutputs"/> é a MESMA instância (por
    /// referência) que <see cref="LivewireOutputsSettingsViewModel.Outputs"/> usa — injetada pelo
    /// <see cref="SettingsViewModel"/> pra este painel poder incluir as Saídas Livewire configuradas
    /// no seletor de "Dispositivo de áudio (Play)" (<see cref="AudioDevices"/>). Ver
    /// <see cref="RebuildAudioDevices"/> pra limitação honesta sobre renomear (mesma já documentada
    /// em <see cref="CartridgeSettingsViewModel"/>).
    /// </summary>
    public DeckSettingsViewModel(DeckViewModel deck, DeckSettingsSnapshot snapshot, UdpPortSettingsViewModel sharedUdpPort, ObservableCollection<LivewireOutputItemViewModel> livewireOutputs)
    {
        ArgumentNullException.ThrowIfNull(deck);
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(sharedUdpPort);
        ArgumentNullException.ThrowIfNull(livewireOutputs);

        SharedUdpPort = sharedUdpPort;
        _livewireOutputs = livewireOutputs;
        DeckName = deck.Name;
        _ndiChannelName = deck.DomainDeck.NdiChannelName ?? string.Empty;
        _audioDeviceId = deck.DomainDeck.AudioDeviceId;
        _externalTriggerEnabled = snapshot.ExternalTriggerEnabled;
        _udpTriggerPlayCommand = snapshot.UdpTriggerPlayCommand;
        _udpTriggerPauseCommand = snapshot.UdpTriggerPauseCommand;
        _outboundUdpEnabled = snapshot.OutboundUdpEnabled;
        _outboundUdpHost = snapshot.OutboundUdpHost;
        _outboundUdpPort = snapshot.OutboundUdpPort;
        _outboundUdpOnPlayCommand = snapshot.OutboundUdpOnPlayCommand;
        _outboundUdpOnEndCommand = snapshot.OutboundUdpOnEndCommand;
        _outboundUdpOnPreviewCommand = snapshot.OutboundUdpOnPreviewCommand;
        _outboundUdpOnPreviewEndCommand = snapshot.OutboundUdpOnPreviewEndCommand;

        AudioDevices = _audioDevices;
        RebuildAudioDevices();
        // Sprint Livewire-4: só Add/Remove disparam recálculo (ver comentário completo em
        // RebuildAudioDevices) — renomear uma Saída já existente não atualiza o rótulo aqui até a
        // janela de Configurações ser fechada e reaberta.
        _livewireOutputs.CollectionChanged += OnLivewireOutputsChanged;

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
        _useBackgroundArt = deck.DomainDeck.UseBackgroundArt;

        _sendToScreensEnabled = deck.DomainDeck.SendToScreens;
        AvailableScreens = new ObservableCollection<ScreenOptionViewModel>(BuildAvailableScreens(deck.DomainDeck.ScreenOutputDeviceNames));

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
        SelectScreensTabCommand = new RelayCommand(_ => SelectedTab = SettingsTab.Telas);
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
                OnPropertyChanged(nameof(IsScreensTabSelected));
                OnPropertyChanged(nameof(IsAutomationTabSelected));
            }
        }
    }

    public bool IsGeneralTabSelected => SelectedTab == SettingsTab.Geral;

    public bool IsVMixTabSelected => SelectedTab == SettingsTab.VMix;

    public bool IsResolumeTabSelected => SelectedTab == SettingsTab.Resolume;

    public bool IsScreensTabSelected => SelectedTab == SettingsTab.Telas;

    public bool IsAutomationTabSelected => SelectedTab == SettingsTab.Automacao;

    public ICommand SelectGeneralTabCommand { get; }

    public ICommand SelectVMixTabCommand { get; }

    public ICommand SelectResolumeTabCommand { get; }

    public ICommand SelectScreensTabCommand { get; }

    public ICommand SelectAutomationTabCommand { get; }

    /// <summary>Nome do canal NDI que este Deck vai gerar (arquitetura §13.1) — consumido de verdade quando o NDI Sender existir (roteiro, passo 3).</summary>
    public string NdiChannelName
    {
        get => _ndiChannelName;
        set => SetProperty(ref _ndiChannelName, value);
    }

    /// <summary>
    /// Dispositivos de saída de áudio do Windows (mmdevice) + a opção "Padrão do sistema" (Value
    /// nulo) no topo + (Sprint Livewire-4, 2026-09-14) as Saídas Livewire nativas configuradas em
    /// Configurações → Saídas Livewire, cada uma com valor prefixado <c>"livewire:{Id}"</c>. Sprint
    /// Áudio-Unificado (2026-09-15): usado pelo ÚNICO seletor "Dispositivo de áudio" deste Deck —
    /// vale tanto pro Play quanto pro Preview (o seletor separado de Preview foi removido, ver
    /// comentário de classe). Tipo concreto é <see cref="ObservableCollection{T}"/> (embora exposto
    /// aqui como <see cref="IReadOnlyList{T}"/>) de propósito, mesmo padrão de
    /// <see cref="CartridgeSettingsViewModel.AudioDevices"/> — o WPF enxerga o objeto real em runtime
    /// e reage a Add/Remove de Saídas Livewire sem precisar trocar o Binding, ver
    /// <see cref="RebuildAudioDevices"/>.
    /// </summary>
    public IReadOnlyList<ComboOption<string?>> AudioDevices { get; }

    /// <summary>
    /// Dispositivo de saída de áudio do Windows (ou Saída Livewire nativa) usado pelo áudio local
    /// deste Deck — Sprint Áudio-Dispositivo-Deck (2026-08-11). Nulo = padrão do sistema. Não afeta o
    /// áudio já enviado pelo NDI (caminho separado, ver <see cref="Engine.PlaybackEngine.SetAudioOutputDevice"/>).
    /// Sprint Áudio-Unificado (2026-09-15): usado tanto pelo Play quanto pelo Preview deste Deck.
    /// </summary>
    public string? AudioDeviceId
    {
        get => _audioDeviceId;
        set => SetProperty(ref _audioDeviceId, value);
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

    /// <summary>
    /// Sprint Arte-de-Fundo (2026-11-10): verdadeiro = a saída NDI deste Deck manda a arte ativa
    /// (Configurações → Arte de Fundo) quando ele não está tocando (Cue/Stopped/Finished), em vez
    /// de parar de mandar quadro nenhum — ver <see cref="Core.Deck.UseBackgroundArt"/> pro
    /// comportamento completo. Pedido do usuário: só precisa disso no Deck que vai pro Resolume/
    /// telões (hoje, o Deck 2) — o Deck que vai pro vMix (Deck 1) não precisa ligar isto.
    /// </summary>
    public bool UseBackgroundArt
    {
        get => _useBackgroundArt;
        set => SetProperty(ref _useBackgroundArt, value);
    }

    /// <summary>
    /// Sprint Telas-Saida-Direta (2026-11-10): verdadeiro = este Deck desenha seu vídeo diretamente
    /// nas telas marcadas em <see cref="AvailableScreens"/>, usando a placa de vídeo local do PC —
    /// sem depender do Resolume. Ver <see cref="Core.Deck.SendToScreens"/> pro comportamento
    /// completo. Pedido do usuário: o Deck 2 vai parar de ir pro Resolume e vai direto pros telões
    /// conectados na placa de vídeo do PC.
    /// </summary>
    public bool SendToScreensEnabled
    {
        get => _sendToScreensEnabled;
        set => SetProperty(ref _sendToScreensEnabled, value);
    }

    /// <summary>
    /// Monitores do Windows detectados agora (<c>System.Windows.Forms.Screen.AllScreens</c>) mesclados
    /// com a seleção persistida deste Deck (<see cref="Core.Deck.ScreenOutputDeviceNames"/>) — ver
    /// <see cref="BuildAvailableScreens"/>. Cada linha é um checkbox na aba "Telas"; uma linha cujo
    /// monitor não está conectado agora (<see cref="ScreenOptionViewModel.IsCurrentlyConnected"/>
    /// falso) continua marcada, mas a tela de Configuração mostra um aviso nela.
    /// </summary>
    public ObservableCollection<ScreenOptionViewModel> AvailableScreens { get; }

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

    /// <summary>Sprint Áudio-Unificado (2026-09-15): texto mandado pro destino acima quando este Deck entra em Preview — mesmo toggle/destino do Play/Fim, só o texto muda. Existe pra mesa/console externa saber diferenciar Play de Preview agora que os dois usam a mesma placa de áudio.</summary>
    public string OutboundUdpOnPreviewCommand
    {
        get => _outboundUdpOnPreviewCommand;
        set => SetProperty(ref _outboundUdpOnPreviewCommand, value);
    }

    /// <summary>Texto mandado pro destino acima quando o Preview deste Deck termina — fim natural OU Stop/Clear manual, mesmo critério de <see cref="OutboundUdpOnEndCommand"/>.</summary>
    public string OutboundUdpOnPreviewEndCommand
    {
        get => _outboundUdpOnPreviewEndCommand;
        set => SetProperty(ref _outboundUdpOnPreviewEndCommand, value);
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

        deck.DomainDeck.UseBackgroundArt = UseBackgroundArt;

        deck.DomainDeck.SendToScreens = SendToScreensEnabled;
        deck.DomainDeck.ScreenOutputDeviceNames.Clear();
        foreach (var screen in AvailableScreens.Where(s => s.IsSelected))
        {
            deck.DomainDeck.ScreenOutputDeviceNames.Add(screen.DeviceName);
        }

        deck.DomainDeck.AutomationActions.Clear();
        foreach (var actionViewModel in OnPlayActions.Concat(OnEndActions))
        {
            actionViewModel.ApplyToModel();
            deck.DomainDeck.AutomationActions.Add(actionViewModel.Model);
        }
    }

    /// <summary>
    /// Sprint Livewire-4 (2026-09-14): (re)monta <see cref="_audioDevices"/> do zero — dispositivos
    /// Windows (mmdevice, enumerados de novo a cada chamada) + Saídas Livewire configuradas agora em
    /// <see cref="_livewireOutputs"/>. Chamado uma vez na construção e sempre que
    /// <see cref="_livewireOutputs"/> ganha ou perde um item (<see cref="OnLivewireOutputsChanged"/>)
    /// — RENOMEAR uma Saída já existente não chama isto, mesma limitação honesta já aceita e
    /// documentada em <see cref="CartridgeSettingsViewModel.RebuildAudioDevices"/> (o rótulo só
    /// atualiza depois de fechar e reabrir Configurações).
    /// </summary>
    private void RebuildAudioDevices()
    {
        _audioDevices.Clear();
        _audioDevices.Add(new ComboOption<string?> { Value = null, Label = "Padrão do sistema" });

        foreach (var device in AudioEngine.EnumerateOutputDevices())
        {
            _audioDevices.Add(new ComboOption<string?> { Value = device.DeviceId, Label = device.Description });
        }

        foreach (var output in _livewireOutputs)
        {
            _audioDevices.Add(new ComboOption<string?>
            {
                Value = AudioEngine.ExternalOutputPrefix + output.Id,
                Label = $"Livewire: {output.Name} (canal {output.Channel})"
            });
        }
    }

    private void OnLivewireOutputsChanged(object? sender, NotifyCollectionChangedEventArgs e) => RebuildAudioDevices();

    /// <summary>
    /// Sprint Telas-Saida-Direta (2026-11-10): monta a lista da aba "Telas" combinando os monitores
    /// que o Windows detecta AGORA (<c>System.Windows.Forms.Screen.AllScreens</c> — nome totalmente
    /// qualificado de propósito, nunca <c>using System.Windows.Forms;</c> neste projeto por causa da
    /// ambiguidade já documentada no .csproj com tipos WPF homônimos como <c>Application</c>) com a
    /// seleção persistida (<paramref name="persistedDeviceNames"/>). Um monitor detectado agora entra
    /// marcado se já estava na seleção salva; um nome salvo que NÃO corresponde a nenhum monitor
    /// detectado agora (placa desconectada) entra como uma linha extra, marcada, com
    /// <see cref="ScreenOptionViewModel.IsCurrentlyConnected"/> falso — "falha honesta" em vez de
    /// simplesmente descartar a escolha do operador por uma desconexão temporária.
    ///
    /// Fix v1.0.57 (2026-11-11): também marca qual linha é <see cref="ScreenOptionViewModel.IsOperatorScreen"/>
    /// — o monitor onde a própria Cartucheira está rodando agora — reaproveitando
    /// <see cref="DeckViewModel.GetOperatorScreenDeviceNames"/> (mesma checagem usada pra nunca
    /// abrir uma Tela ali). Incidente real que motivou isto: o usuário achou que a numeração
    /// "Tela 1"/"Tela 2" estava "invertida" ou "errada" porque marcar a tela onde ele próprio
    /// estava não mandava vídeo pra lugar nenhum — sem essa indicação visual, não tinha como saber
    /// que era bloqueio proposital, não bug de detecção.
    /// </summary>
    private static IEnumerable<ScreenOptionViewModel> BuildAvailableScreens(IEnumerable<string> persistedDeviceNames)
    {
        var persisted = new List<string>(persistedDeviceNames);
        var detectedNames = new HashSet<string>();
        var result = new List<ScreenOptionViewModel>();
        var operatorDeviceNames = DeckViewModel.GetOperatorScreenDeviceNames();

        var detectedScreens = System.Windows.Forms.Screen.AllScreens;
        for (var i = 0; i < detectedScreens.Length; i++)
        {
            var screen = detectedScreens[i];
            detectedNames.Add(screen.DeviceName);
            var label = $"Tela {i + 1} — {screen.Bounds.Width}x{screen.Bounds.Height} em ({screen.Bounds.X},{screen.Bounds.Y})";
            result.Add(new ScreenOptionViewModel(screen.DeviceName, label, persisted.Contains(screen.DeviceName), isCurrentlyConnected: true, isOperatorScreen: operatorDeviceNames.Contains(screen.DeviceName)));
        }

        foreach (var deviceName in persisted)
        {
            if (!detectedNames.Contains(deviceName))
            {
                result.Add(new ScreenOptionViewModel(deviceName, $"{deviceName} (não detectada agora)", isSelected: true, isCurrentlyConnected: false));
            }
        }

        return result;
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
