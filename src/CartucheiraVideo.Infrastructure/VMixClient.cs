using System;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace CartucheiraVideo.Infrastructure;

/// <summary>
/// Sprint vMix Controller (2026-11-14, v1.0.77): primeira implementação real de comando pro vMix —
/// arquitetura §13.1/§13.3/§14 ("vMix Controller (HTTP/TCP) — cortar pro PGM no canal NDI do Deck
/// ao dar Play, voltar pra câmera padrão ao terminar"), item do roteiro em status-implementacao.md
/// que ficava pendente desde a Sprint Config (2026-08-08) — os campos Host/Porta/Input da aba vMix
/// já existiam, salvando de verdade, só sem nenhum cliente de rede atrás.
///
/// Desvio DELIBERADO da pesquisa técnica original (arquitetura-sistema.md §13.3, que recomendava a
/// TCP API na porta 8099 com <c>SUBSCRIBE TALLY</c> pra automação "sem polling"): esta primeira
/// versão usa a **HTTP API (porta 8088)**, bem mais simples de implementar e com risco bem menor
/// (sem socket persistente, sem parser de protocolo por linha, sem lidar com reconexão) — adequada
/// porque o uso real daqui é só "pergunta pontual" (qual Input está ativo agora, uma vez, antes do
/// corte) e "comando pontual" (corte agora), nunca monitoramento contínuo. Se o usuário precisar de
/// confirmação por tally em tempo real no futuro, a TCP API fica como evolução natural — registrado
/// aqui pra não se perder a diferença em relação ao que a pesquisa original recomendava.
///
/// vMix não exige autenticação por padrão (ver mesma pesquisa) — nenhuma credencial é tratada aqui.
/// Toda chamada tem timeout curto (2s) e nunca lança: falha de rede vira <c>(false, mensagem)</c> ou
/// <c>null</c>, pro chamador (DeckViewModel) decidir o que logar, sem nunca travar o Deck por causa
/// de um vMix fora do ar ou host/porta errados.
/// </summary>
public static class VMixClient
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(2) };

    private static readonly Regex ActiveInputPattern = new("<active>(\\d+)</active>", RegexOptions.Compiled);

    /// <summary>
    /// Lê o estado atual do vMix (GET sem Function nenhuma — a API HTTP devolve um XML completo do
    /// estado) e extrai o número do Input ativo no PGM (tag &lt;active&gt;). Usado só pra capturar,
    /// no instante antes de um corte de automação, "o que estava no ar antes" — pra poder restaurar
    /// depois (ver <c>DeckViewModel.TriggerVMixOnPlay</c>). Retorna <c>null</c> em qualquer falha
    /// (vMix fora do ar, host/porta errados, resposta sem a tag esperada) — nunca lança.
    /// </summary>
    public static async Task<int?> GetActiveInputAsync(string host, string port)
    {
        try
        {
            var xml = await Http.GetStringAsync($"http://{host}:{port}/API/").ConfigureAwait(false);
            var match = ActiveInputPattern.Match(xml);
            return match.Success && int.TryParse(match.Groups[1].Value, out var activeInput) ? activeInput : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Corta o PGM do vMix direto pro <paramref name="input"/> informado (número ou nome cadastrado
    /// no vMix) — usa <c>CutDirect</c>/<c>FadeDirect</c> (não <c>Cut</c>/<c>Fade</c> simples) de
    /// propósito: as variantes "Direct" cortam o PGM sem alterar o que está no Preview do operador,
    /// essencial pra automação não bagunçar o que o operador de vMix está preparando manualmente ao
    /// mesmo tempo (mesmo cuidado documentado em arquitetura/pesquisa-ndi-switching.md). Corte seco
    /// por padrão (<paramref name="useFade"/> false); com Fade, <paramref name="durationMs"/>
    /// vira o parâmetro <c>Duration</c> do vMix. Vocabulário <c>bool</c> em vez do enum
    /// <c>TransitionType</c> do Core de propósito — este projeto não referencia o Core (mesmo
    /// princípio já seguido pelo <c>CartucheiraVideo.Ndi</c>), quem traduz é o chamador.
    /// </summary>
    public static async Task<(bool Ok, string? Error)> CutToInputAsync(string host, string port, string input, bool useFade, int durationMs)
    {
        try
        {
            var function = useFade ? "FadeDirect" : "CutDirect";
            var url = $"http://{host}:{port}/API/?Function={function}&Input={Uri.EscapeDataString(input)}";
            if (useFade)
            {
                url += $"&Duration={Math.Max(1, durationMs)}";
            }

            using var response = await Http.GetAsync(url).ConfigureAwait(false);
            return response.IsSuccessStatusCode ? (true, null) : (false, $"HTTP {(int)response.StatusCode}");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }
}
