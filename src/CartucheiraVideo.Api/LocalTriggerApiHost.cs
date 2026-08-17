using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace CartucheiraVideo.Api;

/// <summary>
/// Servidor HTTP local (só <c>localhost</c>, nunca exposto na rede) que recebe comando externo de
/// disparo de um Stream Deck físico (ação "Website"/"HTTP Request") ou do Bitfocus Companion —
/// Sprint Comando Externo (2026-08-10), arquitetura §13 ("Api: HTTP... pra Stream Deck/Companion").
/// Cada Cartucheira (Vinhetas/Trilhas) tem sua própria rota, registrada por
/// <see cref="RegisterRoute"/> — um <c>POST</c> nela dispara o mesmo <c>AudioBankViewModel.
/// TriggerExternalCommand</c> que o atalho de teclado global dispara (ver
/// <c>CartucheiraVideo.Infrastructure.GlobalHotkeyService</c>), então os dois canais têm exatamente
/// o mesmo efeito.
///
/// Sem autenticação de propósito — mesma decisão já tomada pros clientes HTTP/TCP de SAÍDA do vMix/
/// Resolume (arquitetura §13.3): rede local de estúdio, não exposta à internet; e este servidor
/// escuta SÓ em <c>localhost</c>, nunca em <c>0.0.0.0</c>/<c>+</c>/<c>*</c> — nem um Stream Deck em
/// outra máquina da mesma rede local consegue alcançá-lo, só processos rodando no mesmo computador
/// (ex.: o software do Stream Deck, ou o Bitfocus Companion, instalados na mesma máquina).
///
/// **Ponto técnico verificado por pesquisa antes de escrever este código** (mesmo rigor já aplicado
/// nas pesquisas de NDI/vMix/Resolume): a documentação oficial sobre reserva de URL do Http.sys (a
/// camada que o <see cref="HttpListener"/> usa no Windows) não deixa claro, de forma unânime entre
/// fontes, se um prefixo <c>http://localhost:porta/</c> específico (diferente de <c>http://+:porta/</c>
/// ou <c>http://*:porta/</c>) fica isento da exigência de <c>netsh http add urlacl</c> pra processos
/// sem privilégio de administrador — fontes encontradas se contradisseram, e nada aqui pôde ser
/// testado num Windows de verdade. Por isso <see cref="Start"/> nunca assume que vai funcionar: se o
/// Windows negar o registro da porta (<see cref="HttpListenerException"/>, ERROR_ACCESS_DENIED),
/// devolve uma falha honesta com a instrução exata de <c>netsh</c> pra resolver, em vez de travar/
/// derrubar o programa — o atalho de teclado (canal independente) continua funcionando normalmente
/// mesmo se este servidor não conseguir subir.
/// </summary>
public sealed class LocalTriggerApiHost : IDisposable
{
    /// <summary>
    /// Porta fixa do servidor — fonte única de verdade, consumida tanto por
    /// <c>MainViewModel</c> (constrói o host de verdade nesta porta) quanto por
    /// <c>CartridgeSettingsViewModel</c> (exibe a URL completa em Configurações), evitando o
    /// número mágico duplicado em dois projetos — Sprint Comando Externo, correção (2026-08-10).
    /// </summary>
    public const int DefaultPort = 9595;

    /// <summary>Rota da Cartucheira Vinhetas — mesma constante usada pelo <c>MainViewModel</c> (registra) e pelo <c>CartridgeSettingsViewModel</c> (exibe).</summary>
    public const string VinhetasRoutePath = "/cartucheira/vinhetas/trigger";

    /// <summary>Rota da Cartucheira Trilhas.</summary>
    public const string TrilhasRoutePath = "/cartucheira/trilhas/trigger";

    private readonly HttpListener _listener = new();
    private readonly Dictionary<string, Func<bool>> _routes = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _routesLock = new();
    private readonly int _port;
    private CancellationTokenSource? _cts;
    private Task? _loopTask;
    private volatile bool _disposed;
    private volatile bool _started;

    public LocalTriggerApiHost(int port)
    {
        _port = port;
        _listener.Prefixes.Add($"http://localhost:{port}/");
    }

    public int Port => _port;

    /// <summary>Base da URL que o operador cola na ação "Website"/"HTTP Request" do Stream Deck, ou no Companion — ex.: <c>http://localhost:9595</c>.</summary>
    public string BaseUrl => $"http://localhost:{_port}";

    /// <summary>
    /// Registra a rota que um <c>POST</c> precisa bater pra disparar o comando externo de uma
    /// Cartucheira — chamado uma vez por Cartucheira na construção do <c>MainViewModel</c>.
    /// <paramref name="onTriggered"/> deve devolver falso quando a Cartucheira estiver com o comando
    /// externo desabilitado em Configurações (o host responde 403 honesto em vez de fingir sucesso),
    /// verdadeiro quando disparou de verdade.
    /// </summary>
    public void RegisterRoute(string path, Func<bool> onTriggered)
    {
        ArgumentNullException.ThrowIfNull(onTriggered);

        lock (_routesLock)
        {
            _routes[NormalizePath(path)] = onTriggered;
        }
    }

    /// <summary>Abre a porta e começa a escutar — chamado uma vez, na construção do MainViewModel. Nunca lança: falhas viram <see cref="LocalTriggerApiStartResult.Failure"/>.</summary>
    public LocalTriggerApiStartResult Start()
    {
        if (_disposed)
        {
            return LocalTriggerApiStartResult.Failure("o servidor já foi encerrado.");
        }

        if (_started)
        {
            return LocalTriggerApiStartResult.Success(_port);
        }

        try
        {
            _listener.Start();
        }
        catch (HttpListenerException ex)
        {
            return LocalTriggerApiStartResult.Failure(DescribeListenerError(ex));
        }
        catch (SocketException ex)
        {
            return LocalTriggerApiStartResult.Failure($"porta {_port} já está em uso por outro programa, ou inacessível ({ex.Message}).");
        }

        _started = true;
        _cts = new CancellationTokenSource();
        _loopTask = Task.Run(() => ListenLoopAsync(_cts.Token));
        return LocalTriggerApiStartResult.Success(_port);
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
                return; // Dispose()/Stop() já foi chamado — encerramento normal, não é erro.
            }
            catch (Exception)
            {
                // Erro processando UMA conexão (ex.: cliente encerrou no meio) — não derruba o
                // servidor inteiro por causa de um cliente malformado; segue pra próxima.
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
            // Nunca deixa uma exceção ao responder UMA requisição matar a thread que processa as
            // próximas — mesma classe de defesa já usada no tratamento de erro do LibVLC
            // (AudioBankViewModel.ErrorOccurred).
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

        if (!string.Equals(request.HttpMethod, "POST", StringComparison.OrdinalIgnoreCase))
        {
            WriteResponse(response, 405, "Método não permitido — use POST.");
            return;
        }

        var path = NormalizePath(request.Url?.AbsolutePath ?? string.Empty);

        Func<bool>? handler;
        lock (_routesLock)
        {
            _routes.TryGetValue(path, out handler);
        }

        if (handler is null)
        {
            WriteResponse(response, 404, "Rota desconhecida.");
            return;
        }

        var handled = handler();
        WriteResponse(response, handled ? 200 : 403, handled ? "OK" : "Comando externo desabilitado em Configurações para esta Cartucheira.");
    }

    private static void WriteResponse(HttpListenerResponse response, int statusCode, string body)
    {
        try
        {
            response.StatusCode = statusCode;
            response.ContentType = "text/plain; charset=utf-8";
            var bytes = Encoding.UTF8.GetBytes(body);
            response.ContentLength64 = bytes.Length;
            response.OutputStream.Write(bytes, 0, bytes.Length);
        }
        finally
        {
            response.OutputStream.Close();
        }
    }

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
        5 => $"acesso negado pelo Windows ao abrir a porta {_port}. Rode o programa como Administrador uma vez, ou libere a URL executando (uma vez só, como Administrador): netsh http add urlacl url=http://localhost:{_port}/ user=<seu usuário do Windows>.",
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
