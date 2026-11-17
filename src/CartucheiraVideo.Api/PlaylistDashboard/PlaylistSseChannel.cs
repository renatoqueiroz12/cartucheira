using System.Net;
using System.Text;

namespace CartucheiraVideo.Api.PlaylistDashboard;

/// <summary>
/// Canal de "Server-Sent Events" (SSE) de uma rota registrada via
/// <see cref="PlaylistDashboardHost.RegisterSseRoute"/> — Sprint Controle-Remoto-TP, parte 2
/// (2026-11-17, pedido do usuário "2 segundos e muito, precisamos pensar outra alternativa" ao
/// polling de 2s da v1.0.95: "qd esta na cartucheira o texto esta indo direto para tela
/// praticamente em tempo real", ou seja, o produtor quer a dashboard remota tão instantânea quanto
/// o controle local). Em vez da dashboard web PERGUNTAR de novo e de novo ("mudou?"), o servidor
/// AVISA na hora que algo muda (<see cref="Broadcast"/>) — zero atraso de intervalo, só a latência
/// real da rede.
///
/// Cada conexão é um <see cref="HttpListenerResponse"/> mantido deliberadamente ABERTO (nunca
/// fechado por quem chama <see cref="PlaylistDashboardHost.RegisterSseRoute"/> nem por
/// <see cref="PlaylistDashboardHost.ProcessRequest"/> depois de registrar — ver comentário lá) —
/// diferente de toda outra rota desta dashboard, que sempre devolve uma resposta completa e fecha.
/// <see cref="HttpListenerResponse.SendChunked"/> ligado substitui o <c>Content-Length</c> (que não
/// dá pra saber de antemão, a conexão fica escrevendo indefinidamente) por "transfer-encoding:
/// chunked" — cada <see cref="Broadcast"/> escreve e dá <c>Flush()</c>, HttpListener transmite esse
/// pedaço imediatamente pro navegador.
///
/// Thread-safety: <see cref="Broadcast"/> pode ser chamado de QUALQUER thread (no caso de uso real,
/// de dentro de <c>TeleprompterViewModel.StateChanged</c>, disparado às vezes da UI thread via
/// <c>Dispatcher.Invoke</c>) — a lista de conexões é protegida por <see cref="_lock"/>, mesmo
/// princípio de <c>PlaylistDashboardHost._sessionsLock</c>.
///
/// Detecção de desconexão: SSE/HTTP puro não tem um evento nativo de "o navegador fechou a aba" que
/// o servidor receba de propósito ANTES de tentar escrever — só descobrimos na tentativa de escrita
/// seguinte, que lança (<c>IOException</c>/<c>HttpListenerException</c>/<c>ObjectDisposedException</c>).
/// Por isso <see cref="Broadcast"/> remove e fecha qualquer conexão que falhar ao escrever, e um
/// "ping" periódico (<see cref="HeartbeatInterval"/>) garante que conexões mortas sejam detectadas e
/// limpas mesmo em longos períodos sem nenhum evento real pra mandar (ex.: ninguém mexeu no
/// Teleprompter por horas) — sem isso, a lista de conexões só cresceria com abas fechadas que nunca
/// mandaram um TCP FIN limpo (comum atrás de alguns roteadores/NATs).
/// </summary>
public sealed class PlaylistSseChannel : IDisposable
{
    /// <summary>
    /// 20s — bem abaixo de qualquer timeout de NAT/roteador doméstico comum (a maioria usa 30-60s+
    /// pra conexões TCP ociosas), e frequente o suficiente pra detectar uma aba fechada sem gerar
    /// tráfego perceptível numa rede de estúdio. Comentário SSE puro (linha começando com ":"),
    /// ignorado pelo <c>EventSource</c> do navegador — não dispara nenhum listener de evento no
    /// app.js, só mantém a conexão viva e serve de sonda de "ainda está aberta?".
    /// </summary>
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(20);

    private readonly List<HttpListenerResponse> _connections = new();
    private readonly object _lock = new();
    private readonly Timer _heartbeatTimer;
    private bool _disposed;

    public PlaylistSseChannel()
    {
        _heartbeatTimer = new Timer(_ => BroadcastRaw(": ping\n\n"), null, HeartbeatInterval, HeartbeatInterval);
    }

    /// <summary>
    /// Chamado por <see cref="PlaylistDashboardHost.ProcessRequest"/> quando uma requisição GET bate
    /// numa rota registrada via <see cref="PlaylistDashboardHost.RegisterSseRoute"/> (depois da
    /// sessão já verificada — mesmo gate de qualquer outra rota protegida). Configura os cabeçalhos
    /// de streaming e guarda a conexão pra receber <see cref="Broadcast"/>s futuros — nunca fecha
    /// <paramref name="response"/> aqui, de propósito (ver comentário de classe).
    /// </summary>
    internal void AddConnection(HttpListenerResponse response)
    {
        response.StatusCode = 200;
        response.ContentType = "text/event-stream; charset=utf-8";
        response.Headers.Add("Cache-Control", "no-cache");
        response.SendChunked = true;
        response.KeepAlive = true;

        lock (_lock)
        {
            if (_disposed)
            {
                TryClose(response);
                return;
            }

            _connections.Add(response);
        }

        // Primeiro byte logo de cara — alguns navegadores/proxies só consideram o EventSource
        // "aberto" (dispara onopen) depois de receber algo, mesmo que seja só um comentário.
        TryWrite(response, ": conectado\n\n");
    }

    /// <summary>
    /// Manda um evento SSE (<c>event: {eventName}</c> + <c>data: {data}</c>) pra TODAS as conexões
    /// abertas agora. <paramref name="data"/> precisa ser uma única linha (sem <c>\n</c> literal) —
    /// quem chama normalmente passa um JSON compacto (<c>JsonSerializer.Serialize</c> sem
    /// indentação), que nunca quebra linha por padrão.
    /// </summary>
    public void Broadcast(string eventName, string data) => BroadcastRaw($"event: {eventName}\ndata: {data}\n\n");

    private void BroadcastRaw(string payload)
    {
        List<HttpListenerResponse> snapshot;
        lock (_lock)
        {
            if (_connections.Count == 0)
            {
                return;
            }

            snapshot = new List<HttpListenerResponse>(_connections);
        }

        List<HttpListenerResponse>? dead = null;
        foreach (var response in snapshot)
        {
            if (!TryWrite(response, payload))
            {
                (dead ??= new List<HttpListenerResponse>()).Add(response);
            }
        }

        if (dead is null)
        {
            return;
        }

        lock (_lock)
        {
            foreach (var response in dead)
            {
                _connections.Remove(response);
            }
        }

        foreach (var response in dead)
        {
            TryClose(response);
        }
    }

    /// <summary>Nunca lança (mesma defesa de sempre deste projeto) — devolve falso se a escrita falhar, sinal de que a conexão do outro lado já morreu (aba fechada, rede caiu etc.).</summary>
    private static bool TryWrite(HttpListenerResponse response, string payload)
    {
        try
        {
            var bytes = Encoding.UTF8.GetBytes(payload);
            response.OutputStream.Write(bytes, 0, bytes.Length);
            response.OutputStream.Flush();
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static void TryClose(HttpListenerResponse response)
    {
        try
        {
            response.OutputStream.Close();
        }
        catch (Exception)
        {
            // já fechada/conexão perdida — nada mais a fazer.
        }
    }

    /// <summary>Para o heartbeat e fecha toda conexão aberta — chamado por <see cref="PlaylistDashboardHost.Dispose"/> (dashboard desligando/reconfigurando) e nunca mais aceita conexão nova depois disso (ver checagem de <see cref="_disposed"/> em <see cref="AddConnection"/>).</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _heartbeatTimer.Dispose();

        List<HttpListenerResponse> snapshot;
        lock (_lock)
        {
            snapshot = new List<HttpListenerResponse>(_connections);
            _connections.Clear();
        }

        foreach (var response in snapshot)
        {
            TryClose(response);
        }
    }
}
