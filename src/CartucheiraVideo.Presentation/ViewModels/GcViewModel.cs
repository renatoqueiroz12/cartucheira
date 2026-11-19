using System.Linq;
using System.Windows;
using CartucheiraVideo.Configuration;
using CartucheiraVideo.Infrastructure;

namespace CartucheiraVideo.Presentation.ViewModels;

/// <summary>
/// Orquestrador do módulo GC (Gerador de Caracteres) — Sprint GC (2026-11-17, pedido do usuário
/// depois de testar o Controle-Remoto-TP: "precisamos criar o modulo GC, o sistema cartucheira vai
/// enviar para o VMIX O GC automaticamente, assim que o tp ou vt for ativado, o texto GC que esta
/// na lauda é enviado, ai tem que ter um campo no setup do programa para colocar o GC padrao [...]
/// temos que ter um controle parecido ao tp [...] tenho o monitoramento do que esta no ar, parecido
/// com o tp"). Papel equivalente ao de <see cref="TeleprompterViewModel"/>, mesma disciplina de
/// "nunca lança, falha honesta no Logs" de <see cref="DeckViewModel.TriggerVMixOnPlay"/>.
///
/// Decisões confirmadas com o usuário antes de qualquer código (perguntas objetivas, Convenção
/// #10/#12/#13 — ver <c>GcSettingsSnapshot</c> pro resumo das 5 respostas):
///
/// 1. Laudas COM VT: dispara ao DAR PLAY (<see cref="NotifyDeckPlay"/>, chamado pelo
///    <c>MainViewModel.OnDeckPlay</c> — mesmo instante do corte pro vMix de
///    <see cref="DeckViewModel.TriggerVMixOnPlay"/>) e volta pro GC padrão ao Stop/fim natural
///    (<see cref="NotifyDeckEnded"/>, chamado de <c>OnDeckStop</c> E <c>OnDeckMediaEnded</c>, mesmo
///    critério de "terminou" de sempre neste projeto).
/// 2. Laudas SEM VT (Entrevista/Nota/Passagem): dispara quando o TELEPROMPTER mostra o texto dela —
///    <see cref="NotifyDeckLoaded"/>, chamado pelo <c>MainViewModel</c> nos MESMOS pontos que já
///    chamam <c>TeleprompterViewModel.NotifyDeckLoaded</c>, só tem efeito se o Deck for o
///    <see cref="GcSettingsSnapshot.SourceDeckSetting"/> configurado E a Lauda não tiver VT (Laudas
///    com VT são ignoradas aqui de propósito — ficam só por conta de <see cref="NotifyDeckPlay"/>,
///    senão o GC trocaria duas vezes: uma ao carregar, outra ao dar Play).
/// 3. Um único Título de vMix pro sistema todo — por isso <see cref="ApplySettings"/> recebe só UM
///    <see cref="GcSettingsSnapshot"/> (Host/Porta/Título/Canal de Overlay), nunca um por Deck/Programa.
/// 4. GC padrão por Programa (<see cref="SetDefaultFields"/>, chamado pelo <c>MainViewModel</c> ao
///    "abrir o dia" com <c>PlaylistProgramRecord.DefaultGcFields</c>) — fica no ar sempre que nenhuma
///    Lauda específica estiver mandando o seu próprio conjunto (<see cref="_activeAutoSourceDeck"/>
///    nulo). Vazio = esconde o Título do vMix em vez de mandar campos em branco (nunca inventa texto).
/// 5. Controle manual (<see cref="SetManualOverride"/>/<see cref="SetManualFields"/>, chamados pelo
///    <c>PlaylistDashboardCoordinator</c> a partir da dashboard web) — mesmo "disjuntor" de runtime
///    de <see cref="TeleprompterViewModel"/> (nunca persistido; liga/desliga o disparo automático).
///    Desligar o manual volta pro GC padrão (decisão deliberada, mais simples e honesta que tentar
///    "adivinhar" qual Lauda deveria estar ativa — o próximo Play/Carregamento natural corrige sozinho).
///
/// **Correção Sprint GC-Disparo-Explicito (2026-11-19), relatada pelo usuário testando de verdade com
/// o vMix configurado**: "Ele tem que ter algum comando, ou play no VT, ou disparar o TP, que aí
/// aciona o GC" — <see cref="SetDefaultFields"/> estava mandando o GC padrão (decisão #4 acima) pro ar
/// IMEDIATAMENTE ao abrir o dia/trocar de Programa, antes de qualquer Play ou TP (decisões #1/#2) — o
/// usuário via isso como "disparou sozinho, com o texto errado". Agora <see cref="SetDefaultFields"/>
/// SÓ guarda o padrão novo, nunca manda nada pro vMix sozinho; ele continua valendo como "pra onde
/// volta" quando a Lauda ativa termina, só não é mais empurrado pro ar no instante em que é definido.
/// Detalhe junto que piorava o sintoma: um dicionário com chaves mas todo mundo com valor vazio (ex.:
/// salvo pela textarea livre antiga "Nome: Valor" com nada depois dos dois-pontos) passava pela
/// checagem de "tem campo" (Count > 0) e mandava <c>SetText</c> com Value vazio pro vMix — que, pela
/// mesma falha de sempre da API HTTP do vMix (ela responde 200 OK mesmo quando a Function não muda
/// nada de verdade, já documentado em
/// <see cref="CartucheiraVideo.Infrastructure.VMixClient.CutToInputAsync"/>), não limpa o campo —
/// deixa o texto de FÁBRICA do template (o que o usuário via como "HEADLINE"/"DESCRIPTION", nunca
/// escrito por nós) no ar, com o Overlay ligado por cima. <see cref="SendFieldsAsync"/> agora trata
/// "só valores em branco" igual a "nenhum campo" (esconde em vez de mandar SetText vazio).
///
/// Como o Teleprompter, mas SEM nenhuma janela WPF própria — todo o "destino" é uma chamada de rede
/// pro vMix (<see cref="CartucheiraVideo.Infrastructure.VMixClient"/>), fire-and-forget, nunca trava
/// a UI thread. Mesmo cuidado de <c>Dispatcher.Invoke</c> nos métodos chamados pela dashboard web
/// (thread de requisição HTTP em segundo plano) já documentado em
/// <see cref="TeleprompterViewModel.SetManualOverride"/>.
/// </summary>
public sealed class GcViewModel
{
    private readonly DeckViewModel _deckA;
    private readonly DeckViewModel _deckB;
    private readonly Action<string> _onStatus;

    private GcSettingsSnapshot _settings = new();
    private bool _isManualOverride;
    private IReadOnlyDictionary<string, string> _currentFields = new Dictionary<string, string>();
    private IReadOnlyDictionary<string, string> _defaultFields = new Dictionary<string, string>();
    private bool _isShowingDefault = true;
    private bool _isOnAir;

    /// <summary>
    /// Qual Deck disparou automaticamente o <see cref="_currentFields"/> atual (<see cref="NotifyDeckLoaded"/>
    /// pra Laudas sem VT, <see cref="NotifyDeckPlay"/> pra Laudas com VT) — nulo quando é o GC PADRÃO
    /// no ar (ninguém disparou nada), não quando é manual (ver <see cref="_isManualOverride"/>, checado
    /// separadamente). Único propósito: <see cref="NotifyDeckEnded"/> só reverte pro padrão se for o
    /// MESMO Deck que colocou o GC atual no ar — evita que o Deck 1 terminando "roube" o GC do Deck 2,
    /// se o Deck 2 tiver assumido o ar depois (mesmo cuidado de <c>DeckViewModel._vMixInputBeforePlay</c>
    /// só restaurar o que ele mesmo capturou).
    /// </summary>
    private DeckViewModel? _activeAutoSourceDeck;

    /// <summary>Sprint GC (2026-11-17) — exposto pro <c>PlaylistDashboardCoordinator</c> relatar o estado atual (GET /api/gc/state).</summary>
    public bool IsManualOverride => _isManualOverride;

    /// <summary>Campos (nome→valor) exibidos agora — automático (Lauda ou GC padrão) ou manual, idem.</summary>
    public IReadOnlyDictionary<string, string> CurrentFields => _currentFields;

    /// <summary>Verdadeiro quando <see cref="CurrentFields"/> é o GC padrão do Programa (nenhuma Lauda específica no ar) — a dashboard usa isto só pra legenda ("Padrão" vs. "Lauda"), nunca muda o comportamento de envio.</summary>
    public bool IsShowingDefault => _isShowingDefault;

    /// <summary>Verdadeiro se o Título está (ou deveria estar, pela última resposta do vMix) no ar agora — falso quando <see cref="CurrentFields"/> está vazio (nada pra mostrar, ver <see cref="SendFieldsAsync"/>) ou o módulo nunca mandou nada ainda.</summary>
    public bool IsOnAir => _isOnAir;

    /// <summary>Sprint GC (2026-11-17) — dispara sempre que algo relevante muda de verdade, mesmo papel/mesmo consumidor de <see cref="TeleprompterViewModel.StateChanged"/> (<c>PlaylistDashboardCoordinator</c>, via Server-Sent Events). Pode disparar em qualquer thread — quem assina nunca deve tocar em WPF aqui dentro.</summary>
    public event Action? StateChanged;

    private void RaiseStateChanged() => StateChanged?.Invoke();

    public GcViewModel(DeckViewModel deckA, DeckViewModel deckB, Action<string> onStatus)
    {
        _deckA = deckA ?? throw new ArgumentNullException(nameof(deckA));
        _deckB = deckB ?? throw new ArgumentNullException(nameof(deckB));
        _onStatus = onStatus ?? throw new ArgumentNullException(nameof(onStatus));
    }

    /// <summary>Chamado pelo <c>MainViewModel</c> na inicialização e por <c>SettingsViewModel.Save</c> — só guarda a configuração mais recente (Host/Porta/Título/Canal/Deck fonte), nenhum efeito colateral imediato (diferente do Teleprompter, não existe janela pra abrir/fechar aqui).</summary>
    public void ApplySettings(GcSettingsSnapshot settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _settings = settings;
    }

    /// <summary>
    /// Chamado pelo <c>MainViewModel.OnOpenPlaylistRequested</c> ("abrir o dia") com
    /// <c>PlaylistProgramRecord.DefaultGcFields</c> do Programa escolhido — troca o GC PADRÃO.
    ///
    /// Sprint GC-Disparo-Explicito (2026-11-19, correção — ver comentário de classe): SÓ guarda o
    /// valor novo, nunca manda nada pro vMix neste instante — antes, abrir o dia colocava o Título no
    /// ar sozinho mesmo sem nenhum Play/TP ter acontecido ainda. O padrão continua sendo usado como
    /// "pra onde volta" quando a Lauda ativa termina (<see cref="NotifyDeckEnded"/>) ou o manual for
    /// desligado (<see cref="SetManualOverride"/>) — só não é mais empurrado pró-ativamente aqui.
    /// </summary>
    public void SetDefaultFields(IReadOnlyDictionary<string, string>? fields)
    {
        _defaultFields = fields is { Count: > 0 } ? fields : new Dictionary<string, string>();
    }

    /// <summary>
    /// Ver decisão #2 no comentário de classe — só tem efeito se <paramref name="deck"/> for o Deck
    /// fonte configurado E a Lauda NÃO tiver VT (<paramref name="vtFileName"/> vazio); Laudas com VT
    /// são ignoradas aqui de propósito, ficam só por conta de <see cref="NotifyDeckPlay"/>.
    /// </summary>
    public void NotifyDeckLoaded(DeckViewModel deck, string? vtFileName, IReadOnlyDictionary<string, string>? gcFields)
    {
        if (_isManualOverride || !ReferenceEquals(deck, ResolveSourceDeck()) || !string.IsNullOrWhiteSpace(vtFileName))
        {
            return;
        }

        if (gcFields is null || gcFields.Count == 0)
        {
            if (ReferenceEquals(_activeAutoSourceDeck, deck))
            {
                RevertToDefault();
            }

            return;
        }

        ActivateInternal(gcFields, deck, isDefault: false);
    }

    /// <summary>Ver decisão #1 no comentário de classe — chamado pelo <c>MainViewModel.OnDeckPlay</c>, sem restrição de "Deck fonte" (VT pode ir ao ar em qualquer um dos dois Decks).</summary>
    public void NotifyDeckPlay(DeckViewModel deck, IReadOnlyDictionary<string, string>? gcFields)
    {
        if (_isManualOverride || gcFields is null || gcFields.Count == 0)
        {
            return;
        }

        ActivateInternal(gcFields, deck, isDefault: false);
    }

    /// <summary>Ver decisão #1 no comentário de classe — chamado pelo <c>MainViewModel.OnDeckStop</c> E <c>OnDeckMediaEnded</c>. Só reverte pro GC padrão se este Deck for quem colocou o GC atual no ar (ver <see cref="_activeAutoSourceDeck"/>) — senão é no-op silencioso.</summary>
    public void NotifyDeckEnded(DeckViewModel deck)
    {
        if (_isManualOverride || !ReferenceEquals(_activeAutoSourceDeck, deck))
        {
            return;
        }

        RevertToDefault();
    }

    private void RevertToDefault() => ActivateInternal(_defaultFields, sourceDeck: null, isDefault: true);

    private void ActivateInternal(IReadOnlyDictionary<string, string> fields, DeckViewModel? sourceDeck, bool isDefault)
    {
        _activeAutoSourceDeck = sourceDeck;
        _currentFields = fields;
        _isShowingDefault = isDefault;
        SendFields(fields);
        RaiseStateChanged();
    }

    /// <summary>
    /// Sprint GC (2026-11-17) — liga/desliga o controle manual remoto (dashboard web). Mesmo
    /// <c>Dispatcher.Invoke</c> de <see cref="TeleprompterViewModel.SetManualOverride"/> e pelo mesmo
    /// motivo (chamado de uma thread de requisição HTTP em segundo plano; aqui não toca em WPF
    /// nenhum, mas <see cref="RaiseStateChanged"/>/<c>_onStatus</c> ainda precisam de uma thread
    /// previsível). Desligar o manual volta pro GC padrão — ver decisão #5 no comentário de classe.
    /// </summary>
    public void SetManualOverride(bool enabled)
    {
        Application.Current?.Dispatcher.Invoke(() =>
        {
            if (_isManualOverride == enabled)
            {
                return;
            }

            _isManualOverride = enabled;
            _onStatus(enabled
                ? "GC: controle manual ligado (remoto) — disparo automático por TP/VT desligado até desligar o manual."
                : "GC: controle manual desligado — voltou pro GC padrão; o próximo TP/VT ativado assume normalmente.");

            if (!enabled)
            {
                _activeAutoSourceDeck = null;
                RevertToDefault();
            }

            RaiseStateChanged();
        });
    }

    /// <summary>Manda um GC manual agora — só tem efeito com o manual ligado (ver <see cref="SetManualOverride"/>); fora dele, devolve silenciosamente sem mudar nada (mesmo critério de <see cref="TeleprompterViewModel.SetManualText"/>, quem chama decide se avisa o usuário, ver <c>PlaylistDashboardCoordinator.HandleSetGcManualFields</c>).</summary>
    public void SetManualFields(IReadOnlyDictionary<string, string> fields)
    {
        Application.Current?.Dispatcher.Invoke(() =>
        {
            if (!_isManualOverride)
            {
                return;
            }

            _currentFields = fields;
            _isShowingDefault = false;
            SendFields(fields);
            RaiseStateChanged();
        });
    }

    /// <summary>Esconde o Título do vMix por completo (campo vazio) — o botão "Tirar do ar" do controle manual remoto, pra quando o produtor quer limpar o GC sem mandar nenhum texto no lugar.</summary>
    public void ClearManualFields() => SetManualFields(new Dictionary<string, string>());

    /// <summary>Falha honesta, mesmo padrão de <see cref="DeckViewModel.TriggerVMixOnPlay"/>: Host/Título vazios ou módulo desligado em Configurações → GC não mandam nada pro vMix, só atualizam o estado em memória (a dashboard continua mostrando o que "deveria" estar no ar).</summary>
    private void SendFields(IReadOnlyDictionary<string, string> fields)
    {
        if (!_settings.Enabled)
        {
            return;
        }

        var host = _settings.VMixHost?.Trim();
        var input = _settings.TitleInput?.Trim();
        if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(input))
        {
            _onStatus("GC: \"Habilitar\" está marcado mas Host/Título não foram preenchidos em Configurações → GC — nenhum comando foi enviado.");
            return;
        }

        var port = string.IsNullOrWhiteSpace(_settings.VMixPort) ? "8088" : _settings.VMixPort;
        _ = SendFieldsAsync(host, port, input, _settings.OverlayChannel, fields);
    }

    private async Task SendFieldsAsync(string host, string port, string input, int overlayChannel, IReadOnlyDictionary<string, string> fields)
    {
        // Sprint GC-Disparo-Explicito (2026-11-19) — ver comentário de classe: um dicionário com
        // chaves mas TODO MUNDO com valor vazio (ex.: "Headline Text": "") não conta como "tem o que
        // mostrar" — sem isto, mandávamos SetText com Value vazio pro vMix (que, pela mesma falha já
        // documentada de a API HTTP responder 200 OK mesmo quando a Function não muda nada de
        // verdade, deixa o texto de FÁBRICA do template no ar) e ainda assim ligávamos o Overlay por
        // cima, mostrando texto nunca escrito por nós.
        var hasRealContent = fields.Values.Any(value => !string.IsNullOrWhiteSpace(value));
        if (fields.Count == 0 || !hasRealContent)
        {
            var (hideOk, hideError) = await CartucheiraVideo.Infrastructure.VMixClient.SetOverlayVisibleAsync(host, port, overlayChannel, input, visible: false).ConfigureAwait(false);
            if (hideOk)
            {
                _isOnAir = false;
            }

            RunOnUiThread(() => _onStatus(hideOk
                ? "GC: nenhum campo pra mostrar agora — Título escondido do ar."
                : $"GC: falha ao esconder o Título do vMix: {hideError}."));
            return;
        }

        foreach (var pair in fields)
        {
            var (ok, error) = await CartucheiraVideo.Infrastructure.VMixClient.SetTitleTextAsync(host, port, input, pair.Key, pair.Value).ConfigureAwait(false);
            if (!ok)
            {
                RunOnUiThread(() => _onStatus($"GC: falha ao mandar o campo \"{pair.Key}\" pro vMix: {error}."));
            }
        }

        var (showOk, showError) = await CartucheiraVideo.Infrastructure.VMixClient.SetOverlayVisibleAsync(host, port, overlayChannel, input, visible: true).ConfigureAwait(false);
        if (showOk)
        {
            _isOnAir = true;
        }

        RunOnUiThread(() => _onStatus(showOk
            ? $"GC: {fields.Count} campo(s) enviado(s) pro vMix."
            : $"GC: campos enviados, mas falha ao colocar o Título no ar: {showError}."));
    }

    private void RunOnUiThread(Action action) => Application.Current?.Dispatcher.BeginInvoke(action);

    private DeckViewModel ResolveSourceDeck() => _settings.SourceDeckSetting == TeleprompterSourceDeck.DeckA ? _deckA : _deckB;
}
