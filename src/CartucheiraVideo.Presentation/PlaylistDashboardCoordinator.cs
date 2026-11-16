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
/// Nunca lança: qualquer exceção ao processar uma rota vira resposta HTTP 500 com a mensagem, sem
/// derrubar o host nem o programa — mesma defesa de sempre neste projeto.
/// </summary>
public sealed class PlaylistDashboardCoordinator : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private readonly Action<string> _onStatus;
    private PlaylistDashboardHost? _host;

    public PlaylistDashboardCoordinator(Action<string> onStatus)
    {
        _onStatus = onStatus ?? throw new ArgumentNullException(nameof(onStatus));
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

        if (string.IsNullOrWhiteSpace(settings.RootFolder))
        {
            _onStatus("Playlist: pasta raiz não configurada — dashboard desligada (configure em Configurações → Playlist).");
            return;
        }

        var rootFolder = settings.RootFolder;
        var webRoot = Path.Combine(AppContext.BaseDirectory, "PlaylistDashboardWebRoot");
        var host = new PlaylistDashboardHost(settings.DashboardPort, webRoot);

        host.RegisterApiRoute("GET", "/api/playlist/programs", (_, _) => HandleListPrograms(rootFolder));
        host.RegisterApiRoute("POST", "/api/playlist/programs", (_, body) => HandleCreateProgram(rootFolder, body));
        host.RegisterApiRoute("GET", "/api/playlist/editions", (query, _) => HandleListEditions(rootFolder, query));
        host.RegisterApiRoute("POST", "/api/playlist/editions", (_, body) => HandleCreateEdition(rootFolder, body));
        host.RegisterApiRoute("GET", "/api/playlist/laudas", (query, _) => HandleListLaudas(rootFolder, query));
        host.RegisterApiRoute("POST", "/api/playlist/laudas", (_, body) => HandleCreateLauda(rootFolder, body));
        host.RegisterUploadRoute("PUT", "/api/playlist/laudas/vt", (query, stream) => HandleUploadFile(rootFolder, query, stream, PlaylistUploadKind.Video));
        host.RegisterUploadRoute("PUT", "/api/playlist/laudas/imagem", (query, stream) => HandleUploadFile(rootFolder, query, stream, PlaylistUploadKind.Image));

        var result = host.Start();
        if (result.Succeeded)
        {
            _host = host;
            _onStatus($"Playlist: dashboard disponível na rede local na porta {result.Port} (pasta raiz: {rootFolder}).");
        }
        else
        {
            host.Dispose();
            _onStatus($"Playlist: não consegui abrir a dashboard — {result.ErrorMessage}");
        }
    }

    private static PlaylistDashboardResponse HandleListPrograms(string rootFolder)
    {
        var (ok, error, programs) = PlaylistFileStore.ListPrograms(rootFolder);
        if (!ok)
        {
            return PlaylistDashboardResponse.PlainTextError(500, error ?? "erro desconhecido.");
        }

        var dtos = programs.Select(p => new ProgramDto(p.Id, p.Name)).ToList();
        return PlaylistDashboardResponse.Json(JsonSerializer.Serialize(dtos, JsonOptions));
    }

    private static PlaylistDashboardResponse HandleCreateProgram(string rootFolder, string? body)
    {
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

        var (ok, error, program) = PlaylistFileStore.CreateProgram(rootFolder, request.Name);
        if (!ok || program is null)
        {
            return PlaylistDashboardResponse.PlainTextError(500, error ?? "erro desconhecido.");
        }

        return PlaylistDashboardResponse.Json(JsonSerializer.Serialize(new ProgramDto(program.Id, program.Name), JsonOptions), 201);
    }

    private static PlaylistDashboardResponse HandleListEditions(string rootFolder, string? queryString)
    {
        var programId = ParseQueryValue(queryString, "programId");
        if (string.IsNullOrWhiteSpace(programId))
        {
            return PlaylistDashboardResponse.PlainTextError(400, "parâmetro \"programId\" é obrigatório.");
        }

        var (ok, error, editions) = PlaylistFileStore.ListEditions(rootFolder, programId);
        if (!ok)
        {
            return PlaylistDashboardResponse.PlainTextError(500, error ?? "erro desconhecido.");
        }

        var dtos = editions.Select(e => new EditionDto(e.Id, e.DateIso)).ToList();
        return PlaylistDashboardResponse.Json(JsonSerializer.Serialize(dtos, JsonOptions));
    }

    private static PlaylistDashboardResponse HandleCreateEdition(string rootFolder, string? body)
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
    private static PlaylistDashboardResponse HandleListLaudas(string rootFolder, string? queryString)
    {
        var programId = ParseQueryValue(queryString, "programId");
        var editionId = ParseQueryValue(queryString, "editionId");
        if (string.IsNullOrWhiteSpace(programId) || string.IsNullOrWhiteSpace(editionId))
        {
            return PlaylistDashboardResponse.PlainTextError(400, "parâmetros \"programId\" e \"editionId\" são obrigatórios.");
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
    private static PlaylistDashboardResponse HandleCreateLauda(string rootFolder, string? body)
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

    /// <summary>
    /// Sprint 2 (2026-11-15) — recebe o arquivo (VT ou imagem) direto no corpo cru da requisição PUT
    /// (ver comentário de <c>PlaylistDashboardHost.PlaylistUploadHandler</c> pro porquê de não ser
    /// multipart/form-data). Query esperada: <c>programId</c>, <c>editionId</c>, <c>laudaId</c>,
    /// <c>filename</c> (nome original do arquivo, só pra preservar a extensão — o nome final em disco
    /// pode ficar diferente se já existir um igual, ver <c>PlaylistFileStore.SaveUploadedFile</c>).
    /// </summary>
    private static PlaylistDashboardResponse HandleUploadFile(string rootFolder, string? queryString, Stream requestBodyStream, PlaylistUploadKind kind)
    {
        var programId = ParseQueryValue(queryString, "programId");
        var editionId = ParseQueryValue(queryString, "editionId");
        var laudaId = ParseQueryValue(queryString, "laudaId");
        var fileName = ParseQueryValue(queryString, "filename") ?? "arquivo";

        if (string.IsNullOrWhiteSpace(programId) || string.IsNullOrWhiteSpace(editionId) || string.IsNullOrWhiteSpace(laudaId))
        {
            return PlaylistDashboardResponse.PlainTextError(400, "parâmetros \"programId\", \"editionId\" e \"laudaId\" são obrigatórios.");
        }

        var (ok, error, savedFileName) = PlaylistFileStore.SaveUploadedFile(rootFolder, programId, editionId, laudaId, kind, fileName, requestBodyStream);
        if (!ok || savedFileName is null)
        {
            return PlaylistDashboardResponse.PlainTextError(400, error ?? "erro desconhecido.");
        }

        return PlaylistDashboardResponse.Json(JsonSerializer.Serialize(new UploadResultDto(savedFileName), JsonOptions));
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
        _host?.Dispose();
        _host = null;
    }

    private sealed record ProgramDto(string Id, string Name);

    private sealed record EditionDto(string Id, string DateIso);

    private sealed record CreateProgramRequest(string? Name);

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
}
