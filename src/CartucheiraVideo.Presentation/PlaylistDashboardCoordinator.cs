using System.IO;
using System.Text.Json;
using CartucheiraVideo.Api.PlaylistDashboard;
using CartucheiraVideo.Configuration;
using CartucheiraVideo.Infrastructure.Playlist;

namespace CartucheiraVideo.Presentation;

/// <summary>
/// Liga o <see cref="PlaylistDashboardHost"/> (Api, fala só HTTP puro) ao <see cref="PlaylistFileStore"/>
/// (Infrastructure, fala só disco/strings cruas) — Sprint Playlist-Dashboard (2026-11-14, ver
/// arquitetura/playlist-modulo-roteiro.md). Mesmo papel que o <c>MainViewModel</c> já faz pra
/// <c>LocalTriggerApiHost</c>/<c>NdiFirewallGuard</c>: a Presentation é a única camada que conhece os
/// 7 projetos ao mesmo tempo, então é ela quem traduz entre o vocabulário de cada um.
///
/// Sprint 1 ainda não envolve nenhum objeto de <c>Core</c> (<see cref="Core.PlaylistProgram"/> etc.)
/// — os DTOs de JSON abaixo (<c>ProgramDto</c>/<c>EditionDto</c>) bastam pra esta primeira fatia
/// (criar Programa, criar Data, listar os dois); a tradução pra Core entra quando alguma tela WPF da
/// própria Cartucheira precisar ler a Playlist (Sprint 4, integração com o modo Auto).
///
/// **Login/permissão — Sprint 5 (2026-11-16)**: quem verifica usuário/senha
/// (<see cref="HandleLogin"/>, via <c>Infrastructure.Playlist.UserStore.VerifyCredentials</c>) e
/// filtra o que cada usuário pode ver/fazer (<see cref="HasAccessToProgram"/>) é esta classe — o
/// <c>PlaylistDashboardHost</c> só resolve SE existe uma sessão válida, nunca decide permissão
/// nenhuma (ver comentário de classe dele). Todo handler ganhou um parâmetro
/// <c>AuthenticatedDashboardUser user</c> novo, mesmo quando não usa (rota só precisa estar
/// logada, sem checagem extra) — é o Host quem garante que só chega aqui com sessão válida.
///
/// Nunca lança: qualquer exceção ao processar uma rota vira resposta HTTP 500 com a mensagem, sem
/// derrubar o host nem o programa — mesma defesa de sempre neste projeto.
///
/// **Gestão de Usuários pela própria web — Sprint 6 (2026-11-16, pedido do usuário "deixar mais
/// profissional, com menus")**: até a Sprint 5, só a Cartucheira desktop (Configurações → Playlist)
/// criava/editava/removia usuários da dashboard — a web só fazia login. A partir desta sprint, a
/// própria dashboard ganhou um menu lateral com uma tela "Usuários" (só visível pro Admin,
/// <see cref="HandleListUsers"/>/<see cref="HandleCreateUser"/>/
/// <see cref="HandleUpdateUserPermissions"/>/<see cref="HandleResetUserPassword"/>/
/// <see cref="HandleDeleteUser"/>), que chama o MESMO <c>UserStore</c> de sempre — as duas portas de
/// entrada (desktop e web) continuam escrevendo no mesmo <c>users.json</c>, nenhuma duplica lógica de
/// validação/proteção contra autoexclusão do último Admin (tudo isso já vive dentro do
/// <c>UserStore</c>, chamado dos dois lados).
///
/// **Controle Remoto do Teleprompter — Sprint Controle-Remoto-TP (2026-11-17, pedido do usuário
/// "controle manual do tp [...] isso precisa ser feito de outro pc, nao onde esta a cartucheira,
/// para issso um outro software ou url que va controlando")**: três rotas novas
/// (<see cref="HandleGetTeleprompterState"/>/<see cref="HandleSetTeleprompterManual"/>/
/// <see cref="HandleSetTeleprompterText"/>) ligam a dashboard web direto no
/// <see cref="_teleprompter"/> (referência direta — os dois vivem na Presentation, sem violar o
/// isolamento de camadas do resto do projeto; não precisa de callback <c>Func</c>/<c>Action</c>
/// como <see cref="_getBackgroundArtPresets"/> porque a instância é fixa durante toda a vida do
/// <c>MainViewModel</c>, nunca troca). Deliberadamente SEM checagem de <c>IsAdmin</c>/
/// <see cref="HasAccessToProgram"/>: o Teleprompter não pertence a nenhum Programa específico (é
/// uma Cartucheira/Teleprompter só por estúdio), então qualquer usuário autenticado na dashboard
/// pode ligar/desligar o manual e mandar texto — mesmo critério já usado por
/// <see cref="HandleListBackgroundArtPresets"/> (rota sem checagem extra, só sessão válida). Essa
/// escolha não foi perguntada explicitamente ao usuário; se ele preferir deixar só pra Admin, é
/// só adicionar a mesma checagem de <see cref="HandleListUsers"/> aqui.
///
/// **Parte 2 — Server-Sent Events (2026-11-17, pedido do usuário "2 segundos e muito, precisamos
/// pensar outra alternativa" ao polling de 2s da v1.0.95, confirmando que na própria Cartucheira o
/// texto vai pra tela "praticamente em tempo real")**: <see cref="_teleprompterSseChannel"/>/
/// <see cref="OnTeleprompterStateChanged"/> substituem esse polling por um push de verdade — a
/// dashboard abre uma conexão (<c>GET /api/teleprompter/stream</c>) e o servidor manda um evento
/// na hora que <see cref="ViewModels.TeleprompterViewModel.StateChanged"/> dispara, zero intervalo
/// de espera artificial. As três rotas REST da parte 1 continuam todas existindo sem mudança
/// nenhuma (ações continuam sendo pedidas por POST normal; SSE é só pro sentido contrário,
/// servidor→navegador).
/// </summary>
public sealed class PlaylistDashboardCoordinator : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private readonly Action<string> _onStatus;
    private readonly Func<IReadOnlyList<BackgroundArtPreset>> _getBackgroundArtPresets;
    private readonly ViewModels.TeleprompterViewModel _teleprompter;
    private PlaylistDashboardHost? _host;

    /// <summary>
    /// Sprint Controle-Remoto-TP, parte 2 (2026-11-17) — canal de Server-Sent Events da rota
    /// <c>GET /api/teleprompter/stream</c>, recriado em todo <see cref="ApplySettings"/> junto com o
    /// resto do <see cref="_host"/> (ver <see cref="OnTeleprompterStateChanged"/>, assinado UMA
    /// única vez no construtor em <see cref="_teleprompter"/>.<c>StateChanged</c> — sempre lê este
    /// campo no momento do disparo, nunca guarda uma referência velha). Nulo sempre que não houver
    /// dashboard rodando (pasta raiz vazia, porta ocupada etc.) — <see cref="OnTeleprompterStateChanged"/>
    /// usa <c>?.</c> de propósito, broadcast sem ninguém escutando não é erro.
    /// </summary>
    private PlaylistSseChannel? _teleprompterSseChannel;

    /// <summary>
    /// <paramref name="getBackgroundArtPresets"/> — Sprint 3 (2026-11-16, pedido do usuário "cada
    /// programa vai ter o seu fundo"): lido de novo a cada requisição (nunca cacheado), mesmo
    /// princípio já usado pro resto das Configurações — reflete qualquer preset criado/removido em
    /// Configurações → Arte de Fundo sem precisar reiniciar a dashboard. Esta camada não guarda o
    /// Id ativo nenhum, só expõe a LISTA pra dashboard poder montar o &lt;select&gt; do formulário
    /// "Novo Programa"; quem decide qual fundo fica ativo ao abrir a Playlist é o
    /// <c>MainViewModel</c> (ver <c>PlaylistViewModel</c>/Sprint 4), não esta classe.
    ///
    /// <paramref name="teleprompter"/> — Sprint Controle-Remoto-TP (2026-11-17): referência DIRETA
    /// (não um callback, diferente de <paramref name="getBackgroundArtPresets"/>) porque a
    /// instância é construída uma única vez pelo <c>MainViewModel</c> e vive durante todo o
    /// programa, mesmo princípio de <c>DeckA</c>/<c>DeckB</c> serem passados direto pra outros
    /// construtores desta base. Ver comentário de classe completo sobre as três rotas novas.
    /// </summary>
    public PlaylistDashboardCoordinator(Action<string> onStatus, Func<IReadOnlyList<BackgroundArtPreset>> getBackgroundArtPresets, ViewModels.TeleprompterViewModel teleprompter)
    {
        _onStatus = onStatus ?? throw new ArgumentNullException(nameof(onStatus));
        _getBackgroundArtPresets = getBackgroundArtPresets ?? throw new ArgumentNullException(nameof(getBackgroundArtPresets));
        _teleprompter = teleprompter ?? throw new ArgumentNullException(nameof(teleprompter));

        // Sprint Controle-Remoto-TP, parte 2 (2026-11-17): assinado uma única vez aqui (a instância
        // de _teleprompter nunca troca durante a vida do programa — ver comentário do parâmetro
        // acima) em vez de a cada ApplySettings, que só recria _host/_teleprompterSseChannel.
        _teleprompter.StateChanged += OnTeleprompterStateChanged;
    }

    /// <summary>
    /// Sobe (ou resobe, se já estava rodando numa porta diferente) o servidor da dashboard com a
    /// configuração atual. Chamado uma vez na construção do <c>MainViewModel</c> e de novo sempre que
    /// Configurações → Playlist é salvo com pasta raiz/porta diferentes. Nunca lança — falha vira só
    /// uma linha no Logs, a Cartucheira continua funcionando normalmente sem a dashboard.
    /// </summary>
    public void ApplySettings(PlaylistSettingsSnapshot settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        _host?.Dispose();
        _host = null;
        _teleprompterSseChannel = null; // Dispose() do _host acima já fechou as conexões deste canal.

        if (string.IsNullOrWhiteSpace(settings.RootFolder))
        {
            _onStatus("Playlist: pasta raiz não configurada — dashboard desligada (configure em Configurações → Playlist).");
            return;
        }

        var rootFolder = settings.RootFolder;
        var webRoot = Path.Combine(AppContext.BaseDirectory, "PlaylistDashboardWebRoot");
        var host = new PlaylistDashboardHost(settings.DashboardPort, webRoot);

        host.RegisterLoginRoute("POST", "/api/auth/login", body => HandleLogin(rootFolder, body));
        host.RegisterLogoutRoute("POST", "/api/auth/logout");
        host.RegisterApiRoute("GET", "/api/auth/me", (_, _, user) => HandleMe(user));

        // Sprint 6 (2026-11-16, pedido do usuário "deixar mais profissional, com menus"): gestão de
        // Usuários agora também pela própria dashboard web (menu "Usuários"), não só pela Cartucheira
        // desktop — mesmo UserStore, mesmas regras (só Admin chama, 403 pra quem não é, mesmo se a
        // chamada for forçada sem passar pela UI).
        host.RegisterApiRoute("GET", "/api/users", (_, _, user) => HandleListUsers(rootFolder, user));
        host.RegisterApiRoute("POST", "/api/users", (_, body, user) => HandleCreateUser(rootFolder, body, user));
        host.RegisterApiRoute("PUT", "/api/users", (_, body, user) => HandleUpdateUserPermissions(rootFolder, body, user));
        host.RegisterApiRoute("PUT", "/api/users/password", (_, body, user) => HandleResetUserPassword(rootFolder, body, user));
        host.RegisterApiRoute("DELETE", "/api/users", (query, _, user) => HandleDeleteUser(rootFolder, query, user));

        host.RegisterApiRoute("GET", "/api/playlist/programs", (_, _, user) => HandleListPrograms(rootFolder, user));
        host.RegisterApiRoute("POST", "/api/playlist/programs", (_, body, user) => HandleCreateProgram(rootFolder, body, user));
        host.RegisterApiRoute("GET", "/api/playlist/editions", (query, _, user) => HandleListEditions(rootFolder, query, user));
        host.RegisterApiRoute("POST", "/api/playlist/editions", (_, body, user) => HandleCreateEdition(rootFolder, body, user));
        host.RegisterApiRoute("GET", "/api/playlist/laudas", (query, _, user) => HandleListLaudas(rootFolder, query, user));
        host.RegisterApiRoute("POST", "/api/playlist/laudas", (_, body, user) => HandleCreateLauda(rootFolder, body, user));
        host.RegisterApiRoute("PUT", "/api/playlist/laudas", (_, body, user) => HandleUpdateLauda(rootFolder, body, user));
        host.RegisterApiRoute("PUT", "/api/playlist/laudas/reorder", (_, body, user) => HandleReorderLaudas(rootFolder, body, user));
        host.RegisterApiRoute("GET", "/api/playlist/background-art-presets", (_, _, _) => HandleListBackgroundArtPresets());
        host.RegisterUploadRoute("PUT", "/api/playlist/laudas/vt", (query, stream, user) => HandleUploadFile(rootFolder, query, stream, PlaylistUploadKind.Video, user));
        host.RegisterUploadRoute("PUT", "/api/playlist/laudas/imagem", (query, stream, user) => HandleUploadFile(rootFolder, query, stream, PlaylistUploadKind.Image, user));

        // Sprint Controle-Remoto-TP (2026-11-17): sem checagem de IsAdmin/HasAccessToProgram de
        // propósito — ver comentário de classe completo sobre essa escolha.
        host.RegisterApiRoute("GET", "/api/teleprompter/state", (_, _, _) => HandleGetTeleprompterState());
        host.RegisterApiRoute("POST", "/api/teleprompter/manual", (_, body, _) => HandleSetTeleprompterManual(body));
        host.RegisterApiRoute("POST", "/api/teleprompter/text", (_, body, _) => HandleSetTeleprompterText(body));

        // Sprint Controle-Remoto-TP, parte 3 (2026-11-17, pedido do usuário "essa tela que abriu no
        // browser precisa ser um espelho das tela do tp [...] e ai ter os controles de tp") — cinco
        // rotas novas pro espelho de rolagem (texto+posição, ver pesquisa citada no comentário de
        // classe) e os controles Play/Pause+velocidade/passo/início. Mesmo critério de acesso das
        // três rotas da parte 1 (sem IsAdmin/HasAccessToProgram): o Teleprompter é por estúdio, não
        // por Programa.
        host.RegisterApiRoute("POST", "/api/teleprompter/scroll-step", (_, body, _) => HandleTeleprompterScrollStep(body));
        host.RegisterApiRoute("POST", "/api/teleprompter/scroll-to", (_, body, _) => HandleTeleprompterScrollTo(body));
        host.RegisterApiRoute("POST", "/api/teleprompter/jump-start", (_, _, _) => HandleTeleprompterJumpStart());
        host.RegisterApiRoute("POST", "/api/teleprompter/autoscroll/play", (_, body, _) => HandleTeleprompterAutoScrollPlay(body));
        host.RegisterApiRoute("POST", "/api/teleprompter/autoscroll/speed", (_, body, _) => HandleTeleprompterAutoScrollSpeed(body));

        // Sprint Controle-Remoto-TP, parte 2 (2026-11-17): canal de push em vez da dashboard ter que
        // perguntar de novo e de novo (ver comentário de classe completo sobre o pedido do usuário
        // que motivou isto). GET /api/teleprompter/state acima continua existindo — é o que a
        // dashboard usa pra pegar o estado inicial ao abrir a view, antes do EventSource conectar.
        var teleprompterSseChannel = host.RegisterSseRoute("GET", "/api/teleprompter/stream");

        var result = host.Start();
        if (result.Succeeded)
        {
            _host = host;
            _teleprompterSseChannel = teleprompterSseChannel;
            _onStatus($"Playlist: dashboard disponível na rede local na porta {result.Port} (pasta raiz: {rootFolder}).");
        }
        else
        {
            host.Dispose();
            _teleprompterSseChannel = null;
            _onStatus($"Playlist: não consegui abrir a dashboard — {result.ErrorMessage}");
        }
    }

    /// <summary>
    /// Sprint Controle-Remoto-TP, parte 2 (2026-11-17) — assinado em <see cref="_teleprompter"/>.
    /// <c>StateChanged</c> (ver construtor). Nunca toca em nada de WPF (o evento pode disparar numa
    /// thread de requisição HTTP OU na UI thread, dependendo de quem mudou o estado — ver comentário
    /// de <c>TeleprompterViewModel.StateChanged</c>) — só serializa dois valores primitivos e escreve
    /// bytes num socket, seguro de qualquer thread.
    /// </summary>
    private void OnTeleprompterStateChanged()
    {
        _teleprompterSseChannel?.Broadcast("state", JsonSerializer.Serialize(ToTeleprompterStateDto(), JsonOptions));
    }

    /// <summary>Sprint 5 (2026-11-16) — POST /api/auth/login, única rota pública além do logout. Mensagem genérica de erro de propósito (ver <c>UserStore.VerifyCredentials</c>).</summary>
    private static PlaylistLoginAttemptResult HandleLogin(string rootFolder, string? body)
    {
        LoginRequest? request;
        try
        {
            request = string.IsNullOrWhiteSpace(body) ? null : JsonSerializer.Deserialize<LoginRequest>(body, JsonOptions);
        }
        catch (JsonException)
        {
            return PlaylistLoginAttemptResult.Failure(PlaylistDashboardResponse.PlainTextError(400, "corpo da requisição inválido (esperado JSON com \"username\" e \"password\")."));
        }

        if (request is null || string.IsNullOrWhiteSpace(request.Username) || string.IsNullOrWhiteSpace(request.Password))
        {
            return PlaylistLoginAttemptResult.Failure(PlaylistDashboardResponse.PlainTextError(400, "usuário e senha são obrigatórios."));
        }

        var (ok, error, user) = UserStore.VerifyCredentials(rootFolder, request.Username, request.Password);
        if (!ok || user is null)
        {
            return PlaylistLoginAttemptResult.Failure(PlaylistDashboardResponse.PlainTextError(401, error ?? "usuário ou senha incorretos."));
        }

        var authenticated = new AuthenticatedDashboardUser(user.Id, user.Username, user.IsAdmin, user.AllowedProgramIds);
        var response = PlaylistDashboardResponse.Json(JsonSerializer.Serialize(ToAuthUserDto(authenticated), JsonOptions));
        return PlaylistLoginAttemptResult.Success(response, authenticated);
    }

    /// <summary>Sprint 5 (2026-11-16) — GET /api/auth/me: a própria dashboard (app.js) usa isto pra saber se já está logada (401 = mostra tela de login) e, se logada, se é Admin (mostra/esconde "Criar Programa").</summary>
    private static PlaylistDashboardResponse HandleMe(AuthenticatedDashboardUser user) =>
        PlaylistDashboardResponse.Json(JsonSerializer.Serialize(ToAuthUserDto(user), JsonOptions));

    private static AuthUserDto ToAuthUserDto(AuthenticatedDashboardUser user) => new(user.Id, user.Username, user.IsAdmin, user.AllowedProgramIds);

    /// <summary>Sprint 5 (2026-11-16) — Admin vê todos; usuário comum só os Programas liberados pra ele (ver <c>UserStore.CreateUser</c>/<c>UpdateUserPermissions</c>, gerenciados em Configurações → Playlist → Usuários da Dashboard).</summary>
    private static PlaylistDashboardResponse HandleListPrograms(string rootFolder, AuthenticatedDashboardUser user)
    {
        var (ok, error, programs) = PlaylistFileStore.ListPrograms(rootFolder);
        if (!ok)
        {
            return PlaylistDashboardResponse.PlainTextError(500, error ?? "erro desconhecido.");
        }

        var visible = user.IsAdmin ? programs : programs.Where(p => user.AllowedProgramIds.Contains(p.Id, StringComparer.OrdinalIgnoreCase));
        var dtos = visible.Select(p => new ProgramDto(p.Id, p.Name)).ToList();
        return PlaylistDashboardResponse.Json(JsonSerializer.Serialize(dtos, JsonOptions));
    }

    /// <summary>Sprint 5 (2026-11-16, decisão confirmada com o usuário): só Admin cria Programa novo — usuário comum só cria Data/Lauda DENTRO de Programas que já tem acesso.</summary>
    private static PlaylistDashboardResponse HandleCreateProgram(string rootFolder, string? body, AuthenticatedDashboardUser user)
    {
        if (!user.IsAdmin)
        {
            return PlaylistDashboardResponse.PlainTextError(403, "só Administradores podem criar Programas novos — peça pra um Admin criar e liberar o acesso pra você.");
        }

        CreateProgramRequest? request;
        try
        {
            request = string.IsNullOrWhiteSpace(body) ? null : JsonSerializer.Deserialize<CreateProgramRequest>(body, JsonOptions);
        }
        catch (JsonException)
        {
            return PlaylistDashboardResponse.PlainTextError(400, "corpo da requisição inválido (esperado JSON com \"name\").");
        }

        if (request is null || string.IsNullOrWhiteSpace(request.Name))
        {
            return PlaylistDashboardResponse.PlainTextError(400, "nome do Programa não pode ser vazio.");
        }

        var (ok, error, program) = PlaylistFileStore.CreateProgram(rootFolder, request.Name, request.DefaultBackgroundArtId);
        if (!ok || program is null)
        {
            return PlaylistDashboardResponse.PlainTextError(500, error ?? "erro desconhecido.");
        }

        return PlaylistDashboardResponse.Json(JsonSerializer.Serialize(new ProgramDto(program.Id, program.Name), JsonOptions), 201);
    }

    /// <summary>Sprint 5 (2026-11-16) — Admin sempre tem acesso; usuário comum só se o Id do Programa estiver na lista liberada pra ele. Usado por toda rota que recebe um <c>programId</c> (Edições/Laudas/upload).</summary>
    private static bool HasAccessToProgram(AuthenticatedDashboardUser user, string? programId) =>
        user.IsAdmin || (programId is not null && user.AllowedProgramIds.Contains(programId, StringComparer.OrdinalIgnoreCase));

    /// <summary>Sprint 6 (2026-11-16) — GET /api/users: lista os usuários cadastrados (nunca inclui hash/sal de senha — <c>DashboardUserRecord</c> nem os carrega). Só Admin.</summary>
    private static PlaylistDashboardResponse HandleListUsers(string rootFolder, AuthenticatedDashboardUser user)
    {
        if (!user.IsAdmin)
        {
            return PlaylistDashboardResponse.PlainTextError(403, "só Administradores veem a lista de usuários.");
        }

        var (ok, error, users) = UserStore.ListUsers(rootFolder);
        if (!ok)
        {
            return PlaylistDashboardResponse.PlainTextError(500, error ?? "erro desconhecido.");
        }

        var dtos = users.Select(ToDashboardUserDto).ToList();
        return PlaylistDashboardResponse.Json(JsonSerializer.Serialize(dtos, JsonOptions));
    }

    /// <summary>Sprint 6 (2026-11-16) — POST /api/users: cria um usuário novo pela própria dashboard web. Só Admin; validações de nome/senha/"primeiro usuário precisa ser Admin" ficam em <c>UserStore.CreateUser</c>, mesmas de sempre.</summary>
    private static PlaylistDashboardResponse HandleCreateUser(string rootFolder, string? body, AuthenticatedDashboardUser user)
    {
        if (!user.IsAdmin)
        {
            return PlaylistDashboardResponse.PlainTextError(403, "só Administradores criam usuários novos.");
        }

        CreateUserRequest? request;
        try
        {
            request = string.IsNullOrWhiteSpace(body) ? null : JsonSerializer.Deserialize<CreateUserRequest>(body, JsonOptions);
        }
        catch (JsonException)
        {
            return PlaylistDashboardResponse.PlainTextError(400, "corpo da requisição inválido (JSON malformado).");
        }

        if (request is null || string.IsNullOrWhiteSpace(request.Username) || string.IsNullOrWhiteSpace(request.Password))
        {
            return PlaylistDashboardResponse.PlainTextError(400, "\"username\" e \"password\" são obrigatórios.");
        }

        var (ok, error, created) = UserStore.CreateUser(rootFolder, request.Username, request.Password, request.IsAdmin, request.AllowedProgramIds);
        if (!ok || created is null)
        {
            return PlaylistDashboardResponse.PlainTextError(400, error ?? "erro desconhecido.");
        }

        return PlaylistDashboardResponse.Json(JsonSerializer.Serialize(ToDashboardUserDto(created), JsonOptions), 201);
    }

    /// <summary>Sprint 6 (2026-11-16) — PUT /api/users: troca papel (Admin/Produtor) e, se não-Admin, os Programas liberados. Nunca troca usuário/senha (ver <see cref="HandleResetUserPassword"/>). Proteção contra remover o último Admin já vem de <c>UserStore.UpdateUserPermissions</c>.</summary>
    private static PlaylistDashboardResponse HandleUpdateUserPermissions(string rootFolder, string? body, AuthenticatedDashboardUser user)
    {
        if (!user.IsAdmin)
        {
            return PlaylistDashboardResponse.PlainTextError(403, "só Administradores editam permissões de usuários.");
        }

        UpdateUserPermissionsRequest? request;
        try
        {
            request = string.IsNullOrWhiteSpace(body) ? null : JsonSerializer.Deserialize<UpdateUserPermissionsRequest>(body, JsonOptions);
        }
        catch (JsonException)
        {
            return PlaylistDashboardResponse.PlainTextError(400, "corpo da requisição inválido (JSON malformado).");
        }

        if (request is null || string.IsNullOrWhiteSpace(request.Id))
        {
            return PlaylistDashboardResponse.PlainTextError(400, "\"id\" é obrigatório.");
        }

        var (ok, error, updated) = UserStore.UpdateUserPermissions(rootFolder, request.Id, request.IsAdmin, request.AllowedProgramIds);
        if (!ok || updated is null)
        {
            return PlaylistDashboardResponse.PlainTextError(400, error ?? "erro desconhecido.");
        }

        return PlaylistDashboardResponse.Json(JsonSerializer.Serialize(ToDashboardUserDto(updated), JsonOptions));
    }

    /// <summary>Sprint 6 (2026-11-16) — PUT /api/users/password: define uma senha nova pra um usuário já existente. Rota separada da de permissão (mesmo corpo da UI: o campo "nova senha" do modal é opcional, só chama isto se o Admin preencheu algo).</summary>
    private static PlaylistDashboardResponse HandleResetUserPassword(string rootFolder, string? body, AuthenticatedDashboardUser user)
    {
        if (!user.IsAdmin)
        {
            return PlaylistDashboardResponse.PlainTextError(403, "só Administradores trocam a senha de outro usuário.");
        }

        ResetPasswordRequest? request;
        try
        {
            request = string.IsNullOrWhiteSpace(body) ? null : JsonSerializer.Deserialize<ResetPasswordRequest>(body, JsonOptions);
        }
        catch (JsonException)
        {
            return PlaylistDashboardResponse.PlainTextError(400, "corpo da requisição inválido (JSON malformado).");
        }

        if (request is null || string.IsNullOrWhiteSpace(request.Id) || string.IsNullOrWhiteSpace(request.NewPassword))
        {
            return PlaylistDashboardResponse.PlainTextError(400, "\"id\" e \"newPassword\" são obrigatórios.");
        }

        var error = UserStore.ResetPassword(rootFolder, request.Id, request.NewPassword);
        return error is not null
            ? PlaylistDashboardResponse.PlainTextError(400, error)
            : PlaylistDashboardResponse.Json("{}");
    }

    /// <summary>Sprint 6 (2026-11-16) — DELETE /api/users?id=...: remove um usuário. Id vem da query (não do corpo), mesmo padrão já usado por outras rotas desta classe que recebem só um identificador. Proteção contra remover o último Admin já vem de <c>UserStore.DeleteUser</c>.</summary>
    private static PlaylistDashboardResponse HandleDeleteUser(string rootFolder, string? queryString, AuthenticatedDashboardUser user)
    {
        if (!user.IsAdmin)
        {
            return PlaylistDashboardResponse.PlainTextError(403, "só Administradores removem usuários.");
        }

        var id = ParseQueryValue(queryString, "id");
        if (string.IsNullOrWhiteSpace(id))
        {
            return PlaylistDashboardResponse.PlainTextError(400, "parâmetro \"id\" é obrigatório.");
        }

        var error = UserStore.DeleteUser(rootFolder, id);
        return error is not null
            ? PlaylistDashboardResponse.PlainTextError(400, error)
            : PlaylistDashboardResponse.Json("{}");
    }

    private static DashboardUserDto ToDashboardUserDto(DashboardUserRecord user) => new(user.Id, user.Username, user.IsAdmin, user.AllowedProgramIds);

    /// <summary>Sprint 3 (2026-11-16) — presets de Arte de Fundo já cadastrados em Configurações → Arte de Fundo, pro &lt;select&gt; do formulário "Novo Programa" na dashboard.</summary>
    private PlaylistDashboardResponse HandleListBackgroundArtPresets()
    {
        var dtos = _getBackgroundArtPresets().Select(p => new BackgroundArtPresetDto(p.Id, p.Name)).ToList();
        return PlaylistDashboardResponse.Json(JsonSerializer.Serialize(dtos, JsonOptions));
    }

    private static PlaylistDashboardResponse HandleListEditions(string rootFolder, string? queryString, AuthenticatedDashboardUser user)
    {
        var programId = ParseQueryValue(queryString, "programId");
        if (string.IsNullOrWhiteSpace(programId))
        {
            return PlaylistDashboardResponse.PlainTextError(400, "parâmetro \"programId\" é obrigatório.");
        }

        if (!HasAccessToProgram(user, programId))
        {
            return PlaylistDashboardResponse.PlainTextError(403, "você não tem acesso a este Programa.");
        }

        var (ok, error, editions) = PlaylistFileStore.ListEditions(rootFolder, programId);
        if (!ok)
        {
            return PlaylistDashboardResponse.PlainTextError(500, error ?? "erro desconhecido.");
        }

        var dtos = editions.Select(e => new EditionDto(e.Id, e.DateIso)).ToList();
        return PlaylistDashboardResponse.Json(JsonSerializer.Serialize(dtos, JsonOptions));
    }

    private static PlaylistDashboardResponse HandleCreateEdition(string rootFolder, string? body, AuthenticatedDashboardUser user)
    {
        CreateEditionRequest? request;
        try
        {
            request = string.IsNullOrWhiteSpace(body) ? null : JsonSerializer.Deserialize<CreateEditionRequest>(body, JsonOptions);
        }
        catch (JsonException)
        {
            return PlaylistDashboardResponse.PlainTextError(400, "corpo da requisição inválido (esperado JSON com \"programId\" e \"date\").");
        }

        if (request is null || string.IsNullOrWhiteSpace(request.ProgramId) || string.IsNullOrWhiteSpace(request.Date))
        {
            return PlaylistDashboardResponse.PlainTextError(400, "\"programId\" e \"date\" são obrigatórios.");
        }

        if (!HasAccessToProgram(user, request.ProgramId))
        {
            return PlaylistDashboardResponse.PlainTextError(403, "você não tem acesso a este Programa.");
        }

        if (!DateOnly.TryParse(request.Date, out var date))
        {
            return PlaylistDashboardResponse.PlainTextError(400, $"data inválida: \"{request.Date}\" (esperado AAAA-MM-DD).");
        }

        var (ok, error, edition) = PlaylistFileStore.CreateEdition(rootFolder, request.ProgramId, date);
        if (!ok || edition is null)
        {
            return PlaylistDashboardResponse.PlainTextError(400, error ?? "erro desconhecido.");
        }

        return PlaylistDashboardResponse.Json(JsonSerializer.Serialize(new EditionDto(edition.Id, edition.DateIso), JsonOptions), 201);
    }

    /// <summary>
    /// Sprint 2 (2026-11-15) — lista as Laudas do Espelho de uma Edição. Exige <c>programId</c> E
    /// <c>editionId</c> na query (não dá pra achar a pasta de uma Edição só pelo Id dela, porque
    /// <c>editions.json</c> vive dentro da pasta do Programa — ver
    /// <c>PlaylistFileStore.ResolveEditionFolder</c>), mesmo padrão de parâmetros já usado por
    /// <see cref="HandleListEditions"/>.
    /// </summary>
    private static PlaylistDashboardResponse HandleListLaudas(string rootFolder, string? queryString, AuthenticatedDashboardUser user)
    {
        var programId = ParseQueryValue(queryString, "programId");
        var editionId = ParseQueryValue(queryString, "editionId");
        if (string.IsNullOrWhiteSpace(programId) || string.IsNullOrWhiteSpace(editionId))
        {
            return PlaylistDashboardResponse.PlainTextError(400, "parâmetros \"programId\" e \"editionId\" são obrigatórios.");
        }

        if (!HasAccessToProgram(user, programId))
        {
            return PlaylistDashboardResponse.PlainTextError(403, "você não tem acesso a este Programa.");
        }

        var (ok, error, laudas) = PlaylistFileStore.ListLaudas(rootFolder, programId, editionId);
        if (!ok)
        {
            return PlaylistDashboardResponse.PlainTextError(500, error ?? "erro desconhecido.");
        }

        var dtos = laudas.Select(ToLaudaDto).ToList();
        return PlaylistDashboardResponse.Json(JsonSerializer.Serialize(dtos, JsonOptions));
    }

    /// <summary>Sprint 2 (2026-11-15) — cria uma Lauda nova (só metadados; VT/Imagens entram depois, via <see cref="HandleUploadFile"/>, já com o Id devolvido aqui).</summary>
    private static PlaylistDashboardResponse HandleCreateLauda(string rootFolder, string? body, AuthenticatedDashboardUser user)
    {
        CreateLaudaRequest? request;
        try
        {
            request = string.IsNullOrWhiteSpace(body) ? null : JsonSerializer.Deserialize<CreateLaudaRequest>(body, JsonOptions);
        }
        catch (JsonException)
        {
            return PlaylistDashboardResponse.PlainTextError(400, "corpo da requisição inválido (JSON malformado).");
        }

        if (request is null || string.IsNullOrWhiteSpace(request.ProgramId) || string.IsNullOrWhiteSpace(request.EditionId))
        {
            return PlaylistDashboardResponse.PlainTextError(400, "\"programId\" e \"editionId\" são obrigatórios.");
        }

        if (!HasAccessToProgram(user, request.ProgramId))
        {
            return PlaylistDashboardResponse.PlainTextError(403, "você não tem acesso a este Programa.");
        }

        // Pedido do usuário (2026-11-15): nenhum campo da Lauda é obrigatório pra salvar, inclusive
        // o Título — "programId"/"editionId" continuam exigidos porque são amarração interna (qual
        // Edição recebe a Lauda), nunca algo que o produtor digita. Ver PlaylistFileStore.CreateLauda
        // pro "(sem título)" só de exibição quando o Título vem vazio.
        var (ok, error, lauda) = PlaylistFileStore.CreateLauda(
            rootFolder,
            request.ProgramId,
            request.EditionId,
            request.Title,
            request.Type,
            request.GcFields,
            request.TpText,
            request.OffText,
            request.DurationPlanned,
            request.Notes);

        if (!ok || lauda is null)
        {
            return PlaylistDashboardResponse.PlainTextError(400, error ?? "erro desconhecido.");
        }

        return PlaylistDashboardResponse.Json(JsonSerializer.Serialize(ToLaudaDto(lauda), JsonOptions), 201);
    }

    /// <summary>Sprint 3 (2026-11-16) — edita os campos de metadado de uma Lauda já existente (pedido do usuário "poder abrir cada lauda e poder editar ela e salvar"). VT/Imagens não mudam por aqui, ver <see cref="HandleUploadFile"/>.</summary>
    private static PlaylistDashboardResponse HandleUpdateLauda(string rootFolder, string? body, AuthenticatedDashboardUser user)
    {
        UpdateLaudaRequest? request;
        try
        {
            request = string.IsNullOrWhiteSpace(body) ? null : JsonSerializer.Deserialize<UpdateLaudaRequest>(body, JsonOptions);
        }
        catch (JsonException)
        {
            return PlaylistDashboardResponse.PlainTextError(400, "corpo da requisição inválido (JSON malformado).");
        }

        if (request is null || string.IsNullOrWhiteSpace(request.LaudaId) || string.IsNullOrWhiteSpace(request.ProgramId) || string.IsNullOrWhiteSpace(request.EditionId))
        {
            return PlaylistDashboardResponse.PlainTextError(400, "\"laudaId\", \"programId\" e \"editionId\" são obrigatórios.");
        }

        if (!HasAccessToProgram(user, request.ProgramId))
        {
            return PlaylistDashboardResponse.PlainTextError(403, "você não tem acesso a este Programa.");
        }

        var (ok, error, lauda) = PlaylistFileStore.UpdateLauda(
            rootFolder,
            request.ProgramId,
            request.EditionId,
            request.LaudaId,
            request.Title,
            request.Type,
            request.GcFields,
            request.TpText,
            request.OffText,
            request.DurationPlanned,
            request.Notes);

        if (!ok || lauda is null)
        {
            return PlaylistDashboardResponse.PlainTextError(400, error ?? "erro desconhecido.");
        }

        return PlaylistDashboardResponse.Json(JsonSerializer.Serialize(ToLaudaDto(lauda), JsonOptions));
    }

    /// <summary>Sprint 3 (2026-11-16) — reordena as Laudas do Espelho conforme a sequência arrastada na dashboard (pedido do usuário "poder mover ela para cima e para baixo").</summary>
    private static PlaylistDashboardResponse HandleReorderLaudas(string rootFolder, string? body, AuthenticatedDashboardUser user)
    {
        ReorderLaudasRequest? request;
        try
        {
            request = string.IsNullOrWhiteSpace(body) ? null : JsonSerializer.Deserialize<ReorderLaudasRequest>(body, JsonOptions);
        }
        catch (JsonException)
        {
            return PlaylistDashboardResponse.PlainTextError(400, "corpo da requisição inválido (JSON malformado).");
        }

        if (request is null || string.IsNullOrWhiteSpace(request.ProgramId) || string.IsNullOrWhiteSpace(request.EditionId) || request.OrderedIds is null)
        {
            return PlaylistDashboardResponse.PlainTextError(400, "\"programId\", \"editionId\" e \"orderedIds\" são obrigatórios.");
        }

        if (!HasAccessToProgram(user, request.ProgramId))
        {
            return PlaylistDashboardResponse.PlainTextError(403, "você não tem acesso a este Programa.");
        }

        var (ok, error, laudas) = PlaylistFileStore.ReorderLaudas(rootFolder, request.ProgramId, request.EditionId, request.OrderedIds);
        if (!ok)
        {
            return PlaylistDashboardResponse.PlainTextError(400, error ?? "erro desconhecido.");
        }

        var dtos = laudas.Select(ToLaudaDto).ToList();
        return PlaylistDashboardResponse.Json(JsonSerializer.Serialize(dtos, JsonOptions));
    }

    /// <summary>
    /// Sprint 2 (2026-11-15) — recebe o arquivo (VT ou imagem) direto no corpo cru da requisição PUT
    /// (ver comentário de <c>PlaylistDashboardHost.PlaylistUploadHandler</c> pro porquê de não ser
    /// multipart/form-data). Query esperada: <c>programId</c>, <c>editionId</c>, <c>laudaId</c>,
    /// <c>filename</c> (nome original do arquivo, só pra preservar a extensão — o nome final em disco
    /// pode ficar diferente se já existir um igual, ver <c>PlaylistFileStore.SaveUploadedFile</c>).
    /// </summary>
    private static PlaylistDashboardResponse HandleUploadFile(string rootFolder, string? queryString, Stream requestBodyStream, PlaylistUploadKind kind, AuthenticatedDashboardUser user)
    {
        var programId = ParseQueryValue(queryString, "programId");
        var editionId = ParseQueryValue(queryString, "editionId");
        var laudaId = ParseQueryValue(queryString, "laudaId");
        var fileName = ParseQueryValue(queryString, "filename") ?? "arquivo";

        if (string.IsNullOrWhiteSpace(programId) || string.IsNullOrWhiteSpace(editionId) || string.IsNullOrWhiteSpace(laudaId))
        {
            return PlaylistDashboardResponse.PlainTextError(400, "parâmetros \"programId\", \"editionId\" e \"laudaId\" são obrigatórios.");
        }

        if (!HasAccessToProgram(user, programId))
        {
            return PlaylistDashboardResponse.PlainTextError(403, "você não tem acesso a este Programa.");
        }

        var (ok, error, savedFileName) = PlaylistFileStore.SaveUploadedFile(rootFolder, programId, editionId, laudaId, kind, fileName, requestBodyStream);
        if (!ok || savedFileName is null)
        {
            return PlaylistDashboardResponse.PlainTextError(400, error ?? "erro desconhecido.");
        }

        return PlaylistDashboardResponse.Json(JsonSerializer.Serialize(new UploadResultDto(savedFileName), JsonOptions));
    }

    /// <summary>Sprint Controle-Remoto-TP (2026-11-17) — GET /api/teleprompter/state: pra tela remota saber se o manual está ligado e o que está exibido agora (automático ou manual), tanto ao abrir a página quanto no polling periódico (ver app.js).</summary>
    private PlaylistDashboardResponse HandleGetTeleprompterState() =>
        PlaylistDashboardResponse.Json(JsonSerializer.Serialize(ToTeleprompterStateDto(), JsonOptions));

    private TeleprompterStateDto ToTeleprompterStateDto() => new(
        _teleprompter.IsManualOverride,
        _teleprompter.CurrentText,
        _teleprompter.ScrollFraction,
        _teleprompter.IsAutoScrollPlaying,
        _teleprompter.AutoScrollSpeed,
        _teleprompter.FontSizePt,
        _teleprompter.IsMirrored);

    /// <summary>Sprint Controle-Remoto-TP (2026-11-17) — POST /api/teleprompter/manual: liga/desliga o controle manual remoto (ver <see cref="ViewModels.TeleprompterViewModel.SetManualOverride"/> pro efeito completo, incluindo desligar a voz junto ao ligar).</summary>
    private PlaylistDashboardResponse HandleSetTeleprompterManual(string? body)
    {
        SetTeleprompterManualRequest? request;
        try
        {
            request = string.IsNullOrWhiteSpace(body) ? null : JsonSerializer.Deserialize<SetTeleprompterManualRequest>(body, JsonOptions);
        }
        catch (JsonException)
        {
            return PlaylistDashboardResponse.PlainTextError(400, "corpo da requisição inválido (esperado JSON com \"enabled\").");
        }

        if (request is null)
        {
            return PlaylistDashboardResponse.PlainTextError(400, "\"enabled\" é obrigatório (true/false).");
        }

        _teleprompter.SetManualOverride(request.Enabled);
        return PlaylistDashboardResponse.Json(JsonSerializer.Serialize(ToTeleprompterStateDto(), JsonOptions));
    }

    /// <summary>
    /// Sprint Controle-Remoto-TP (2026-11-17) — POST /api/teleprompter/text: define o texto exibido
    /// manualmente. Só tem efeito com o manual já ligado (ver <see cref="ViewModels.TeleprompterViewModel.SetManualText"/>);
    /// aqui devolve 409 nesse caso em vez de aceitar silenciosamente, pra tela remota avisar o
    /// produtor em vez de parecer que salvou e não fez nada.
    /// </summary>
    private PlaylistDashboardResponse HandleSetTeleprompterText(string? body)
    {
        SetTeleprompterTextRequest? request;
        try
        {
            request = string.IsNullOrWhiteSpace(body) ? null : JsonSerializer.Deserialize<SetTeleprompterTextRequest>(body, JsonOptions);
        }
        catch (JsonException)
        {
            return PlaylistDashboardResponse.PlainTextError(400, "corpo da requisição inválido (esperado JSON com \"text\").");
        }

        if (request is null)
        {
            return PlaylistDashboardResponse.PlainTextError(400, "\"text\" é obrigatório (pode ser \"\" pra limpar).");
        }

        if (!_teleprompter.IsManualOverride)
        {
            return PlaylistDashboardResponse.PlainTextError(409, "ligue o controle manual antes de mandar texto (POST /api/teleprompter/manual com \"enabled\": true).");
        }

        _teleprompter.SetManualText(request.Text ?? string.Empty);
        return PlaylistDashboardResponse.Json(JsonSerializer.Serialize(ToTeleprompterStateDto(), JsonOptions));
    }

    /// <summary>
    /// Sprint Controle-Remoto-TP, parte 3 (2026-11-17) — POST /api/teleprompter/scroll-step: passo
    /// manual (seta pra cima/baixo), equivalente remoto da tecla local (ver
    /// <see cref="ViewModels.TeleprompterViewModel.StepScrollUp"/>/<c>StepScrollDown</c>, que já
    /// ignoram silenciosamente fora do manual — esta rota nunca devolve 409 por isso, igual
    /// <see cref="HandleSetTeleprompterManual"/>).
    /// </summary>
    private PlaylistDashboardResponse HandleTeleprompterScrollStep(string? body)
    {
        ScrollStepRequest? request;
        try
        {
            request = string.IsNullOrWhiteSpace(body) ? null : JsonSerializer.Deserialize<ScrollStepRequest>(body, JsonOptions);
        }
        catch (JsonException)
        {
            return PlaylistDashboardResponse.PlainTextError(400, "corpo da requisição inválido (esperado JSON com \"direction\": \"up\"|\"down\").");
        }

        if (request is null || (request.Direction != "up" && request.Direction != "down"))
        {
            return PlaylistDashboardResponse.PlainTextError(400, "\"direction\" é obrigatório (\"up\" ou \"down\").");
        }

        if (request.Direction == "up")
        {
            _teleprompter.StepScrollUp();
        }
        else
        {
            _teleprompter.StepScrollDown();
        }

        return PlaylistDashboardResponse.Json(JsonSerializer.Serialize(ToTeleprompterStateDto(), JsonOptions));
    }

    /// <summary>Sprint Controle-Remoto-TP, parte 3 (2026-11-17) — POST /api/teleprompter/scroll-to: pula direto pra uma fração 0..1 do texto (barra arrastável/scrubber do espelho remoto).</summary>
    private PlaylistDashboardResponse HandleTeleprompterScrollTo(string? body)
    {
        ScrollToRequest? request;
        try
        {
            request = string.IsNullOrWhiteSpace(body) ? null : JsonSerializer.Deserialize<ScrollToRequest>(body, JsonOptions);
        }
        catch (JsonException)
        {
            return PlaylistDashboardResponse.PlainTextError(400, "corpo da requisição inválido (esperado JSON com \"fraction\": número de 0 a 1).");
        }

        if (request is null)
        {
            return PlaylistDashboardResponse.PlainTextError(400, "\"fraction\" é obrigatório (número de 0 a 1).");
        }

        _teleprompter.SetScrollFractionRemote(request.Fraction);
        return PlaylistDashboardResponse.Json(JsonSerializer.Serialize(ToTeleprompterStateDto(), JsonOptions));
    }

    /// <summary>Sprint Controle-Remoto-TP, parte 3 (2026-11-17) — POST /api/teleprompter/jump-start (sem corpo): volta pro início do texto, equivalente remoto da tecla Home local.</summary>
    private PlaylistDashboardResponse HandleTeleprompterJumpStart()
    {
        _teleprompter.JumpToStart();
        return PlaylistDashboardResponse.Json(JsonSerializer.Serialize(ToTeleprompterStateDto(), JsonOptions));
    }

    /// <summary>
    /// Sprint Controle-Remoto-TP, parte 3 (2026-11-17) — POST /api/teleprompter/autoscroll/play:
    /// liga/desliga a rolagem automática por tempo (ver
    /// <see cref="ViewModels.TeleprompterViewModel.SetAutoScrollPlaying"/> — controle central desta
    /// parte 3, já que com o manual ligado a rolagem por voz fica desligada e sem isto o produtor só
    /// teria o passo manual).
    /// </summary>
    private PlaylistDashboardResponse HandleTeleprompterAutoScrollPlay(string? body)
    {
        AutoScrollPlayRequest? request;
        try
        {
            request = string.IsNullOrWhiteSpace(body) ? null : JsonSerializer.Deserialize<AutoScrollPlayRequest>(body, JsonOptions);
        }
        catch (JsonException)
        {
            return PlaylistDashboardResponse.PlainTextError(400, "corpo da requisição inválido (esperado JSON com \"playing\").");
        }

        if (request is null)
        {
            return PlaylistDashboardResponse.PlainTextError(400, "\"playing\" é obrigatório (true/false).");
        }

        _teleprompter.SetAutoScrollPlaying(request.Playing);
        return PlaylistDashboardResponse.Json(JsonSerializer.Serialize(ToTeleprompterStateDto(), JsonOptions));
    }

    /// <summary>Sprint Controle-Remoto-TP, parte 3 (2026-11-17) — POST /api/teleprompter/autoscroll/speed: troca a velocidade (escala 1-10, ver comentário de <c>TeleprompterViewModel._autoScrollSpeed</c> sobre a escolha dessa escala em vez de palavras-por-minuto).</summary>
    private PlaylistDashboardResponse HandleTeleprompterAutoScrollSpeed(string? body)
    {
        AutoScrollSpeedRequest? request;
        try
        {
            request = string.IsNullOrWhiteSpace(body) ? null : JsonSerializer.Deserialize<AutoScrollSpeedRequest>(body, JsonOptions);
        }
        catch (JsonException)
        {
            return PlaylistDashboardResponse.PlainTextError(400, "corpo da requisição inválido (esperado JSON com \"speed\": número de 1 a 10).");
        }

        if (request is null)
        {
            return PlaylistDashboardResponse.PlainTextError(400, "\"speed\" é obrigatório (número de 1 a 10).");
        }

        _teleprompter.SetAutoScrollSpeed(request.Speed);
        return PlaylistDashboardResponse.Json(JsonSerializer.Serialize(ToTeleprompterStateDto(), JsonOptions));
    }

    private static LaudaDto ToLaudaDto(PlaylistLaudaRecord lauda) => new(
        lauda.Id,
        lauda.Order,
        lauda.Title,
        lauda.Type,
        lauda.FileName,
        lauda.GcFields,
        lauda.TpText,
        lauda.OffText,
        lauda.ImageFileNames,
        lauda.DurationPlanned,
        lauda.Duration,
        lauda.Status,
        lauda.Notes);

    /// <summary>Leitura de query string bem simples (só pares "chave=valor" separados por "&amp;") — suficiente pro único parâmetro usado nesta sprint (<c>programId</c>), sem puxar nenhuma dependência de parsing de URL maior.</summary>
    private static string? ParseQueryValue(string? queryString, string key)
    {
        if (string.IsNullOrWhiteSpace(queryString))
        {
            return null;
        }

        foreach (var pair in queryString.Split('&'))
        {
            var parts = pair.Split('=', 2);
            if (parts.Length == 2 && string.Equals(Uri.UnescapeDataString(parts[0]), key, StringComparison.OrdinalIgnoreCase))
            {
                return Uri.UnescapeDataString(parts[1]);
            }
        }

        return null;
    }

    public void Dispose()
    {
        _teleprompter.StateChanged -= OnTeleprompterStateChanged;
        _host?.Dispose();
        _host = null;
        _teleprompterSseChannel = null;
    }

    private sealed record ProgramDto(string Id, string Name);

    private sealed record EditionDto(string Id, string DateIso);

    /// <summary><see cref="DefaultBackgroundArtId"/> — Sprint 3 (2026-11-16): opcional, Id de um preset já cadastrado (ver <see cref="HandleListBackgroundArtPresets"/>); vazio/nulo significa "sem fundo padrão".</summary>
    private sealed record CreateProgramRequest(string? Name, string? DefaultBackgroundArtId);

    private sealed record BackgroundArtPresetDto(string Id, string Name);

    private sealed record UpdateLaudaRequest(
        string? LaudaId,
        string? ProgramId,
        string? EditionId,
        string? Title,
        string? Type,
        Dictionary<string, string>? GcFields,
        string? TpText,
        string? OffText,
        string? DurationPlanned,
        string? Notes);

    private sealed record ReorderLaudasRequest(string? ProgramId, string? EditionId, List<string>? OrderedIds);

    private sealed record CreateEditionRequest(string? ProgramId, string? Date);

    /// <summary>Formato de uma Lauda devolvido pra dashboard — Sprint 2 (2026-11-15). <c>gcFields</c> continua um dicionário livre nome→valor, mesmo formato já usado desde a Sprint 1.</summary>
    private sealed record LaudaDto(
        string Id,
        int Order,
        string Title,
        string Type,
        string FileName,
        IReadOnlyDictionary<string, string> GcFields,
        string? TpText,
        string? OffText,
        IReadOnlyList<string> Images,
        string? DurationPlanned,
        string? Duration,
        string Status,
        string? Notes);

    private sealed record CreateLaudaRequest(
        string? ProgramId,
        string? EditionId,
        string? Title,
        string? Type,
        Dictionary<string, string>? GcFields,
        string? TpText,
        string? OffText,
        string? DurationPlanned,
        string? Notes);

    private sealed record UploadResultDto(string FileName);

    private sealed record LoginRequest(string? Username, string? Password);

    /// <summary>Sprint 5 (2026-11-16) — devolvido tanto por <see cref="HandleLogin"/> quanto por <see cref="HandleMe"/>, mesmo formato pras duas respostas.</summary>
    private sealed record AuthUserDto(string Id, string Username, bool IsAdmin, IReadOnlyList<string> AllowedProgramIds);

    /// <summary>Sprint 6 (2026-11-16) — formato de um usuário da dashboard devolvido pra tela "Usuários" (web). Nunca inclui hash/sal de senha — <c>UserStore.DashboardUserRecord</c> nem os carrega pra fora da Infrastructure.</summary>
    private sealed record DashboardUserDto(string Id, string Username, bool IsAdmin, IReadOnlyList<string> AllowedProgramIds);

    private sealed record CreateUserRequest(string? Username, string? Password, bool IsAdmin, List<string>? AllowedProgramIds);

    private sealed record UpdateUserPermissionsRequest(string? Id, bool IsAdmin, List<string>? AllowedProgramIds);

    private sealed record ResetPasswordRequest(string? Id, string? NewPassword);

    /// <summary>
    /// Sprint Controle-Remoto-TP (2026-11-17) — devolvido por GET /api/teleprompter/state E depois
    /// de toda rota POST /api/teleprompter/* (pra tela remota atualizar sem precisar de uma segunda
    /// chamada). Expandido na parte 3 (2026-11-17) com os campos do espelho de rolagem/autoscroll —
    /// <see cref="ScrollFraction"/> (0..1, posição de leitura atual, ver
    /// <c>TeleprompterViewModel.ScrollFraction</c>), <see cref="AutoScrollPlaying"/>/
    /// <see cref="AutoScrollSpeed"/> (estado do Play remoto), <see cref="FontSizePt"/>/
    /// <see cref="Mirrored"/> (só pra dashboard desenhar o espelho fiel ao tamanho de fonte/
    /// orientação reais da janela local, sem ter que duplicar essa configuração).
    /// </summary>
    private sealed record TeleprompterStateDto(
        bool ManualOverride,
        string CurrentText,
        double ScrollFraction,
        bool AutoScrollPlaying,
        int AutoScrollSpeed,
        double FontSizePt,
        bool Mirrored);

    private sealed record SetTeleprompterManualRequest(bool Enabled);

    private sealed record SetTeleprompterTextRequest(string? Text);

    private sealed record ScrollStepRequest(string? Direction);

    private sealed record ScrollToRequest(double Fraction);

    private sealed record AutoScrollPlayRequest(bool Playing);

    private sealed record AutoScrollSpeedRequest(int Speed);
}
