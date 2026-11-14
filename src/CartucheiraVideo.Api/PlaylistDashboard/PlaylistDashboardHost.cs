using System.Net;
using System.Net.Sockets;
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
/// Presentation) e, sem nenhuma autenticação nesta v1 (decisão do usuário: dashboard só pra rede
/// local do estúdio, mesmo princípio já aceito pro vMix/NDI/Resolume) — qualquer um na mesma rede
/// consegue ler/criar Programas e Datas. Risco aceito nesta sprint, registrado pra se alguém revisitar
/// mais pra frente.
///
/// Vocabulário 100% HTTP puro, mesmo princípio de isolamento do <c>LocalTriggerApiHost</c>: nenhuma
/// referência a <c>Core</c>/<c>Infrastructure</c>/<c>Configuration</c> — as rotas de API são só
/// "método+caminho → função que recebe query string e corpo cru, devolve status+corpo" (ver
/// <see cref="PlaylistApiHandler"/>); quem liga uma rota a lógica de verdade (ler/criar
/// Programa/Edição em disco) é a Presentation (<c>PlaylistDashboardCoordinator</c>).
///
/// Nunca lança: erro processando UMA requisição nunca derruba o servidor inteiro, mesma defesa já
/// usada pelo <see cref="LocalTriggerApiHost"/>.
/// </summary>
public sealed class PlaylistDashboardHost : IDisposable
{
    /// <summary>Recebe a query string crua (sem o "?") e o corpo cru da requisição (nulo em GET) — devolve a resposta HTTP completa.</summary>
    public delegate PlaylistDashboardResponse PlaylistApiHandler(string? queryString, string? requestBody);

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
    private readonly object _routesLock = new();
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

    private void ProcessRequest(HttpListenerContext context)
    {
        var request = context.Request;
        var response = context.Response;

        var path = NormalizePath(request.Url?.AbsolutePath ?? string.Empty);

        PlaylistApiHandler? apiHandler;
        lock (_routesLock)
        {
            _apiRoutes.TryGetValue(RouteKey(request.HttpMethod, path), out apiHandler);
        }

        if (apiHandler is not null)
        {
            var query = request.Url?.Query is { Length: > 0 } q ? q.TrimStart('?') : null;
            string? body = null;
            if (string.Equals(request.HttpMethod, "POST", StringComparison.OrdinalIgnoreCase)
                || string.Equals(request.HttpMethod, "PUT", StringComparison.OrdinalIgnoreCase))
            {
                using var reader = new StreamReader(request.InputStream, Encoding.UTF8);
                body = reader.ReadToEnd();
            }

            var apiResponse = apiHandler(query, body);
            WriteResponse(response, apiResponse.StatusCode, apiResponse.ContentType, apiResponse.Body);
            return;
        }

        if (!string.Equals(request.HttpMethod, "GET", StringComparison.OrdinalIgnoreCase))
        {
            WriteResponse(response, 405, "text/plain; charset=utf-8", "Método não permitido.");
            return;
        }

        ServeStaticFile(response, path);
    }

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
