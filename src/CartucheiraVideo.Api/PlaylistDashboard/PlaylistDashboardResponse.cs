namespace CartucheiraVideo.Api.PlaylistDashboard;

/// <summary>Resposta de uma rota de API registrada em <see cref="PlaylistDashboardHost"/> — vocabulário HTTP puro (status/tipo/corpo), sem nenhum tipo de domínio.</summary>
public sealed record PlaylistDashboardResponse(int StatusCode, string ContentType, string Body)
{
    public static PlaylistDashboardResponse Json(string body, int statusCode = 200) => new(statusCode, "application/json; charset=utf-8", body);

    public static PlaylistDashboardResponse PlainTextError(int statusCode, string message) => new(statusCode, "text/plain; charset=utf-8", message);
}
