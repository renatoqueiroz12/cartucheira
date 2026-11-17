using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace CartucheiraVideo.Api.PlaylistDashboard;

/// <summary>
/// Servidor HTTP da dashboard de Playlist — Sprint Playlist-Dashboard (2026-11-14, ver
/// arquitetura/playlist-modulo-roteiro.md). Serve a página web que os produtores usam pra organizar
/// Programa/Data/ingest, e expõe as rotas de API (registradas de fora, ver <see cref="RegisterApiRoute"/>)
/// que ela consome.
///
/// **Diferença deliberada e importante em relação ao <see cref="LocalTriggerApiHost"/> (mesma
/// biblioteca, <c>HttpListener</c>, mesmo projeto):** aquele servidor escuta só em
/// <c>http://localhost:porta/</c> de propósito (comando de um Stream Deck/Companion rodando na MESMA
/// máquina). Esta dashboard precisa ser alcançada por OUTRO computador da rede local (o produtor) —
/// por isso escuta em <c>http://+:porta/</c> (qualquer interface de rede da máquina). Isso tem duas
/// consequências que o <c>LocalTriggerApiHost</c> não tinha: (1) no Windows, reservar um prefixo
/// "+"/"*"/IP específico (diferente de <c>localhost</c>) normalmente exige rodar como Administrador
/// OU uma reserva prévia de URL ACL (<c>netsh http add urlacl</c>) — <see cref="Start"/> nunca assume
/// que vai funcionar, devolve a instrução exata se o Windows recusar; (2) qualquer computador na
/// mesma rede local consegue alcançar este servidor, não só processos da própria máquina — por isso
/// depende de uma regra de Firewall de ENTRADA liberando a porta
/// (<c>CartucheiraVideo.Infrastructure.Playlist.PlaylistDashboardFirewallGuard</c>, aplicada pela
/// Presentation).
///
/// **Login/permissão — Sprint 5 (2026-11-16, pedido do usuário "precisamos de uma barra de
/// login... criar usuários... dando permissão")**: substitui a v1 sem nenhuma autenticação. Toda
/// rota registrada via <see cref="RegisterApiRoute"/>/<see cref="RegisterUploadRoute"/> agora exige
/// uma sessão válida (cookie <c>cartucheira_session</c>, 12h de validade a partir do login,
/// resolvida por <see cref="TryResolveSession"/> ANTES de chamar o handler — 401 sem nem chamar o
/// handler se não houver sessão válida) — só <see cref="RegisterLoginRoute"/>/
/// <see cref="RegisterLogoutRoute"/> ficam de fora desse gate, por definição (não dá pra exigir
/// login pra poder logar). Arquivos estáticos (HTML/CSS/JS, ver <see cref="ServeStaticFile"/>)
/// continuam sem gate nenhum — é a própria dashboard (app.js) que decide mostrar só a tela de login
/// ou o resto da UI, com base na resposta de <c>GET /api/auth/me</c> (uma rota comum, protegida,
/// registrada pela Presentation como qualquer outra). Sessão guardada só em memória
/// (<see cref="_sessions"/>) — reiniciar a Cartucheira desloga todo mundo, aceitável pro tamanho
/// desta dashboard. Sem HTTPS nesta v1 (mesmo princípio aceito pro resto da dashboard: rede local
/// do estúdio, não exposta à internet) — o cookie não tem a flag <c>Secure</c> de propósito (exigiria
/// HTTPS, que não existe aqui), mas tem <c>HttpOnly</c> (JS da própria dashboard não lê o cookie,
/// só o navegador manda de volta) e <c>SameSite=Lax</c>.
///
/// Vocabulário 100% HTTP/sessão puro, mesmo princípio de isolamento do <c>LocalTriggerApiHost</c>:
/// nenhuma referência a <c>Core</c>/<c>Infrastructure</c>/<c>Configuration</c> — as rotas de API são
/// só "método+caminho → função que recebe query string, corpo cru e o usuário autenticado da sessão,
/// devolve status+corpo" (ver <see cref="PlaylistApiHandler"/>); quem liga uma rota a lógica de
/// verdade (ler/criar Programa/Edição em disco, verificar usuário/senha) é a Presentation
/// (<c>PlaylistDashboardCoordinator</c>).
///
/// Nunca lança: erro processando UMA requisição nunca derruba o servidor inteiro, mesma defesa já
/// usada pelo <see cref="LocalTriggerApiHost"/>.
///
/// **Server-Sent Events — Sprint Controle-Remoto-TP, parte 2 (2026-11-17, pedido do usuário "2
/// segundos e muito, precisamos pensar outra alternativa" ao polling de 2s da v1.0.95)**:
/// <see cref="RegisterSseRoute"/>/<see cref="PlaylistSseChannel"/> dão uma rota que nunca devolve
/// uma resposta completa — fica com a conexão aberta indefinidamente, recebendo <c>Broadcast</c>s
/// de quem registrou o canal sempre que houver algo novo pra mandar, em vez da dashboard ter que
/// perguntar de novo e de novo. Mesmo gate de sessão das rotas normais; ver comentário de classe
/// completo de <see cref="PlaylistSseChannel"/> pro resto (thread-safety, detecção de desconexão,
/// heartbeat).
/// </summary>
public sealed class PlaylistDashboardHost : IDisposable
{
    /// <summary>Recebe a query string crua (sem o "?"), o corpo cru da requisição (nulo em GET) e o usuário autenticado da sessão (nunca nulo — rota só é chamada com sessão válida, ver comentário de classe) — devolve a resposta HTTP completa.</summary>
    public delegate PlaylistDashboardResponse PlaylistApiHandler(string? queryString, string? requestBody, AuthenticatedDashboardUser user);

    /// <summary>
    /// Mesmo papel de <see cref="PlaylistApiHandler"/>, mas pra upload de arquivo (VT/imagem) — Sprint
    /// Playlist-Dashboard, Sprint 2 (2026-11-15). Recebe o corpo da requisição como <see cref="Stream"/>
    /// CRU em vez de texto: um upload de vídeo/imagem decodificado como UTF-8 (o que
    /// <see cref="PlaylistApiHandler"/> faz) corromperia os bytes binários. O navegador manda o
    /// arquivo puro no corpo da requisição (<c>fetch(url, {{ method: "PUT", body: file }})</c> do lado
    /// do <c>app.js</c> — SEM multipart/form-data, deliberado: evita escrever um parser de multipart à
    /// mão só pra isto, mesmo princípio de "o mínimo necessário" já seguido no resto da dashboard).
    /// Quem chama é responsável por ler o quanto precisar do stream (tipicamente copiando pra um
    /// arquivo) — o host fecha a conexão depois que o handler retorna, nunca antes.
    /// </summary>
    public delegate PlaylistDashboardResponse PlaylistUploadHandler(string? queryString, Stream requestBodyStream, AuthenticatedDashboardUser user);

    /// <summary>Rota pública de login (ver <see cref="RegisterLoginRoute"/>) — recebe o corpo cru (usuário/senha, formato decidido pela Presentation) e devolve a resposta HTTP junto com quem autenticou, se alguém autenticou (ver <see cref="PlaylistLoginAttemptResult"/>).</summary>
    public delegate PlaylistLoginAttemptResult PlaylistLoginHandler(string? requestBody);

    private const string SessionCookieName = "cartucheira_session";
    private static readonly TimeSpan SessionLifetime = TimeSpan.FromHours(12);

    private static readonly Dictionary<string, string> ContentTypesByExtension = new(StringComparer.OrdinalIgnoreCase)
    {
        [".html"] = "text/html; charset=utf-8",
        [".js"] = "text/javascript; charset=utf-8",
        [".css"] = "text/css; charset=utf-8",
        [".json"] = "application/json; charset=utf-8",
        [".png"] = "image/png",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".ico"] = "image/x-icon",
        [".svg"] = "image/svg+xml",
    };

    private readonly HttpListener _listener = new();
    private readonly Dictionary<string, PlaylistApiHandler> _apiRoutes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, PlaylistUploadHandler> _uploadRoutes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, PlaylistLoginHandler> _loginRoutes = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _logoutRoutes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, PlaylistSseChannel> _sseRoutes = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _routesLock = new();
    private readonly Dictionary<string, SessionEntry> _sessions = new(StringComparer.Ordinal);
    private readonly object _sessionsLock = new();
    private readonly int _port;
    private readonly string _webRootFolder;
    private CancellationTokenSource? _cts;
    private Task? _loopTask;
    private volatile bool _disposed;
    private volatile bool _started;

    public PlaylistDashboardHost(int port, string webRootFolder)
    {
        _port = port;
        _webRootFolder = webRootFolder;
        _listener.Prefixes.Add($"http://+:{port}/");
    }

    public int Port => _port;

    /// <summary>Registra uma rota de API — chave é "MÉTODO caminho" (ex.: "GET /api/playlist/programs"), caminho sempre normalizado (sem barra final, exceto raiz).</summary>
    public void RegisterApiRoute(string method, string path, PlaylistApiHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);

        lock (_routesLock)
        {
            _apiRoutes[RouteKey(method, path)] = handler;
        }
    }

    /// <summary>Registra uma rota de upload de arquivo — ver <see cref="PlaylistUploadHandler"/> pro porquê de ser uma rota separada das de <see cref="RegisterApiRoute"/>.</summary>
    public void RegisterUploadRoute(string method, string path, PlaylistUploadHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);

        lock (_routesLock)
        {
            _uploadRoutes[RouteKey(method, path)] = handler;
        }
    }

    /// <summary>
    /// Registra a rota pública de login (ver <see cref="PlaylistLoginHandler"/>) — Sprint 5
    /// (2026-11-16). A ÚNICA rota de API que nunca exige sessão válida (senão seria impossível
    /// logar). Quando o handler devolve um <see cref="AuthenticatedDashboardUser"/> não-nulo, o Host
    /// mesmo cria a sessão nova e manda o cookie — o handler (Presentation) só precisa verificar
    /// usuário/senha, nunca lida com cookie/token.
    /// </summary>
    public void RegisterLoginRoute(string method, string path, PlaylistLoginHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);

        lock (_routesLock)
        {
            _loginRoutes[RouteKey(method, path)] = handler;
        }
    }

    /// <summary>
    /// Registra a rota pública de logout — Sprint 5 (2026-11-16). Sem handler: o próprio Host limpa
    /// a sessão (se existir) e manda um cookie vazio/expirado de volta. Pública pelo mesmo motivo do
    /// login (se já expirou/não existe sessão, só devolve sucesso mesmo assim — "já estava
    /// deslogado" não é um erro).
    /// </summary>
    public void RegisterLogoutRoute(string method, string path)
    {
        lock (_routesLock)
        {
            _logoutRoutes.Add(RouteKey(method, path));
        }
    }

    /// <summary>
    /// Registra uma rota de "Server-Sent Events" — Sprint Controle-Remoto-TP, parte 2 (2026-11-17,
    /// ver comentário de classe de <see cref="PlaylistSseChannel"/> pro motivo: trocar polling por
    /// um servidor que AVISA a dashboard na hora que algo muda). Devolve o <see cref="PlaylistSseChannel"/>
    /// na hora do registro (não é assíncrono nem depende de nenhuma conexão já ter chegado) — quem
    /// chamou guarda essa referência e chama <see cref="PlaylistSseChannel.Broadcast"/> sempre que
    /// tiver algo novo pra mandar pra quem estiver conectado àquela rota, nenhuma conexão precisa já
    /// existir no momento do registro. Mesma exigência de sessão válida de <see cref="RegisterApiRoute"/>
    /// (ver <see cref="ProcessRequest"/>) — só GET faz sentido pra uma rota de streaming, mas o
    /// método é um parâmetro aqui só pra manter a mesma assinatura das outras rotas desta classe.
    /// </summary>
    public PlaylistSseChannel RegisterSseRoute(string method, string path)
    {
        var channel = new PlaylistSseChannel();
        lock (_routesLock)
        {
            _sseRoutes[RouteKey(method, path)] = channel;
        }

        return channel;
    }

    /// <summary>Abre a porta e começa a escutar — nunca lança: falhas viram <see cref="PlaylistDashboardStartResult.Failure"/>.</summary>
    public PlaylistDashboardStartResult Start()
    {
        if (_disposed)
        {
            return PlaylistDashboardStartResult.Failure("o servidor já foi encerrado.");
        }

        if (_started)
        {
            return PlaylistDashboardStartResult.Success(_port);
        }

        try
        {
            _listener.Start();
        }
        catch (HttpListenerException ex)
        {
            return PlaylistDashboardStartResult.Failure(DescribeListenerError(ex));
        }
        catch (SocketException ex)
        {
            return PlaylistDashboardStartResult.Failure($"porta {_port} já está em uso por outro programa, ou inacessível ({ex.Message}).");
        }

        _started = true;
        _cts = new CancellationTokenSource();
        _loopTask = Task.Run(() => ListenLoopAsync(_cts.Token));
        return PlaylistDashboardStartResult.Success(_port);
    }

    private async Task ListenLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (Exception) when (token.IsCancellationRequested || _disposed)
            {
                return;
            }
            catch (Exception)
            {
                continue;
            }

            _ = Task.Run(() => ProcessRequestSafe(context), CancellationToken.None);
        }
    }

    private void ProcessRequestSafe(HttpListenerContext context)
    {
        try
        {
            ProcessRequest(context);
        }
        catch (Exception ex)
        {
            // Achado na Sprint 2 (2026-11-15), testando upload de VT: esta captura só fechava a
            // conexão sem escrever nada — o cliente via um 200 "OK" com corpo vazio (o StatusCode
            // padrão do HttpListenerResponse é 200), e o app.js quebrava com "Unexpected end of
            // JSON input" ao tentar `response.json()` numa resposta vazia — mensagem real do erro
            // completamente perdida, impossível de diagnosticar. Corrigido pra bater com o que a
            // documentação da classe já prometia ("vira resposta HTTP 500 com a mensagem"), usando
            // o mesmo WriteResponse de sempre.
            // Diagnóstico temporário da v1.0.85 (devolvia `ex.ToString()`, stack trace completo)
            // cumpriu seu papel: achou a causa raiz real (NullReferenceException em
            // PlaylistFileStore.CreateLauda, linha ~261, manifest.Laudas nulo após desserializar —
            // ver fix em PlaylistFileStore.ReadManifest, v1.0.86). Revertido pra só `ex.Message`
            // agora que a causa está corrigida — stack trace completo não precisa mais ir pra
            // produção o tempo todo.
            try
            {
                WriteResponse(context.Response, 500, "text/plain; charset=utf-8", $"Erro interno: {ex.Message}");
            }
            catch (Exception)
            {
                try
                {
                    context.Response.Close();
                }
                catch (Exception)
                {
                    // conexão já perdida — nada mais a fazer.
                }
            }
        }
    }

    private void ProcessRequest(HttpListenerContext context)
    {
        var request = context.Request;
        var response = context.Response;

        var path = NormalizePath(request.Url?.AbsolutePath ?? string.Empty);
        var routeKey = RouteKey(request.HttpMethod, path);

        // Login/logout são as ÚNICAS rotas públicas (ver comentário de classe) — resolvidas ANTES
        // de qualquer checagem de sessão, senão seria impossível logar/deslogar.
        PlaylistLoginHandler? loginHandler;
        bool isLogoutRoute;
        lock (_routesLock)
        {
            _loginRoutes.TryGetValue(routeKey, out loginHandler);
            isLogoutRoute = _logoutRoutes.Contains(routeKey);
        }

        if (loginHandler is not null)
        {
            ProcessLogin(loginHandler, request, response);
            return;
        }

        if (isLogoutRoute)
        {
            ProcessLogout(request, response);
            return;
        }

        PlaylistUploadHandler? uploadHandler;
        PlaylistApiHandler? apiHandler;
        lock (_routesLock)
        {
            _uploadRoutes.TryGetValue(routeKey, out uploadHandler);
            if (uploadHandler is null)
            {
                _apiRoutes.TryGetValue(routeKey, out apiHandler);
            }
            else
            {
                apiHandler = null;
            }
        }

        if (uploadHandler is not null || apiHandler is not null)
        {
            // Sprint 5 (2026-11-16): toda rota de API/upload (fora login/logout) agora exige sessão
            // válida — resolvida ANTES de chamar o handler, 401 sem nem tocar na lógica de
            // Programa/Edição/Lauda/Usuário se não houver sessão (ver comentário de classe).
            var user = TryResolveSession(request);
            if (user is null)
            {
                WriteResponse(response, 401, "text/plain; charset=utf-8", "não autenticado — faça login.");
                return;
            }

            if (uploadHandler is not null)
            {
                // Nunca passa pelo StreamReader/UTF-8 do bloco de baixo — leitura crua de bytes direto
                // do stream da requisição, ver comentário de PlaylistUploadHandler pro porquê.
                var uploadQuery = request.Url?.Query is { Length: > 0 } uq ? uq.TrimStart('?') : null;
                var uploadResponse = uploadHandler(uploadQuery, request.InputStream, user);
                WriteResponse(response, uploadResponse.StatusCode, uploadResponse.ContentType, uploadResponse.Body);
                return;
            }

            var query = request.Url?.Query is { Length: > 0 } q ? q.TrimStart('?') : null;
            string? body = null;
            if (string.Equals(request.HttpMethod, "POST", StringComparison.OrdinalIgnoreCase)
                || string.Equals(request.HttpMethod, "PUT", StringComparison.OrdinalIgnoreCase))
            {
                using var reader = new StreamReader(request.InputStream, Encoding.UTF8);
                body = reader.ReadToEnd();
            }

            var apiResponse = apiHandler!(query, body, user);
            WriteResponse(response, apiResponse.StatusCode, apiResponse.ContentType, apiResponse.Body);
            return;
        }

        // Sprint Controle-Remoto-TP, parte 2 (2026-11-17): mesmo gate de sessão das rotas de
        // API/upload acima, mas o "handler" aqui é só REGISTRAR a conexão — ver
        // PlaylistSseChannel.AddConnection. Deliberadamente NUNCA fecha `response` neste caminho
        // (nem com WriteResponse nem com ServeStaticFile): a conexão precisa ficar aberta
        // indefinidamente pra receber Broadcast()s futuros, bem diferente de toda outra rota desta
        // classe (que sempre devolve uma resposta completa e fecha).
        PlaylistSseChannel? sseChannel;
        lock (_routesLock)
        {
            _sseRoutes.TryGetValue(routeKey, out sseChannel);
        }

        if (sseChannel is not null)
        {
            var user = TryResolveSession(request);
            if (user is null)
            {
                WriteResponse(response, 401, "text/plain; charset=utf-8", "não autenticado — faça login.");
                return;
            }

            sseChannel.AddConnection(response);
            return;
        }

        if (!string.Equals(request.HttpMethod, "GET", StringComparison.OrdinalIgnoreCase))
        {
            WriteResponse(response, 405, "text/plain; charset=utf-8", "Método não permitido.");
            return;
        }

        ServeStaticFile(response, path);
    }

    /// <summary>Executa a tentativa de login e, se autenticou, cria a sessão nova e manda o cookie — ver comentário de classe.</summary>
    private void ProcessLogin(PlaylistLoginHandler handler, HttpListenerRequest request, HttpListenerResponse response)
    {
        using var reader = new StreamReader(request.InputStream, Encoding.UTF8);
        var body = reader.ReadToEnd();

        var attempt = handler(body);
        if (attempt.AuthenticatedUser is { } user)
        {
            var token = GenerateSessionToken();
            lock (_sessionsLock)
            {
                PruneExpiredSessions();
                _sessions[token] = new SessionEntry(user.Id, user.Username, user.IsAdmin, user.AllowedProgramIds, DateTime.UtcNow.Add(SessionLifetime));
            }

            AppendSetCookieHeader(response, token, SessionLifetime);
        }

        var result = attempt.Response;
        WriteResponse(response, result.StatusCode, result.ContentType, result.Body);
    }

    /// <summary>Limpa a sessão (se o cookie mandado bater com alguma) e manda um cookie já expirado de volta — nunca falha mesmo se não houver sessão nenhuma pra limpar.</summary>
    private void ProcessLogout(HttpListenerRequest request, HttpListenerResponse response)
    {
        var token = request.Cookies[SessionCookieName]?.Value;
        if (!string.IsNullOrEmpty(token))
        {
            lock (_sessionsLock)
            {
                _sessions.Remove(token);
            }
        }

        AppendSetCookieHeader(response, string.Empty, TimeSpan.Zero);
        WriteResponse(response, 200, "application/json; charset=utf-8", "{}");
    }

    /// <summary>Resolve o usuário da sessão a partir do cookie da requisição — nulo se não houver cookie, a sessão não existir, ou já tiver expirado (e, nesse caso, remove a sessão vencida — limpeza "de passagem", sem thread de fundo nenhuma).</summary>
    private AuthenticatedDashboardUser? TryResolveSession(HttpListenerRequest request)
    {
        var token = request.Cookies[SessionCookieName]?.Value;
        if (string.IsNullOrEmpty(token))
        {
            return null;
        }

        lock (_sessionsLock)
        {
            if (!_sessions.TryGetValue(token, out var session))
            {
                return null;
            }

            if (session.ExpiresAtUtc <= DateTime.UtcNow)
            {
                _sessions.Remove(token);
                return null;
            }

            return new AuthenticatedDashboardUser(session.UserId, session.Username, session.IsAdmin, session.AllowedProgramIds);
        }
    }

    /// <summary>Chamado sempre dentro de <see cref="_sessionsLock"/> — remove sessões já vencidas (ex.: a Cartucheira ficou aberta dias), evita o dicionário crescer sem limite.</summary>
    private void PruneExpiredSessions()
    {
        var now = DateTime.UtcNow;
        var expired = _sessions.Where(kv => kv.Value.ExpiresAtUtc <= now).Select(kv => kv.Key).ToList();
        foreach (var key in expired)
        {
            _sessions.Remove(key);
        }
    }

    /// <summary><paramref name="lifetime"/> zero expira o cookie imediatamente (logout) — <c>HttpOnly</c> sempre, sem <c>Secure</c> (ver comentário de classe pro porquê), <c>SameSite=Lax</c>.</summary>
    private static void AppendSetCookieHeader(HttpListenerResponse response, string token, TimeSpan lifetime)
    {
        var maxAgeSeconds = lifetime <= TimeSpan.Zero ? 0 : (int)lifetime.TotalSeconds;
        response.Headers.Add("Set-Cookie", $"{SessionCookieName}={token}; Path=/; HttpOnly; SameSite=Lax; Max-Age={maxAgeSeconds}");
    }

    private static string GenerateSessionToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        // Base64url (RFC 4648 §5) em vez de Base64 padrão — evita "+"/"/" no valor do cookie, que
        // alguns clientes/proxies tratam mal mesmo sendo tecnicamente permitidos.
        return Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_').TrimEnd('=');
    }

    /// <summary>Sessão guardada em memória (ver comentário de classe) — vocabulário primitivo, nunca um tipo de domínio.</summary>
    private sealed record SessionEntry(string UserId, string Username, bool IsAdmin, IReadOnlyList<string> AllowedProgramIds, DateTime ExpiresAtUtc);

    private void ServeStaticFile(HttpListenerResponse response, string path)
    {
        var relative = path == "/" ? "index.html" : path.TrimStart('/');

        // Nunca deixa ".." escapar da pasta configurada — mesmo cuidado básico de qualquer servidor
        // de arquivo estático, relevante aqui porque este servidor aceita conexão de outra máquina.
        var fullPath = Path.GetFullPath(Path.Combine(_webRootFolder, relative));
        var rootFullPath = Path.GetFullPath(_webRootFolder);
        if (!fullPath.StartsWith(rootFullPath, StringComparison.OrdinalIgnoreCase) || !File.Exists(fullPath))
        {
            WriteResponse(response, 404, "text/plain; charset=utf-8", "Arquivo não encontrado.");
            return;
        }

        var extension = Path.GetExtension(fullPath);
        var contentType = ContentTypesByExtension.GetValueOrDefault(extension, "application/octet-stream");

        try
        {
            var bytes = File.ReadAllBytes(fullPath);
            response.StatusCode = 200;
            response.ContentType = contentType;
            response.ContentLength64 = bytes.Length;
            response.OutputStream.Write(bytes, 0, bytes.Length);
        }
        finally
        {
            response.OutputStream.Close();
        }
    }

    private static void WriteResponse(HttpListenerResponse response, int statusCode, string contentType, string body)
    {
        try
        {
            response.StatusCode = statusCode;
            response.ContentType = contentType;
            var bytes = Encoding.UTF8.GetBytes(body);
            response.ContentLength64 = bytes.Length;
            response.OutputStream.Write(bytes, 0, bytes.Length);
        }
        finally
        {
            response.OutputStream.Close();
        }
    }

    private static string RouteKey(string method, string path) => $"{method.ToUpperInvariant()} {NormalizePath(path)}";

    private static string NormalizePath(string path)
    {
        path = path.Trim();
        if (path.Length == 0 || path == "/")
        {
            return "/";
        }

        if (!path.StartsWith('/'))
        {
            path = "/" + path;
        }

        return path.TrimEnd('/');
    }

    private string DescribeListenerError(HttpListenerException ex) => ex.ErrorCode switch
    {
        5 => $"acesso negado pelo Windows ao abrir a porta {_port} pra rede local. Rode a Cartucheira como Administrador uma vez, ou libere a URL executando (uma vez só, como Administrador): netsh http add urlacl url=http://+:{_port}/ user=<seu usuário do Windows>.",
        _ => $"erro do Windows ao abrir a porta {_port} (código {ex.ErrorCode}: {ex.Message})."
    };

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _cts?.Cancel();

        // Sprint Controle-Remoto-TP, parte 2 (2026-11-17): fecha toda conexão SSE aberta (ver
        // PlaylistSseChannel.Dispose) ANTES de parar o listener — mesmo sem isso o socket cairia
        // sozinho quando o listener fechar, mas fechar explicitamente evita um Broadcast() tardio
        // (ex.: TeleprompterViewModel.StateChanged disparando no exato instante da troca de porta)
        // tentar escrever numa conexão de um host que já era pra estar morto.
        List<PlaylistSseChannel> sseChannelsSnapshot;
        lock (_routesLock)
        {
            sseChannelsSnapshot = _sseRoutes.Values.ToList();
        }

        foreach (var channel in sseChannelsSnapshot)
        {
            try
            {
                channel.Dispose();
            }
            catch (Exception)
            {
                // best-effort — não trava o resto do Dispose por causa disso.
            }
        }

        try
        {
            _listener.Stop();
        }
        catch (Exception)
        {
            // já parado / nunca chegou a iniciar — sem problema.
        }

        try
        {
            _loopTask?.Wait(TimeSpan.FromSeconds(2));
        }
        catch (Exception)
        {
            // encerramento best-effort — não trava o Dispose por causa disso.
        }

        try
        {
            _listener.Close();
        }
        catch (Exception)
        {
            // idem.
        }

        _cts?.Dispose();
    }
}
