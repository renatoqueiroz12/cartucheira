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
}
