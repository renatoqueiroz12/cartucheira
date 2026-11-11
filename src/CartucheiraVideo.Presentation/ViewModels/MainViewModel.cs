using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Threading;
using CartucheiraVideo.Api;
using CartucheiraVideo.Configuration;
using CartucheiraVideo.Core;
using CartucheiraVideo.Infrastructure;
using CartucheiraVideo.Infrastructure.Livewire;
using CartucheiraVideo.Ndi;
using CartucheiraVideo.Presentation.Mvvm;
using CartucheiraVideo.Presentation.Views;
using LibVLC = LibVLCSharp.Shared.LibVLC;

namespace CartucheiraVideo.Presentation.ViewModels;

/// <summary>
/// ViewModel da janela principal. Concentra a logica de "pra qual Deck vai esse arquivo":
/// duplo clique (Explorer ou Playlist) usa o algoritmo esperto (deck vazio > deck parado >
/// pergunta se os dois estao tocando); arrastar direto num Deck (ver MainWindow.xaml.cs) carrega
/// nesse Deck especifico, sem perguntar nada — essa sprint nao bloqueia drop em Deck tocando (ver
/// arquitetura §11).
///
/// Tambem concentra os Bancos de Botoes da Botoneira (mockup v8: dois bancos fixos, Vinhetas e
/// Trilhas, sempre visiveis) e o status (so visual, sempre desconectado) das saidas NDI/vMix/
/// Resolume na barra superior.
///
/// Desde os ajustes de Deck (2026-08-09), tambem e responsavel pelo espelhamento Deck 1 → Deck 2
/// (ver <see cref="OnDeckPlay"/>): dar Play no Deck 1 carrega automaticamente o mesmo arquivo no
/// Deck 2, sem tocar.
///
/// Desde a Sprint Config Geral, parte 1 (2026-08-09), tambem e o composition root da persistencia
/// de configuracao: guarda o <see cref="SettingsStore"/> e o <see cref="AppSettingsSnapshot"/>
/// carregado do disco, aplica esse snapshot nos Decks logo na construcao (antes de qualquer outra
/// interacao), e controla abrir/fechar a janela unica de Configuracoes — protegida por senha de
/// admin (definida no primeiro acesso, exigida em login nos seguintes) — via
/// <see cref="OpenSettingsCommand"/>.
///
/// Desde a Sprint Cartucheira (2026-08-09), os dois bancos da Botoneira tocam áudio de verdade
/// (ver <see cref="ReloadCartridgeBanksFromSettings"/>) — populados a partir da pasta/dispositivo
/// configurados em Configurações → Cartucheira, recarregados sempre que essa janela fecha.
///
/// Sprint Comando Externo (2026-08-10): também é o dono do <see cref="GlobalHotkeyService"/> (um
/// atalho de teclado global por Cartucheira), do <see cref="LocalTriggerApiHost"/> (servidor HTTP
/// local, uma rota por Cartucheira, pro Stream Deck/Companion) e, desde a correção pós-entrega
/// desta mesma data, do <see cref="UdpTriggerListener"/> (mesa de áudio real via UDP, uma porta só
/// pras duas Cartucheiras — quem diferencia é o texto do comando, ver
/// <see cref="TryTriggerExternalUdpCommand"/>) — os três canais convergem em
/// <c>AudioBankViewModel.TriggerExternalCommand</c>/<c>StopExternalCommand</c> (arquitetura §14.2).
/// Teclado/HTTP aplicados/reaplicados em <see cref="ApplyExternalTriggerSettings"/>, mesmo
/// ponto/mesmo gatilho (construção + fechar Configurações) que
/// <see cref="ReloadCartridgeBanksFromSettings"/> já usa; UDP não precisa reaplicar nada (o texto
/// configurado é consultado a cada pacote recebido, mesma ideia do HTTP).
/// </summary>
public sealed class MainViewModel : ViewModelBase, IDisposable
{
    private string _statusMessage = "Pronto.";
    private string _clock = DateTime.Now.ToString("HH:mm:ss");
    private readonly DispatcherTimer _clockTimer;
    private readonly SettingsStore _settingsStore;
    private readonly AppSettingsSnapshot _persistedSettings;
    private readonly LibVLC _libVlc;
    private readonly GlobalHotkeyService _hotkeyService = new();
    /// <summary>Porta/rotas vêm de <see cref="LocalTriggerApiHost"/> (fonte única de verdade) — correção (2026-08-10) pra <see cref="CartridgeSettingsViewModel"/> exibir a mesma URL sem duplicar o número da porta.</summary>
    private readonly LocalTriggerApiHost _triggerApiHost = new(LocalTriggerApiHost.DefaultPort);
    /// <summary>
    /// Comando externo da mesa de áudio via UDP — Sprint Comando Externo, correção pós-entrega
    /// (2026-08-10), protocolo confirmado pelo usuário. UMA porta só, compartilhada por TUDO — as
    /// duas Cartucheiras E os dois Decks (<see cref="AppSettingsSnapshot.UdpTriggerPort"/>) — quem
    /// diferencia é sempre o texto do pacote (ver <see cref="TryTriggerExternalUdpCommand"/>).
    /// Consolidação 2026-08-13: existiam duas portas separadas (Cartucheira/Deck), que causaram
    /// confusão real de configuração (trocar uma sem perceber que a outra continuava diferente) —
    /// uma porta só elimina essa classe de erro. NÃO é <c>readonly</c> (única exceção no arquivo):
    /// a porta é editável em Configurações e passa a valer sem reiniciar o programa — ver
    /// <see cref="ReapplyUdpListenerPort"/>, chamado sempre que a janela de Configurações fecha,
    /// mesmo gatilho já usado por <see cref="ReloadCartridgeBanksFromSettings"/>/
    /// <see cref="ApplyExternalTriggerSettings"/>. Como depende de <see cref="_persistedSettings"/>
    /// carregado, a primeira instância é construída dentro do corpo do construtor, não como
    /// inicializador de campo.
    /// </summary>
    private UdpTriggerListener _udpTriggerListener;
    /// <summary>
    /// Avisa por UDP um destino externo (mesa/console que o usuário está desenvolvendo) quando um
    /// Deck dá Play e quando termina — Sprint Comando Externo Deck→Mesa (2026-08-13, arquitetura
    /// §15), direção CONTRÁRIA do <see cref="_udpTriggerListener"/> (que recebe). UMA instância só,
    /// reaproveitada pelos dois Decks — cada um manda pro seu próprio IP:porta (independente por
    /// Deck, decisão explícita do usuário), configurado em <see cref="DeckSettingsSnapshot.OutboundUdpHost"/>/
    /// <see cref="DeckSettingsSnapshot.OutboundUdpPort"/>. Sem dependência de <see cref="_persistedSettings"/>
    /// pra construir (não tem porta própria pra abrir, só manda pro destino que cada chamada
    /// especificar) — por isso, diferente do <see cref="_udpTriggerListener"/>, pode ser um campo
    /// <c>readonly</c> com inicializador normal.
    /// </summary>
    private readonly UdpTriggerSender _outboundUdpSender = new();
    /// <summary>
    /// Sprint Livewire-3 (2026-09-13, reordenada na frente do Deck): anuncia TODAS as Saídas Livewire
    /// configuradas (independente de estarem selecionadas em algum Deck/Cartucheira) — instância
    /// ÚNICA por processo, diferente de <see cref="Engine.AudioEngine"/>/<c>LivewireSender</c> (um
    /// por banco). Alimentada/reaberta por <see cref="ReapplyLivewireAdvertiser"/>, chamado na
    /// construção e sempre que a janela de Configurações fecha — mesmo gatilho já usado por
    /// <see cref="ReloadCartridgeBanksFromSettings"/>.
    /// </summary>
    private readonly LivewireAdvertiser _livewireAdvertiser = new();
    /// <summary>Última placa de rede usada pra (re)abrir <see cref="_livewireAdvertiser"/> — só reabre o socket quando este valor muda de verdade; a lista de Saídas em si é atualizada sem reabrir nada (<see cref="LivewireAdvertiser.UpdateSources"/>).</summary>
    private string? _livewireAdvertiserInterfaceIp;
    /// <summary>Falso até o primeiro <see cref="ReapplyLivewireAdvertiser"/> — necessário porque o valor inicial de <see cref="_livewireAdvertiserInterfaceIp"/> (nulo) é indistinguível de "placa configurada = deixar o sistema escolher" (também nulo); sem isto a primeira chamada nunca chamaria <see cref="LivewireAdvertiser.Start"/> quando nenhuma placa específica estiver configurada (o caso mais comum).</summary>
    private bool _livewireAdvertiserStarted;
    /// <summary>Último valor aplicado por <see cref="ReapplyNdiNetworkAdapter"/> — só regrava o arquivo do Windows (<see cref="NdiNetworkAdapterConfig"/>, que vale pra máquina inteira) quando este valor muda de verdade desde a última vez, pra não ficar reescrevendo um arquivo compartilhado com outros programas NDI toda vez que Configurações fecha sem essa aba ter mudado nada.</summary>
    private string? _ndiNetworkInterfaceIpApplied;
    /// <summary>Falso até a primeira <see cref="ReapplyNdiNetworkAdapter"/> — mesmo motivo do <see cref="_livewireAdvertiserStarted"/> acima (nulo inicial indistinguível de "configurado = deixar o NDI escolher").</summary>
    private bool _ndiNetworkAdapterApplied;
    private SettingsWindow? _settingsWindow;
    private int? _vinhetasHotkeyId;
    private int? _trilhasHotkeyId;

    /// <summary>
    /// Sprint Deck-pausa-Cartucheira (2026-08-11, revisada no mesmo dia — v2): conjunto dos Decks
    /// que estão, agora mesmo, segurando a pausa das duas Cartucheiras (Vinhetas/Trilhas) — ver
    /// <see cref="PauseCartucheiraFor"/>/<see cref="ResumeCartucheiraFor"/>. Contagem por referência
    /// em vez de um bool solto de propósito: cobre o caso dos dois Decks com uma ação "Ao dar Play →
    /// Cartucheira" (ver <see cref="HasCartucheiraOnPlayAction"/>) tocando ao mesmo tempo — a
    /// Cartucheira só deve voltar a tocar quando o ÚLTIMO dos dois soltar a pausa, nunca no
    /// primeiro.
    /// </summary>
    private readonly HashSet<DeckViewModel> _decksHoldingCartucheiraPause = new();

    /// <summary>
    /// Sprint Engine (2026-08-08): recebe a instancia UNICA e compartilhada de <see cref="LibVLC"/>
    /// do composition root (App.xaml.cs) — cada Deck cria seu proprio PlaybackEngine (e portanto
    /// seu proprio MediaPlayer) em cima dela, mas o LibVLC em si nao e recriado por Deck.
    /// </summary>
    public MainViewModel(LibVLC libVlc)
    {
        ArgumentNullException.ThrowIfNull(libVlc);

        _libVlc = libVlc;

        // Sprint Config Geral, parte 1 (2026-08-09): restaura a configuração salva (se houver)
        // ANTES de qualquer outra coisa tocar nos Decks — primeira vez que algo sobrevive a fechar
        // e abrir o programa de novo.
        _settingsStore = new SettingsStore();
        _persistedSettings = _settingsStore.Load();

        // Fix pós-entrega da Sprint NDI-Firewall-Livewire (v1.0.26, 2026-09-13, mesmo dia da
        // v1.0.25): estas duas chamadas MOVIDAS pra ANTES de criar qualquer DeckViewModel — na
        // ordem antiga (depois dos Decks), o usuário confirmou com testes reais que, mesmo com as
        // regras de Firewall corretamente instaladas e visíveis no `netsh ... show rule`, o vídeo
        // continuava chegando pela placa do Livewire ao desconectar o cabo. Causa: cada
        // DeckViewModel cria um `NdiSender` já "sempre quente" (NdiSender.cs — começa a anunciar e
        // aceitar conexão imediatamente, antes de qualquer Play) — e os Decks eram construídos ANTES
        // de `ReapplyNdiFirewallGuard` terminar de aplicar a regra (que pode levar vários segundos
        // esperando o usuário clicar em "Sim" na janela de UAC). Nessa janela de tempo, um receptor
        // já em rede podia abrir a conexão TCP de vídeo pela placa do Livewire ANTES do bloqueio
        // existir — e o Windows Firewall não derruba conexão já estabelecida quando uma regra de
        // bloqueio nova aparece depois, só filtra conexões NOVAS a partir dali. Resultado: a conexão
        // "furava" o bloqueio na inicialização e ficava presa naquele estado (furada) pelo resto da
        // sessão, mesmo com as regras certas instaladas — dando a impressão de que o bloqueio não
        // funcionava. Agora nenhum `NdiSender` existe ainda neste ponto do construtor, então não tem
        // como uma conexão ser aceita antes do bloqueio estar de pé.
        ReapplyNdiNetworkAdapter();
        ReapplyNdiFirewallGuard();

        // Sprint Livewire-4 (2026-09-14): os dois últimos argumentos são os mesmos já passados pra
        // cada AudioBankViewModel (ver Banks logo abaixo) — deixam cada Deck resolver uma Saída
        // Livewire selecionada em Configurações (DeckSettingsViewModel.AudioDeviceId = "livewire:{Id}")
        // sem precisar conhecer a lista completa nem a interface de rede escolhida.
        //
        // Sprint Arte-de-Fundo (2026-11-10): GetActiveBackgroundArtPath é novo, mesmo padrão das
        // duas funções Livewire acima — ver comentário completo lá embaixo.
        DeckA = new DeckViewModel("DECK 1", libVlc, OnDeckPlay, OnDeckPause, OnDeckStop, OnDeckMediaEnded, OnDeckError, SetStatus, ResolveLivewireOutput, GetLivewireNetworkInterfaceIp, GetActiveBackgroundArtPath, OnDeckPreview, OnDeckPreviewEnd);
        DeckB = new DeckViewModel("DECK 2", libVlc, OnDeckPlay, OnDeckPause, OnDeckStop, OnDeckMediaEnded, OnDeckError, SetStatus, ResolveLivewireOutput, GetLivewireNetworkInterfaceIp, GetActiveBackgroundArtPath, OnDeckPreview, OnDeckPreviewEnd);
        Decks = new[] { DeckA, DeckB };

        SettingsMapper.ApplyToDeck(_persistedSettings.DeckA, DeckA);
        SettingsMapper.ApplyToDeck(_persistedSettings.DeckB, DeckB);

        // Sprint Arte-de-Fundo (2026-11-10): ApplyToDeck acima só escreve DomainDeck.UseBackgroundArt
        // — não passa por State nenhuma vez (o Deck começa e continua em Cue), então o gatilho normal
        // (DeckViewModel.State setter chamando UpdateBackgroundArtIdleSender) nunca dispara sozinho
        // na inicialização. Por isso a primeira avaliação (RefreshScreenOutputs + RefreshBackgroundArt)
        // precisa ser forçada manualmente — mas NÃO aqui dentro do construtor, ver
        // InitializeAfterMainWindowShown() logo abaixo pro motivo (incidente real, Fix v1.0.54).

        // Sprint Cartucheira (2026-08-09): dois bancos fixos (Vinhetas/Trilhas), cada um com seu
        // próprio Engine.AudioEngine independente (NAudio desde a Sprint Crossfade v2, 2026-08-11 —
        // ver AudioEngine.cs) — populados a partir da pasta/dispositivo persistidos
        // (ReloadCartridgeBanksFromSettings), não mais com dados
        // fictícios (CreateSampleBanks/CreateSampleButtons1/2, removidos nesta sprint). Sprint
        // Cartucheira 2, parte 2 (2026-08-10): cada banco também recebe um callback pra persistir
        // cor/loop por cartucho (PersistCartridgeOverride) — cada um salva na sua própria lista
        // (Cartridges.VinhetasOverrides/TrilhasOverrides).
        // Sprint Livewire-2 (2026-09-13): os dois últimos parâmetros deixam cada AudioBankViewModel
        // resolver uma Saída Livewire selecionada (SetOutputDevice("livewire:{Id}")) sem precisar
        // conhecer a lista inteira nem a interface de rede escolhida — ambos leem sempre
        // _persistedSettings, que já reflete qualquer edição salva (mesma instância por referência
        // usada pelo SettingsViewModel/LivewireOutputsSettingsViewModel).
        Banks = new ObservableCollection<AudioBankViewModel>
        {
            new("Vinhetas", SetStatus, (path, color, loop) => PersistCartridgeOverride(_persistedSettings.Cartridges.VinhetasOverrides, path, color, loop),
                ResolveLivewireOutput, GetLivewireNetworkInterfaceIp),
            new("Trilhas", SetStatus, (path, color, loop) => PersistCartridgeOverride(_persistedSettings.Cartridges.TrilhasOverrides, path, color, loop),
                ResolveLivewireOutput, GetLivewireNetworkInterfaceIp)
        };
        ReloadCartridgeBanksFromSettings();

        // Sprint Livewire-3 (2026-09-13): sobe o anunciador já na construção, mesmo sem nenhuma
        // Saída Livewire configurada ainda (a thread fica ociosa, sem mandar nada, até a lista deixar
        // de estar vazia — ver ReapplyLivewireAdvertiser/LivewireAdvertiser.Loop). Precisa vir DEPOIS
        // de _persistedSettings carregado, por isso aqui no corpo do construtor.
        ReapplyLivewireAdvertiser();

        // Sprint Comando Externo (2026-08-10): as duas rotas HTTP existem sempre (uma por
        // Cartucheira) — o handler consulta o "habilitado" salvo em Config a cada chamada (ver
        // ApplyExternalTriggerSettings), então não precisa recriar rota nenhuma quando a
        // configuração muda, só o atalho de teclado precisa ser registrado/removido de novo.
        _triggerApiHost.RegisterRoute(LocalTriggerApiHost.VinhetasRoutePath, () => TryTriggerExternally(VinhetasBank, _persistedSettings.Cartridges.VinhetasExternalTriggerEnabled));
        _triggerApiHost.RegisterRoute(LocalTriggerApiHost.TrilhasRoutePath, () => TryTriggerExternally(TrilhasBank, _persistedSettings.Cartridges.TrilhasExternalTriggerEnabled));
        var apiStart = _triggerApiHost.Start();
        StatusMessage = apiStart.Succeeded
            ? $"Comando externo por Stream Deck/Companion pronto em {_triggerApiHost.BaseUrl}."
            : $"Comando externo por Stream Deck/Companion indisponível: {apiStart.ErrorMessage}";

        // Correção pós-entrega (2026-08-10), consolidada em 2026-08-13: canal único de mesa de áudio
        // via UDP — Cartucheira (Vinhetas/Trilhas) E Deck (1/2) todos na MESMA porta, "habilitado" e
        // texto de cada elemento consultados a cada pacote recebido via TryTriggerExternalUdpCommand,
        // então não precisa recriar nada quando texto/toggle mudam — só quando a PORTA muda (ver
        // ReapplyUdpListenerPort, chamado ao fechar Configurações). Callback chamado numa thread de
        // fundo do UdpTriggerListener — marcha pra Dispatcher.Invoke antes de tocar em qualquer
        // AudioBankViewModel/DeckViewModel, mesmo contrato já usado pro teclado/HTTP.
        _udpTriggerListener = new UdpTriggerListener(_persistedSettings.UdpTriggerPort);
        var udpStart = _udpTriggerListener.Start(text => Application.Current?.Dispatcher.Invoke(() => TryTriggerExternalUdpCommand(text)));
        StatusMessage = udpStart.Succeeded
            ? $"Comando externo por mesa de áudio (UDP) pronto em 127.0.0.1:{udpStart.Port}."
            : $"Comando externo por mesa de áudio (UDP) indisponível: {udpStart.ErrorMessage}";

        ApplyExternalTriggerSettings();

        // Fix v1.0.56 (2026-11-11): atalho de emergência "fechar todas as Telas" — sempre ativo,
        // não é uma opção de Configurações (ver RegisterEmergencyCloseScreensHotkey).
        RegisterEmergencyCloseScreensHotkey();

        NdiOutputs = new ObservableCollection<NdiOutputStatusViewModel>
        {
            new() { Name = "vMix", IsConnected = false },
            new() { Name = "Resolume", IsConnected = false },
        };

        FileExplorer = new FileExplorerViewModel();
        FileExplorer.FileActivated += OnExplorerFileActivated;
        FileExplorer.Playlist.ItemActivated += OnPlaylistItemActivated;

        OpenSettingsCommand = new RelayCommand(_ => OpenSettings());
        OpenAboutCommand = new RelayCommand(_ => StatusMessage = "Cartucheira Vídeo — protótipo de interface.");
        OpenBanksManagerCommand = new RelayCommand(_ => StatusMessage = "Gerenciador de bancos ainda não implementado — use Renomear/Recarregar em cada metade da Botoneira, ou a pasta configurada em Configurações → Cartucheira, por enquanto.");
        OpenLogsCommand = new RelayCommand(_ => StatusMessage = "Tela de Logs ainda não implementada.");

        _clockTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _clockTimer.Tick += (_, _) => Clock = DateTime.Now.ToString("HH:mm:ss");
        _clockTimer.Start();
    }

    /// <summary>
    /// Fix v1.0.54 (2026-11-11): chamado por <c>App.OnStartup</c> logo DEPOIS de
    /// <c>mainWindow.Show()</c> — nunca de dentro deste construtor. Faz a primeira avaliação de
    /// Telas/Arte de Fundo dos dois Decks, mesmo papel que <see cref="DeckViewModel.RefreshScreenOutputs"/>/
    /// <see cref="DeckViewModel.RefreshBackgroundArt"/> já cumprem ao Salvar em Configurações —
    /// necessário porque <c>ApplyToDeck</c> (chamado no construtor, acima) só escreve os campos,
    /// nunca passa <see cref="DeckViewModel.State"/> por uma transição, então nenhum dos dois
    /// gatilhos normais dispara sozinho na inicialização.
    ///
    /// Esta chamada só funciona corretamente DEPOIS da janela principal já estar mostrada: a
    /// checagem de segurança que impede uma Tela de abrir em cima do monitor do operador
    /// (<see cref="DeckViewModel.RefreshScreenOutputs"/>) resolve isso olhando
    /// <see cref="Application.Current"/>.Windows — antes do <c>Show()</c> essa coleção está vazia
    /// (a MainWindow sequer existe ainda nesta classe, que é construída ANTES dela em
    /// <c>App.OnStartup</c>), então a checagem não acha nada pra proteger e a tela acaba abrindo
    /// mesmo em cima do monitor onde a janela principal está prestes a aparecer — foi exatamente
    /// o incidente real que motivou o Fix v1.0.54 (o usuário via tela preta direto ao abrir o
    /// programa, mesmo já com a proteção daquele fix). Chamar isto só depois do <c>Show()</c>
    /// resolve na raiz, sem precisar de nenhum atraso/retry artificial.
    /// </summary>
    public void InitializeAfterMainWindowShown()
    {
        DeckA.RefreshScreenOutputs();
        DeckB.RefreshScreenOutputs();

        DeckA.RefreshBackgroundArt();
        DeckB.RefreshBackgroundArt();
    }

    public string Title => "Cartucheira Vídeo";

    /// <summary>
    /// Sprint Versao-Numero-Manual (2026-09-12): numero incremental (v1.0.X), atualizado A MAO por
    /// mim a cada sprint/correcao entregue — decisao explicita do usuario em vez de derivar a versao
    /// da data de compilacao ou do commit do Git (as correcoes recentes tem sido aplicadas via copia
    /// manual de arquivo .cs, sem passar pelo Git a cada teste, entao um numero ligado ao Git ficaria
    /// desatualizado). Convencao: incrementar o ultimo digito (X) a cada entrega de codigo nova,
    /// nunca pular numero, nunca reaproveitar um numero ja usado — ver
    /// arquitetura/versao-atual.md (doc do projeto) pro numero mais recente entregue, fonte de
    /// verdade entre sessoes.
    /// </summary>
    public string VersionLabel => "v1.0.59";

    public DeckViewModel DeckA { get; }

    public DeckViewModel DeckB { get; }

    public IReadOnlyList<DeckViewModel> Decks { get; }

    /// <summary>
    /// Bancos de botoes da Botoneira (audio-only). Desde a conversao pro mockup v8, os DOIS bancos
    /// ficam sempre visiveis lado a lado (nao ha mais "banco selecionado" por dropdown) — ver
    /// <see cref="VinhetasBank"/>/<see cref="TrilhasBank"/>, que so indexam esta lista. Desde a
    /// Sprint Cartucheira (2026-08-09), cada banco toca áudio de verdade (ver
    /// <see cref="AudioBankViewModel"/>) — populados a partir de uma pasta real, não mais fictícios.
    /// </summary>
    public ObservableCollection<AudioBankViewModel> Banks { get; }

    /// <summary>Metade esquerda da Botoneira (mockup v8, nota #15) — sempre <c>Banks[0]</c>.</summary>
    public AudioBankViewModel VinhetasBank => Banks[0];

    /// <summary>Metade direita da Botoneira — sempre <c>Banks[1]</c>.</summary>
    public AudioBankViewModel TrilhasBank => Banks[1];

    /// <summary>Status (visual) das integrações vMix/Resolume exibido na barra superior.</summary>
    public ObservableCollection<NdiOutputStatusViewModel> NdiOutputs { get; }

    /// <summary>Painel lateral de arquivos (abas Explorer/Playlist, mockup v8).</summary>
    public FileExplorerViewModel FileExplorer { get; }

    public RelayCommand OpenSettingsCommand { get; }

    public RelayCommand OpenAboutCommand { get; }

    public RelayCommand OpenBanksManagerCommand { get; }

    public RelayCommand OpenLogsCommand { get; }

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    public string Clock
    {
        get => _clock;
        private set => SetProperty(ref _clock, value);
    }

    /// <summary>
    /// Carrega um arquivo num Deck especifico — usado pelo drop direto num Deck (arraste
    /// consciente do operador, sempre aceito nesta sprint). <paramref name="filePath"/> precisa
    /// ser o caminho COMPLETO do arquivo (desde a Sprint Engine — o Deck real abre o arquivo via
    /// LibVLC, nao so exibe o nome), nao apenas o nome de exibicao.
    /// </summary>
    public void LoadIntoDeck(DeckViewModel deck, string filePath)
    {
        deck.Load(filePath);
        FileExplorer.PushRecent(filePath);
        StatusMessage = $"\"{deck.LoadedFileName}\" carregado no {deck.Name}.";
    }

    /// <summary>Ponto único de escrita de StatusMessage passado como delegate pra colaboradores (DeckViewModel) que não podem chamar o setter privado direto.</summary>
    private void SetStatus(string message) => StatusMessage = message;

    /// <summary>
    /// Ajustes de Deck (2026-08-09): ao dar Play no Deck 1, o mesmo arquivo é carregado
    /// automaticamente no Deck 2, SEM tocar — fica pronto pra ser disparado manualmente depois (ex.:
    /// pra mandar pro Resolume/telas depois de um comentário sobre o vídeo que já foi ao ar pelo
    /// Deck 1). Direção fixa (só Deck 1 → Deck 2, nunca o contrário) e sempre ativa, sem toggle —
    /// decisão do usuário. <see cref="DeckViewModel.Load"/> já é seguro de chamar mesmo se o Deck 2
    /// estiver tocando outra coisa (mesmo comportamento já usado pelo drag&amp;drop/duplo clique
    /// desde a Sprint 4 — carregar sobrepõe o que estiver tocando).
    /// </summary>
    private void OnDeckPlay(DeckViewModel deck)
    {
        StatusMessage = $"{deck.Name} tocando \"{deck.LoadedFileName}\".";

        PauseCartucheiraFor(deck);
        SendOutboundDeckTrigger(deck, settings => settings.OutboundUdpOnPlayCommand);

        if (ReferenceEquals(deck, DeckA) && DeckA.LoadedFilePath is { } mirroredPath)
        {
            DeckB.Load(mirroredPath);
            StatusMessage = $"{DeckB.Name} recebeu \"{DeckB.LoadedFileName}\" (espelhado do {DeckA.Name}).";
        }
    }

    private void OnDeckPause(DeckViewModel deck) =>
        StatusMessage = $"{deck.Name} pausado.";

    private void OnDeckStop(DeckViewModel deck)
    {
        StatusMessage = $"{deck.Name} parado.";
        ResumeCartucheiraFor(deck);
        SendOutboundDeckTrigger(deck, settings => settings.OutboundUdpOnEndCommand);
    }

    /// <summary>
    /// Sprint Áudio-Unificado (2026-09-15): chamado quando um Deck entra em Preview — diferente de
    /// <see cref="OnDeckPlay"/>, NUNCA pausa a Cartucheira nem espelha pro Deck B (Preview continua
    /// isolado dessas duas coisas, decisão de sempre desde a Sprint Preview-Isolado). Único efeito:
    /// mandar o aviso UDP dedicado (<see cref="DeckSettingsSnapshot.OutboundUdpOnPreviewCommand"/>)
    /// pra mesa/console externa — motivo de existir: agora que Play e Preview usam a mesma placa de
    /// áudio (ver <c>Core.Deck.AudioDeviceId</c>), a mesa precisa de um jeito de saber qual dos dois
    /// está tocando, pra decidir ela mesma a rota física (ar vs. monitor).
    /// </summary>
    private void OnDeckPreview(DeckViewModel deck)
    {
        SendOutboundDeckTrigger(deck, settings => settings.OutboundUdpOnPreviewCommand);
    }

    /// <summary>Ver <see cref="OnDeckPreview"/> — mesma ideia, mas pro fim do Preview (fim natural OU Stop/Clear manual, mesmo critério de "terminar" já usado em <see cref="OnDeckStop"/>/<see cref="OnDeckMediaEnded"/>). Nunca retoma Cartucheira — Preview nunca a pausou pra começar.</summary>
    private void OnDeckPreviewEnd(DeckViewModel deck)
    {
        SendOutboundDeckTrigger(deck, settings => settings.OutboundUdpOnPreviewEndCommand);
    }

    /// <summary>Chamado quando um Deck termina de tocar sozinho (sem loop) — ver <see cref="HasCartucheiraOnPlayAction"/>/<see cref="ResumeCartucheiraFor"/>. Confirmado explicitamente pelo usuário: o cartucho pausado pela Cartucheira volta a tocar terminando sozinho OU com Stop manual (ambos convergem aqui e em <see cref="OnDeckStop"/>) — mesmo critério usado pro aviso UDP de saída (<see cref="SendOutboundDeckTrigger"/>).</summary>
    private void OnDeckMediaEnded(DeckViewModel deck)
    {
        ResumeCartucheiraFor(deck);
        SendOutboundDeckTrigger(deck, settings => settings.OutboundUdpOnEndCommand);
    }

    /// <summary>
    /// Sprint Comando Externo Deck→Mesa (2026-08-13, arquitetura §15): manda um aviso UDP pro
    /// destino configurado deste Deck (IP:porta independentes por Deck, decisão explícita do
    /// usuário) — chamado de <see cref="OnDeckPlay"/> (texto de Play) e de <see cref="OnDeckStop"/>/
    /// <see cref="OnDeckMediaEnded"/> (texto de Fim, mesmo critério de "terminou" já usado em todo o
    /// programa). Sem efeito se o toggle estiver desligado, o IP estiver vazio, ou o texto daquele
    /// evento específico estiver vazio (mesmo padrão "campo vazio não dispara" de sempre). Envio
    /// roda numa thread de fundo (<see cref="UdpTriggerSender.Send"/>) — nunca atrasa o Play/Stop
    /// esperando rede; falha só aparece na barra de status, sem travar nada.
    /// </summary>
    private void SendOutboundDeckTrigger(DeckViewModel deck, Func<DeckSettingsSnapshot, string> selectCommand)
    {
        var settings = ReferenceEquals(deck, DeckA) ? _persistedSettings.DeckA : _persistedSettings.DeckB;
        if (!settings.OutboundUdpEnabled)
        {
            return;
        }

        var host = settings.OutboundUdpHost?.Trim();
        if (string.IsNullOrEmpty(host))
        {
            return;
        }

        var commandText = selectCommand(settings)?.Trim() ?? string.Empty;
        if (commandText.Length == 0)
        {
            return;
        }

        var port = settings.OutboundUdpPort;
        _outboundUdpSender.Send(host, port, commandText, result =>
        {
            if (result.Succeeded)
            {
                return;
            }

            Application.Current?.Dispatcher.Invoke(() =>
                StatusMessage = $"Falha ao avisar {deck.Name} → {host}:{port}: {result.ErrorMessage}");
        });
    }

    /// <summary>
    /// Sprint Deck-pausa-Cartucheira v2 (2026-08-11): pausa as duas Cartucheiras (Vinhetas e
    /// Trilhas) — só de verdade na transição vazio→não-vazio de
    /// <see cref="_decksHoldingCartucheiraPause"/>, pra nunca pausar duas vezes à toa quando os dois
    /// Decks tocam ao mesmo tempo. Sem efeito nenhum se <paramref name="deck"/> não tiver nenhuma
    /// ação "Ao dar Play → Cartucheira" configurada (<see cref="GetCartucheiraOnPlayAction"/>).
    /// Sprint fade ajustável (2026-08-11, mesmo dia): o tempo (ms) da própria ação vira o fade da
    /// pausa (ver <see cref="AutomationAction.TransitionDurationMs"/>/
    /// <see cref="AudioBankViewModel.Pause"/>) — zero (padrão) continua sendo corte seco.
    /// </summary>
    private void PauseCartucheiraFor(DeckViewModel deck)
    {
        var action = GetCartucheiraOnPlayAction(deck);
        if (action is null)
        {
            return;
        }

        var wasEmpty = _decksHoldingCartucheiraPause.Count == 0;
        if (_decksHoldingCartucheiraPause.Add(deck) && wasEmpty)
        {
            VinhetasBank.Pause(action.TransitionDurationMs);
            TrilhasBank.Pause(action.TransitionDurationMs);
        }
    }

    /// <summary>
    /// Contrapartida de <see cref="PauseCartucheiraFor"/> — chamado por <see cref="OnDeckStop"/>
    /// (Stop manual) e <see cref="OnDeckMediaEnded"/> (fim natural), os dois gatilhos confirmados
    /// explicitamente pelo usuário. Só retoma as Cartucheiras de verdade quando o ÚLTIMO Deck que
    /// estava segurando a pausa sai do conjunto — se os dois Decks estiverem pausando ao mesmo
    /// tempo, a Cartucheira só volta quando o segundo também parar/terminar. Chamar pra um Deck que
    /// não estava segurando pausa nenhuma é seguro e sem efeito (<c>HashSet.Remove</c> devolve falso
    /// nesse caso). O RETORNO em si é deliberadamente incondicional (não depende de nenhuma ação "Ao
    /// terminar → Cartucheira" — essa opção nem existe, ver
    /// <see cref="DeckSettingsViewModel.OnPlayDestinationOptions"/>): sempre que este Deck foi quem
    /// pausou, ele tem que devolver, senão a Cartucheira corre o risco de ficar presa pausada pra
    /// sempre. Sprint fade ajustável: reaproveita o MESMO tempo (ms) configurado na ação "Ao dar
    /// Play" deste Deck pro fade de volta (retomar simetricamente ao mesmo ritmo que pausou) — se a
    /// ação já não existir mais nesse momento (removida/Deck reconfigurado entre o Play e agora),
    /// cai pro corte seco (0ms), nunca trava esperando um valor que sumiu.
    /// </summary>
    private void ResumeCartucheiraFor(DeckViewModel deck)
    {
        if (!_decksHoldingCartucheiraPause.Remove(deck) || _decksHoldingCartucheiraPause.Count != 0)
        {
            return;
        }

        var fadeMs = GetCartucheiraOnPlayAction(deck)?.TransitionDurationMs ?? 0;
        VinhetasBank.Resume(fadeMs);
        TrilhasBank.Resume(fadeMs);
    }

    /// <summary>A ação "Ao dar Play" com Destino Cartucheira de <paramref name="deck"/>, se houver (aba Automação) — ver <see cref="PauseCartucheiraFor"/>/<see cref="ResumeCartucheiraFor"/>.</summary>
    private static AutomationAction? GetCartucheiraOnPlayAction(DeckViewModel deck) =>
        deck.DomainDeck.AutomationActions.FirstOrDefault(a => a.Trigger == AutomationTrigger.OnPlay && a.Destination == OutputRouteType.Cartucheira);

    /// <summary>
    /// Sprint Áudio-Dispositivo-Fix-4 (2026-09-13): DESLIGADO TEMPORARIAMENTE a pedido do usuário,
    /// enquanto ele termina a rodada de testes dos fixes de áudio em andamento (não quer digitar
    /// senha a cada teste). Pra REVERTER (voltar a exigir a senha de admin pra abrir Configurações,
    /// exatamente como era antes desta sprint): trocar esta constante de volta pra <c>true</c>. Nada
    /// mais foi removido — <see cref="AdminAuthViewModel"/>/<see cref="AdminAuthWindow"/> e toda a
    /// lógica de definir/logar com a senha continuam intactos logo abaixo, só pulados por este
    /// `if`.
    /// </summary>
    private const bool RequireAdminPasswordForSettings = false;

    /// <summary>
    /// Sprint Config Geral, parte 1 (2026-08-09): abre a janela única de Configurações — protegida
    /// por senha de admin (arquitetura: "Login e senha de admin... para acessar o config"). Fluxo:
    /// (1) se a janela já estiver aberta, só traz ela pra frente (evita duplicata); (2) se ainda
    /// não existe senha salva (primeiro acesso), pede pra DEFINIR uma — sucesso já persiste o
    /// hash/salt novos imediatamente, não espera o Salvar do Settings; (3) senão, pede LOGIN com a
    /// senha existente; (4) só então abre a janela (não-modal — o operador continua usando os Decks
    /// com ela aberta). Ver <see cref="RequireAdminPasswordForSettings"/>: passos (2)/(3) estão
    /// temporariamente desligados a pedido do usuário.
    /// </summary>
    private void OpenSettings()
    {
        if (_settingsWindow is not null)
        {
            _settingsWindow.Activate();
            return;
        }

        if (RequireAdminPasswordForSettings)
        {
            if (string.IsNullOrEmpty(_persistedSettings.AdminPasswordHash))
            {
                var setPasswordViewModel = new AdminAuthViewModel(AdminAuthMode.SetPassword);
                var setPasswordWindow = new AdminAuthWindow(setPasswordViewModel) { Owner = Application.Current?.MainWindow };

                if (setPasswordWindow.ShowDialog() != true)
                {
                    StatusMessage = "Definição de senha de admin cancelada — Configurações não abertas.";
                    return;
                }

                _persistedSettings.AdminPasswordHash = setPasswordViewModel.HashResult;
                _persistedSettings.AdminPasswordSalt = setPasswordViewModel.SaltResult;
                _settingsStore.Save(_persistedSettings);
                StatusMessage = "Senha de admin definida.";
            }
            else
            {
                var loginViewModel = new AdminAuthViewModel(AdminAuthMode.Login, _persistedSettings.AdminPasswordHash, _persistedSettings.AdminPasswordSalt);
                var loginWindow = new AdminAuthWindow(loginViewModel) { Owner = Application.Current?.MainWindow };

                if (loginWindow.ShowDialog() != true)
                {
                    StatusMessage = "Login de admin cancelado — Configurações não abertas.";
                    return;
                }
            }
        }

        var settingsViewModel = new SettingsViewModel(DeckA, DeckB, _settingsStore, _persistedSettings, SetStatus);
        _settingsWindow = new SettingsWindow(settingsViewModel) { Owner = Application.Current?.MainWindow };
        _settingsWindow.Closed += (_, _) =>
        {
            _settingsWindow = null;

            // Sprint Cartucheira (2026-08-09): recarrega os dois bancos com o que tiver sido salvo
            // (dispositivo/pasta podem ter mudado) — barato o suficiente pra rodar sempre que a
            // janela fecha, em vez de um callback dedicado só pra "a seção Cartucheira foi salva".
            ReloadCartridgeBanksFromSettings();

            // Sprint Livewire-3 (2026-09-13): idem pro anúncio — Saída criada/removida/renomeada ou
            // placa de rede trocada em Configurações → Saídas Livewire só vale de verdade a partir
            // daqui (mesmo gatilho de sempre).
            ReapplyLivewireAdvertiser();

            // Sprint NDI-Placa-de-Rede (2026-09-13): idem pra placa de rede do NDI — mudar a escolha
            // em Configurações → Rede NDI só grava o arquivo do Windows a partir daqui.
            ReapplyNdiNetworkAdapter();

            // Sprint NDI-Firewall-Livewire (2026-09-13): idem pro reforço por Firewall — mudar a
            // placa do Livewire em Configurações → Saídas Livewire só aplica/atualiza a regra a
            // partir daqui (mesmo gatilho de ReapplyLivewireAdvertiser/ReapplyNdiNetworkAdapter).
            ReapplyNdiFirewallGuard();

            // Sprint Comando Externo (2026-08-10): idem pro atalho de teclado — habilitado/tecla
            // podem ter mudado (ou o operador pode ter religado/desligado o comando externo).
            ApplyExternalTriggerSettings();

            // Consolidação da porta UDP (2026-08-13): idem pra porta do comando externo — sem isto,
            // trocar a porta em Configurações só valia depois de reiniciar o programa (o socket já
            // estava aberto na porta antiga desde a construção). Mesmo gatilho dos dois de cima.
            ReapplyUdpListenerPort();
        };
        _settingsWindow.Show();
    }

    /// <summary>
    /// Aplica o dispositivo de saída de áudio e recarrega a lista de programas de cada banco a
    /// partir da pasta RAIZ configurada em Configurações → Cartucheira (<see cref="_persistedSettings"/>
    /// já reflete qualquer edição salva, já que é a mesma instância passada por referência pro
    /// SettingsViewModel). Chamado na construção (estado inicial) e sempre que a janela de
    /// Configurações fecha. Sprint Cartucheira 2, parte 1 (2026-08-10): <c>LoadFromFolder</c> virou
    /// <c>SetRootFolder</c> — a pasta configurada agora é uma raiz de programas (subpastas), não
    /// mais uma lista direta de arquivos.
    /// </summary>
    private void ReloadCartridgeBanksFromSettings()
    {
        var cartridges = _persistedSettings.Cartridges;

        VinhetasBank.SetOutputDevice(cartridges.VinhetasAudioDeviceId);
        VinhetasBank.SetCrossfadeDuration(cartridges.VinhetasCrossfadeMs);
        VinhetasBank.SetRootFolder(cartridges.VinhetasFolderPath);
        VinhetasBank.LoadOverrides(cartridges.VinhetasOverrides);

        TrilhasBank.SetOutputDevice(cartridges.TrilhasAudioDeviceId);
        TrilhasBank.SetCrossfadeDuration(cartridges.TrilhasCrossfadeMs);
        TrilhasBank.SetRootFolder(cartridges.TrilhasFolderPath);
        TrilhasBank.LoadOverrides(cartridges.TrilhasOverrides);
    }

    /// <summary>
    /// Sprint Livewire-2 (2026-09-13): resolve o id gravado num valor prefixado
    /// (<c>"livewire:{Id}"</c>, ver <c>Engine.AudioEngine.ExternalOutputPrefix</c>) pra Saída
    /// Livewire configurada correspondente — nulo se ela foi apagada em Configurações depois de já
    /// selecionada num banco (<see cref="AudioBankViewModel"/> trata isso de forma honesta, sem
    /// inventar canal nenhum). Lê sempre <see cref="_persistedSettings"/> diretamente — já reflete
    /// qualquer edição salva, mesma instância por referência usada pelo SettingsViewModel.
    /// </summary>
    private LivewireOutputSettings? ResolveLivewireOutput(string outputId) =>
        _persistedSettings.LivewireOutputs.FirstOrDefault(o => o.Id == outputId);

    /// <summary>Sprint Livewire-2: IP local (IPv4) configurado pra transmitir Livewire — nulo deixa o sistema escolher a interface, ver <see cref="AppSettingsSnapshot.LivewireNetworkInterfaceIp"/>.</summary>
    private string? GetLivewireNetworkInterfaceIp() => _persistedSettings.LivewireNetworkInterfaceIp;

    /// <summary>
    /// Sprint Arte-de-Fundo (2026-11-10): resolve <see cref="AppSettingsSnapshot.ActiveBackgroundArtId"/>
    /// pro caminho do arquivo da arte correspondente em <see cref="AppSettingsSnapshot.BackgroundArtPresets"/>
    /// — nulo se nenhuma arte estiver ativa, ou se o Id ativo não bater com nenhuma arte da lista
    /// (ex.: ela foi removida em Configurações depois de já estar ativa — falha honesta, mesmo
    /// padrão de <see cref="ResolveLivewireOutput"/>). Lê sempre <see cref="_persistedSettings"/>
    /// diretamente — já reflete qualquer edição salva, mesma instância por referência usada pelo
    /// SettingsViewModel/BackgroundArtSettingsViewModel.
    /// </summary>
    private string? GetActiveBackgroundArtPath()
    {
        var activeId = _persistedSettings.ActiveBackgroundArtId;
        if (string.IsNullOrWhiteSpace(activeId))
        {
            return null;
        }

        return _persistedSettings.BackgroundArtPresets.FirstOrDefault(p => p.Id == activeId)?.FilePath;
    }

    /// <summary>
    /// Sprint Livewire-3 (2026-09-13): (re)aplica <see cref="_livewireAdvertiser"/> a partir do que
    /// estiver salvo agora em <see cref="_persistedSettings"/> — TODAS as Saídas Livewire configuradas
    /// (não só as selecionadas nalgum Deck/Cartucheira; ver comentário de classe de
    /// <see cref="LivewireAdvertiser"/>). Só reabre o socket (<see cref="LivewireAdvertiser.Start"/>)
    /// quando a placa de rede escolhida mudou de verdade desde a última vez — criar/remover/renomear
    /// uma Saída ou trocar seu canal não precisa disso, só atualizar a lista
    /// (<see cref="LivewireAdvertiser.UpdateSources"/>), bem mais barato.
    /// </summary>
    private void ReapplyLivewireAdvertiser()
    {
        var desiredInterfaceIp = _persistedSettings.LivewireNetworkInterfaceIp;

        if (!_livewireAdvertiserStarted || _livewireAdvertiserInterfaceIp != desiredInterfaceIp)
        {
            var machineName = Environment.MachineName;
            if (_livewireAdvertiser.Start(machineName, desiredInterfaceIp))
            {
                _livewireAdvertiserStarted = true;
            }
            else
            {
                StatusMessage = $"Anúncio Livewire indisponível: {_livewireAdvertiser.LastError}";
            }

            _livewireAdvertiserInterfaceIp = desiredInterfaceIp;
        }

        var sources = _persistedSettings.LivewireOutputs
            .Select(o => new LivewireSourceAnnouncement(o.Channel, o.Name))
            .ToList();
        _livewireAdvertiser.UpdateSources(sources);
    }

    /// <summary>
    /// Sprint NDI-Placa-de-Rede (2026-09-13): grava (ou remove) a restrição de placa de rede do NDI
    /// a partir do que estiver salvo em <see cref="_persistedSettings"/>, chamando
    /// <see cref="NdiNetworkAdapterConfig.ApplyAllowedAdapter"/> — diferente de
    /// <see cref="ReapplyLivewireAdvertiser"/>, aqui NÃO existe socket nenhum pra reabrir: é só um
    /// arquivo do Windows (<c>%ProgramData%\NDI\ndi-config.v1.json</c>, que vale pra QUALQUER
    /// programa NDI da máquina, não só a Cartucheira — ver comentário de classe de
    /// <see cref="NdiNetworkAdapterConfig"/>), regravado só quando o valor muda de verdade desde a
    /// última vez (mesmo cuidado de <see cref="_livewireAdvertiserStarted"/>, ver
    /// <see cref="_ndiNetworkAdapterApplied"/>) pra não ficar mexendo num arquivo compartilhado à
    /// toa toda vez que Configurações fecha.
    /// </summary>
    private void ReapplyNdiNetworkAdapter()
    {
        var desiredInterfaceIp = _persistedSettings.NdiNetworkInterfaceIp;

        if (_ndiNetworkAdapterApplied && _ndiNetworkInterfaceIpApplied == desiredInterfaceIp)
        {
            return;
        }

        var (ok, error) = NdiNetworkAdapterConfig.ApplyAllowedAdapter(desiredInterfaceIp);
        if (ok)
        {
            _ndiNetworkAdapterApplied = true;
            _ndiNetworkInterfaceIpApplied = desiredInterfaceIp;
        }
        else
        {
            StatusMessage = $"Não foi possível gravar a configuração de rede do NDI: {error}";
        }
    }

    /// <summary>
    /// Sprint NDI-Firewall-Livewire (2026-09-13, com fixes v1.0.24 e v1.0.25): (re)aplica o bloqueio
    /// de NDI por Firewall (<see cref="NdiFirewallGuard"/>) sempre que a placa do Livewire
    /// (<see cref="AppSettingsSnapshot.LivewireNetworkInterfaceIp"/>) OU o desenho das regras
    /// (<see cref="NdiFirewallGuard.RuleSchemaVersion"/>) mudarem desde a última vez que a regra foi
    /// aplicada com sucesso. Pedida depois que o usuário confirmou, com testes reais (reboot
    /// completo, Preferred NIC certo no NDI Access Manager, receptor reconectado do zero), que
    /// <see cref="ReapplyNdiNetworkAdapter"/> sozinho não impediu o NDI de usar a placa errada — esta
    /// é a camada de verdade, imposta pelo Windows, não um pedido que o NDI pode ignorar.
    ///
    /// **Fix pós-entrega da v1.0.24 (v1.0.25, mesmo dia):** até aqui a comparação era só do IP
    /// (<see cref="AppSettingsSnapshot.NdiFirewallGuardAppliedForLivewireIp"/>) — e isso escondeu um
    /// bug real: a v1.0.24 corrigiu o DESENHO da regra (acrescentou bloqueio de ENTRADA, que faltava
    /// na v1.0.23), mas como o IP da placa do Livewire do usuário não tinha mudado entre as duas
    /// versões, este método nunca chamou <see cref="NdiFirewallGuard.ApplyBlockRule"/> de novo — a
    /// correção da v1.0.24 nunca rodou de verdade na máquina do usuário, mesmo já instalada ("tudo
    /// igual, funcionando pela placa do livewire"). Corrigido comparando TAMBÉM
    /// <see cref="NdiFirewallGuard.RuleSchemaVersion"/> contra
    /// <see cref="AppSettingsSnapshot.NdiFirewallGuardAppliedRuleSchemaVersion"/> — só pula a
    /// reaplicação quando os DOIS baterem; qualquer mudança futura no desenho das regras (e o
    /// incremento correspondente da constante) força uma reaplicação de verdade mesmo com o IP igual.
    ///
    /// Ambos os valores ficam PERSISTIDOS em disco (não só em memória, diferente de
    /// <see cref="_ndiNetworkAdapterApplied"/>) de propósito: aplicar a regra exige uma janela de
    /// permissão de Administrador (UAC), e sem essa memória persistida o programa pediria essa
    /// permissão TODA VEZ que abrisse, mesmo sem nada ter mudado. Ao ter sucesso, grava o snapshot
    /// imediatamente (não espera o próximo Salvar de Configurações), pra essa memória valer mesmo que
    /// o usuário feche o programa sem mexer em mais nada.
    ///
    /// **Fix pós-entrega das v1.0.24/25/26 (v1.0.27, mesmo dia) — causa raiz de verdade:**
    /// diagnóstico ao vivo na máquina do usuário achou que os perfis de Firewall Particular/Público
    /// estavam DESLIGADOS — nenhuma das quatro sprints anteriores (v1.0.23 a v1.0.26) tinha chance de
    /// funcionar, não importa o desenho da regra. <see cref="NdiFirewallGuard.ApplyBlockRule"/> agora
    /// também liga esses dois perfis e cria duas regras de PERMISSÃO geral pro processo (sem elas,
    /// ligar o Firewall sozinho quebraria o NDI em QUALQUER placa, não só na do Livewire — confirmado
    /// ao vivo). Nenhuma mudança de lógica NESTE método — o bump de
    /// <see cref="NdiFirewallGuard.RuleSchemaVersion"/> (2→3) já força a reaplicação sozinho, mesmo
    /// pra quem já tinha o IP certo persistido de uma versão anterior. Ver o comentário de classe de
    /// <see cref="NdiFirewallGuard"/> pro diagnóstico completo (timestamps, `Get-NetTCPConnection`,
    /// `netsh advfirewall show allprofiles state`).
    /// </summary>
    private void ReapplyNdiFirewallGuard()
    {
        var desiredLivewireIp = _persistedSettings.LivewireNetworkInterfaceIp;

        if (_persistedSettings.NdiFirewallGuardAppliedForLivewireIp == desiredLivewireIp
            && _persistedSettings.NdiFirewallGuardAppliedRuleSchemaVersion == NdiFirewallGuard.RuleSchemaVersion)
        {
            return;
        }

        var (ok, error) = NdiFirewallGuard.ApplyBlockRule(desiredLivewireIp);
        if (ok)
        {
            _persistedSettings.NdiFirewallGuardAppliedForLivewireIp = desiredLivewireIp;
            _persistedSettings.NdiFirewallGuardAppliedRuleSchemaVersion = NdiFirewallGuard.RuleSchemaVersion;
            _settingsStore.Save(_persistedSettings);
        }
        else
        {
            StatusMessage = $"Não foi possível reforçar por Firewall o bloqueio do NDI na placa do Livewire: {error}";
        }
    }

    /// <summary>
    /// Registra/reregistra os dois atalhos globais de teclado (Vinhetas e Trilhas) a partir do que
    /// estiver salvo em Config — Sprint Comando Externo (2026-08-10). Chamado na construção e sempre
    /// que a janela de Configurações fecha (mesmo gatilho de <see cref="ReloadCartridgeBanksFromSettings"/>).
    /// Sempre desregistra o atalho anterior antes de registrar o novo (idempotente — cobre ligar,
    /// desligar, e trocar de tecla, os três só chamando este método de novo com o snapshot atual).
    /// As duas ROTAS HTTP (Stream Deck/Companion) não precisam disso — são registradas uma vez só na
    /// construção (<see cref="TryTriggerExternally"/> já consulta o "habilitado" atual a cada
    /// chamada, não precisa recriar rota quando a configuração muda).
    /// </summary>
    private void ApplyExternalTriggerSettings()
    {
        var cartridges = _persistedSettings.Cartridges;

        _vinhetasHotkeyId = ReapplyHotkey(_vinhetasHotkeyId, cartridges.VinhetasExternalTriggerEnabled, cartridges.VinhetasKeyboardShortcut, VinhetasBank, "Vinhetas");
        _trilhasHotkeyId = ReapplyHotkey(_trilhasHotkeyId, cartridges.TrilhasExternalTriggerEnabled, cartridges.TrilhasKeyboardShortcut, TrilhasBank, "Trilhas");
    }

    /// <summary>Desregistra <paramref name="currentId"/> (se houver) e registra a tecla nova, se habilitado e escolhida — devolve o novo id (nulo se desabilitado/sem tecla/falhou), pra <see cref="ApplyExternalTriggerSettings"/> guardar de volta no campo correspondente.</summary>
    private int? ReapplyHotkey(int? currentId, bool enabled, string? shortcut, AudioBankViewModel bank, string bankLabel)
    {
        if (currentId is { } id)
        {
            _hotkeyService.Unregister(id);
        }

        if (!enabled || string.IsNullOrWhiteSpace(shortcut) || !Enum.TryParse<GlobalHotkeyKey>(shortcut, out var key))
        {
            return null;
        }

        var result = _hotkeyService.Register(key, () => Application.Current?.Dispatcher.Invoke(bank.TriggerExternalCommand));

        if (!result.Succeeded)
        {
            StatusMessage = $"Não foi possível registrar o atalho de teclado de {bankLabel} ({shortcut}): {result.ErrorMessage}";
            return null;
        }

        return result.Id;
    }

    /// <summary>
    /// Fix v1.0.56 (2026-11-11): registra o atalho global (Ctrl+Alt+Shift+F9) que fecha TODAS as
    /// <see cref="ScreenOutputWindow"/> dos dois Decks na hora — pedido direto do usuário depois de
    /// ficar travado de novo mesmo com a proteção da v1.0.54/v1.0.55 (ver DeckViewModel.
    /// CloseAllScreenOutputWindowsForSafety): aquela proteção evita abrir uma Tela em cima do
    /// monitor do operador, mas não cobre todo caso possível (ex.: máquina com um monitor só, onde
    /// "a tela principal" É o único monitor que existe). Diferente do antigo atalho Ctrl+Shift+Esc
    /// (removido — colidia com o atalho reservado do próprio Windows pro Gerenciador de Tarefas e
    /// por isso nunca disparava, confirmado pelo usuário testando no Windows real): este é um
    /// atalho GLOBAL de verdade via <see cref="GlobalHotkeyService"/>/<c>RegisterHotKey</c> —
    /// funciona mesmo sem nenhuma janela da Cartucheira em foco (diferente do antigo, que era um
    /// <c>PreviewKeyDown</c> amarrado a cada <see cref="ScreenOutputWindow"/> individualmente, e só
    /// fazia efeito se aquela janela específica estivesse em foco — no incidente real, a janela
    /// nem conseguia receber foco de verdade por estar coberta). Ctrl+Alt+Shift é uma combinação
    /// deliberadamente incomum, pra minimizar a chance de colidir com outro programa já rodando
    /// (vMix, Resolume, OBS etc.); se ainda assim colidir, o registro falha de forma honesta (ver
    /// <see cref="StatusMessage"/> abaixo) em vez de fingir que o atalho está ativo.
    ///
    /// Sempre ativo enquanto o programa roda — não é uma opção de Configurações, é uma rede de
    /// segurança incondicional. Nunca é desregistrado/reregistrado (diferente de
    /// <see cref="ReapplyHotkey"/>), então não precisa guardar o id — <c>_hotkeyService.Dispose()</c>
    /// (chamado em <see cref="Dispose"/>) já libera no fechamento do programa.
    /// </summary>
    private void RegisterEmergencyCloseScreensHotkey()
    {
        var result = _hotkeyService.Register(
            GlobalHotkeyKey.F9,
            () => Application.Current?.Dispatcher.Invoke(() =>
            {
                DeckA.CloseAllScreenOutputWindowsForSafety();
                DeckB.CloseAllScreenOutputWindowsForSafety();

                StatusMessage = "Emergência: todas as Telas foram fechadas agora (Ctrl+Alt+Shift+F9). Vá em Configurações e corrija a seleção de Telas antes de salvar de novo.";

                if (Application.Current?.MainWindow is { } mainWindow)
                {
                    mainWindow.Activate();
                    mainWindow.Topmost = true;
                    mainWindow.Topmost = false;
                }
            }),
            GlobalHotkeyModifiers.Control | GlobalHotkeyModifiers.Alt | GlobalHotkeyModifiers.Shift);

        if (!result.Succeeded)
        {
            StatusMessage = $"Atalho de emergência (Ctrl+Alt+Shift+F9) não pôde ser ativado: {result.ErrorMessage}. As outras proteções de Telas continuam valendo.";
        }
    }

    /// <summary>
    /// Reabre o <see cref="_udpTriggerListener"/> na porta atual de <see cref="_persistedSettings"/>
    /// se ela tiver mudado desde a última vez — consolidação da porta UDP (2026-08-13). Chamado
    /// sempre que a janela de Configurações fecha (mesmo gatilho de
    /// <see cref="ReloadCartridgeBanksFromSettings"/>/<see cref="ApplyExternalTriggerSettings"/>).
    /// Sem isto, a porta só valeria depois de reiniciar o programa — o socket já estaria aberto na
    /// porta antiga desde a construção (diferente do texto do comando, consultado a cada pacote,
    /// que já vale na hora do Salvar). Compara com <see cref="UdpTriggerListener.Port"/> antes de
    /// fazer qualquer coisa — se a porta não mudou (o operador só mexeu em texto/toggle), não reabre
    /// socket à toa.
    /// </summary>
    private void ReapplyUdpListenerPort()
    {
        var desiredPort = _persistedSettings.UdpTriggerPort;
        if (_udpTriggerListener.Port == desiredPort)
        {
            return;
        }

        _udpTriggerListener.Dispose();
        _udpTriggerListener = new UdpTriggerListener(desiredPort);
        var udpStart = _udpTriggerListener.Start(text => Application.Current?.Dispatcher.Invoke(() => TryTriggerExternalUdpCommand(text)));
        StatusMessage = udpStart.Succeeded
            ? $"Porta do comando externo (UDP) atualizada para 127.0.0.1:{udpStart.Port}."
            : $"Não foi possível abrir a nova porta UDP ({desiredPort}): {udpStart.ErrorMessage}";
    }

    /// <summary>
    /// Handler das duas rotas HTTP do <see cref="LocalTriggerApiHost"/> (registradas uma vez na
    /// construção) — chamado na thread do listener HTTP, nunca na UI, por isso marcha pra
    /// <c>Dispatcher.Invoke</c> antes de tocar em <see cref="AudioBankViewModel.TriggerExternalCommand"/>
    /// (que mexe em binding de WPF). Devolve falso (o host responde 403) quando o comando externo
    /// está desabilitado pra aquela Cartucheira em Config — nunca finge sucesso.
    /// </summary>
    private bool TryTriggerExternally(AudioBankViewModel bank, bool enabled)
    {
        if (!enabled)
        {
            return false;
        }

        Application.Current?.Dispatcher.Invoke(bank.TriggerExternalCommand);
        return true;
    }

    /// <summary>
    /// Handler de todo pacote recebido pelo <see cref="_udpTriggerListener"/> — UMA porta só,
    /// compartilhada por TUDO (as duas Cartucheiras E os dois Decks, consolidação 2026-08-13; antes
    /// eram dois listeners/handlers separados). O texto do pacote é quem decide o quê: tenta
    /// primeiro contra os quatro campos da Cartucheira (Vinhetas/Trilhas × Ligar/Desligar), depois
    /// contra os quatro campos do Deck (1/2 × Play/Pause) — só nos elementos com o "habilitado"
    /// ligado, comparação sem diferenciar maiúsculas/minúsculas nem espaço nas pontas (a mesa pode
    /// mandar "on"/"ON"/" ON " dependendo de como o operador digitou lá), campo vazio nunca casa com
    /// nada. Texto que não bate com nada registra na barra de status o texto recebido (nunca
    /// trava/loga erro) — ajuda o operador a descobrir o texto exato que a mesa manda e copiar pro
    /// campo certo em Configurações. Já chamado dentro de <c>Dispatcher.Invoke</c> por quem
    /// registrou o callback (<see cref="UdpTriggerListener.Start"/>, ver construtor) — seguro tocar
    /// em <see cref="AudioBankViewModel"/>/<see cref="DeckViewModel"/> aqui direto.
    /// </summary>
    private void TryTriggerExternalUdpCommand(string commandText)
    {
        var text = commandText.Trim();

        if (TryMatchCartridgeUdpCommand(text))
        {
            return;
        }

        if (TryMatchDeckUdpCommand(text))
        {
            return;
        }

        StatusMessage = $"Comando UDP recebido, não reconhecido: \"{text}\" — confira o texto configurado em Configurações → Cartucheira/Deck.";
    }

    /// <summary>Tenta casar <paramref name="text"/> contra o Ligar/Desligar de Vinhetas, depois Trilhas — devolve verdadeiro (e já disparou/parou) se bateu com algum dos quatro.</summary>
    private bool TryMatchCartridgeUdpCommand(string text)
    {
        var cartridges = _persistedSettings.Cartridges;

        return TryMatchCartridgeBankUdpCommand(text, cartridges.VinhetasExternalTriggerEnabled, cartridges.VinhetasUdpTriggerOnCommand, cartridges.VinhetasUdpTriggerOffCommand, VinhetasBank)
            || TryMatchCartridgeBankUdpCommand(text, cartridges.TrilhasExternalTriggerEnabled, cartridges.TrilhasUdpTriggerOnCommand, cartridges.TrilhasUdpTriggerOffCommand, TrilhasBank);
    }

    /// <summary>Compara <paramref name="text"/> contra o Ligar/Desligar de UM banco — devolve verdadeiro (e já disparou/parou) se bateu com algum dos dois, falso senão (deixa <see cref="TryMatchCartridgeUdpCommand"/> tentar o outro banco).</summary>
    private static bool TryMatchCartridgeBankUdpCommand(string text, bool enabled, string onCommand, string offCommand, AudioBankViewModel bank)
    {
        if (!enabled)
        {
            return false;
        }

        if (onCommand.Length > 0 && string.Equals(text, onCommand, StringComparison.OrdinalIgnoreCase))
        {
            bank.TriggerExternalCommand();
            return true;
        }

        if (offCommand.Length > 0 && string.Equals(text, offCommand, StringComparison.OrdinalIgnoreCase))
        {
            bank.StopExternalCommand();
            return true;
        }

        return false;
    }

    /// <summary>Tenta casar <paramref name="text"/> contra o Play/Pause do Deck 1, depois Deck 2 — devolve verdadeiro se bateu com algum dos quatro (ver <see cref="TryMatchDeckUdpCommand(string, DeckSettingsSnapshot, DeckViewModel)"/>).</summary>
    private bool TryMatchDeckUdpCommand(string text)
    {
        return TryMatchDeckUdpCommand(text, _persistedSettings.DeckA, DeckA)
            || TryMatchDeckUdpCommand(text, _persistedSettings.DeckB, DeckB);
    }

    /// <summary>
    /// Compara <paramref name="text"/> contra o Play/Pause de UM Deck — devolve verdadeiro (já
    /// reconhecido, com ou sem efeito real) se bateu com algum dos dois, falso senão (deixa
    /// <see cref="TryMatchDeckUdpCommand(string)"/> tentar o outro Deck). Diferente da Cartucheira
    /// (que chama <c>TriggerExternalCommand</c>/<c>StopExternalCommand</c> direto — métodos simples,
    /// sempre seguros de chamar), aqui o disparo passa pelos <c>ICommand</c> públicos do Deck
    /// (<see cref="DeckViewModel.PlayCommand"/>/<see cref="DeckViewModel.PauseCommand"/>) — e
    /// <c>RelayCommand.Execute</c> NÃO checa <c>CanExecute</c> sozinho (diferente de um botão do WPF,
    /// que só chama Execute depois de CanExecute confirmar), então checar explicitamente aqui é
    /// obrigatório pra nunca tentar dar Play num Deck sem vídeo carregado ou já tocando, nem Pause
    /// num Deck que não está tocando — nesses casos o comando é reconhecido (retorna verdadeiro,
    /// texto configurado bateu) mas simplesmente não tem efeito, sem gerar mensagem de erro nenhuma.
    /// </summary>
    private static bool TryMatchDeckUdpCommand(string text, DeckSettingsSnapshot settings, DeckViewModel deck)
    {
        if (!settings.ExternalTriggerEnabled)
        {
            return false;
        }

        if (settings.UdpTriggerPlayCommand.Length > 0 && string.Equals(text, settings.UdpTriggerPlayCommand, StringComparison.OrdinalIgnoreCase))
        {
            if (deck.PlayCommand.CanExecute(null))
            {
                deck.PlayCommand.Execute(null);
            }

            return true;
        }

        if (settings.UdpTriggerPauseCommand.Length > 0 && string.Equals(text, settings.UdpTriggerPauseCommand, StringComparison.OrdinalIgnoreCase))
        {
            if (deck.PauseCommand.CanExecute(null))
            {
                deck.PauseCommand.Execute(null);
            }

            return true;
        }

        return false;
    }

    /// <summary>
    /// Persiste (ou remove, se a customização voltou ao padrão — sem cor escolhida e sem loop) a
    /// cor/loop de UM cartucho na lista de overrides correta (Vinhetas ou Trilhas, decidido por qual
    /// callback o AudioBankViewModel chamou) — Sprint Cartucheira 2, parte 2 (2026-08-10). Salva
    /// imediatamente no disco (não espera o Salvar da janela de Configurações), mesmo padrão já
    /// usado pra senha de admin (ver <see cref="OpenSettings"/>): a mudança acontece direto na tela
    /// principal (Ctrl+clique direito no cartucho), não dentro de um diálogo com botão Salvar.
    /// </summary>
    private void PersistCartridgeOverride(List<CartridgeOverride> overrides, string filePath, string? colorOverride, bool loopEnabled)
    {
        var existing = overrides.FirstOrDefault(o => o.FilePath == filePath);

        if (colorOverride is null && !loopEnabled)
        {
            if (existing is not null)
            {
                overrides.Remove(existing);
                _settingsStore.Save(_persistedSettings);
            }

            return;
        }

        if (existing is not null)
        {
            existing.ColorOverride = colorOverride;
            existing.LoopEnabled = loopEnabled;
        }
        else
        {
            overrides.Add(new CartridgeOverride { FilePath = filePath, ColorOverride = colorOverride, LoopEnabled = loopEnabled });
        }

        _settingsStore.Save(_persistedSettings);
    }

    private void OnDeckError(DeckViewModel deck, string message) =>
        StatusMessage = $"Erro no {deck.Name}: {message}";

    private void OnPlaylistItemActivated(PlaylistItemViewModel item)
    {
        var target = PickTargetDeckForDoubleClick();
        if (target is null)
        {
            return;
        }

        LoadIntoDeck(target, item.FilePath);
    }

    private void OnExplorerFileActivated(FileSystemItemViewModel item)
    {
        var target = PickTargetDeckForDoubleClick();
        if (target is null)
        {
            return;
        }

        LoadIntoDeck(target, item.FullPath);
    }

    /// <summary>
    /// Algoritmo do duplo clique: deck vazio primeiro; senao, deck parado (Cue); senao, se os
    /// dois estiverem tocando, pergunta qual substituir.
    /// </summary>
    private DeckViewModel? PickTargetDeckForDoubleClick()
    {
        if (!DeckA.HasVideo)
        {
            return DeckA;
        }

        if (!DeckB.HasVideo)
        {
            return DeckB;
        }

        if (!DeckA.IsPlaying)
        {
            return DeckA;
        }

        if (!DeckB.IsPlaying)
        {
            return DeckB;
        }

        var result = MessageBox.Show(
            "Os dois Decks estão tocando. Substituir o DECK 1?\n\n(Sim = Deck 1, Não = Deck 2, Cancelar = não carregar)",
            "Cartucheira Vídeo",
            MessageBoxButton.YesNoCancel,
            MessageBoxImage.Question);

        return result switch
        {
            MessageBoxResult.Yes => DeckA,
            MessageBoxResult.No => DeckB,
            _ => null
        };
    }

    /// <summary>
    /// Libera os PlaybackEngine dos dois Decks e os AudioEngine dos dois bancos da Cartucheira (e
    /// portanto os MediaPlayer nativos do LibVLC) — precisa acontecer ANTES de descartar o LibVLC
    /// compartilhado (App.xaml.cs), senao a ordem de descarte fica invertida (MediaPlayer
    /// sobrevivendo ao LibVLC que o criou).
    /// </summary>
    public void Dispose()
    {
        DeckA.Dispose();
        DeckB.Dispose();
        VinhetasBank.Dispose();
        TrilhasBank.Dispose();

        // Sprint Comando Externo (2026-08-10): encerra o atalho global de teclado (desregistra as
        // duas teclas e derruba a thread/janela de mensagem dedicada), o servidor HTTP local e o
        // listener UDP único (Cartucheira + Deck, consolidação 2026-08-13, ou o que tiver sido
        // trocado por ReapplyUdpListenerPort) — todos criados no construtor, precisam ser
        // descartados aqui igual a tudo o mais.
        _hotkeyService.Dispose();
        _triggerApiHost.Dispose();
        _udpTriggerListener.Dispose();
        // Sprint Comando Externo Deck→Mesa (2026-08-13): quinto canal a encerrar — remetente UDP de
        // saída (avisos de Play/Fim dos Decks), mesmo padrão.
        _outboundUdpSender.Dispose();
        // Sprint Livewire-3 (2026-09-13): sexto canal a encerrar — anunciador Livewire (ADVT1/ADVT2).
        _livewireAdvertiser.Dispose();
    }
}
