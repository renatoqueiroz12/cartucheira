using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Threading;
using CartucheiraVideo.Api;
using CartucheiraVideo.Configuration;
using CartucheiraVideo.Core;
using CartucheiraVideo.Infrastructure;
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

        DeckA = new DeckViewModel("DECK 1", libVlc, OnDeckPlay, OnDeckPause, OnDeckStop, OnDeckMediaEnded, OnDeckError, SetStatus);
        DeckB = new DeckViewModel("DECK 2", libVlc, OnDeckPlay, OnDeckPause, OnDeckStop, OnDeckMediaEnded, OnDeckError, SetStatus);
        Decks = new[] { DeckA, DeckB };

        // Sprint Config Geral, parte 1 (2026-08-09): restaura a configuração salva (se houver)
        // ANTES de qualquer outra coisa tocar nos Decks — primeira vez que algo sobrevive a fechar
        // e abrir o programa de novo.
        _settingsStore = new SettingsStore();
        _persistedSettings = _settingsStore.Load();
        SettingsMapper.ApplyToDeck(_persistedSettings.DeckA, DeckA);
        SettingsMapper.ApplyToDeck(_persistedSettings.DeckB, DeckB);

        // Sprint Cartucheira (2026-08-09): dois bancos fixos (Vinhetas/Trilhas), cada um com seu
        // próprio Engine.AudioEngine independente (NAudio desde a Sprint Crossfade v2, 2026-08-11 —
        // ver AudioEngine.cs) — populados a partir da pasta/dispositivo persistidos
        // (ReloadCartridgeBanksFromSettings), não mais com dados
        // fictícios (CreateSampleBanks/CreateSampleButtons1/2, removidos nesta sprint). Sprint
        // Cartucheira 2, parte 2 (2026-08-10): cada banco também recebe um callback pra persistir
        // cor/loop por cartucho (PersistCartridgeOverride) — cada um salva na sua própria lista
        // (Cartridges.VinhetasOverrides/TrilhasOverrides).
        Banks = new ObservableCollection<AudioBankViewModel>
        {
            new("Vinhetas", SetStatus, (path, color, loop) => PersistCartridgeOverride(_persistedSettings.Cartridges.VinhetasOverrides, path, color, loop)),
            new("Trilhas", SetStatus, (path, color, loop) => PersistCartridgeOverride(_persistedSettings.Cartridges.TrilhasOverrides, path, color, loop))
        };
        ReloadCartridgeBanksFromSettings();

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

    public string Title => "Cartucheira Vídeo";

    public string VersionLabel => "v1.0.0";

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
    /// Sprint Config Geral, parte 1 (2026-08-09): abre a janela única de Configurações — protegida
    /// por senha de admin (arquitetura: "Login e senha de admin... para acessar o config"). Fluxo:
    /// (1) se a janela já estiver aberta, só traz ela pra frente (evita duplicata); (2) se ainda
    /// não existe senha salva (primeiro acesso), pede pra DEFINIR uma — sucesso já persiste o
    /// hash/salt novos imediatamente, não espera o Salvar do Settings; (3) senão, pede LOGIN com a
    /// senha existente; (4) só então abre a janela (não-modal — o operador continua usando os Decks
    /// com ela aberta).
    /// </summary>
    private void OpenSettings()
    {
        if (_settingsWindow is not null)
        {
            _settingsWindow.Activate();
            return;
        }

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

        var settingsViewModel = new SettingsViewModel(DeckA, DeckB, _settingsStore, _persistedSettings, SetStatus);
        _settingsWindow = new SettingsWindow(settingsViewModel) { Owner = Application.Current?.MainWindow };
        _settingsWindow.Closed += (_, _) =>
        {
            _settingsWindow = null;

            // Sprint Cartucheira (2026-08-09): recarrega os dois bancos com o que tiver sido salvo
            // (dispositivo/pasta podem ter mudado) — barato o suficiente pra rodar sempre que a
            // janela fecha, em vez de um callback dedicado só pra "a seção Cartucheira foi salva".
            ReloadCartridgeBanksFromSettings();

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
    }
}
