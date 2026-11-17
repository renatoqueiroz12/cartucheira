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
///
/// Sprint GC (2026-11-17): duas funções novas (<see cref="SetTitleTextAsync"/>/
/// <see cref="SetOverlayVisibleAsync"/>) pro módulo GC — pesquisadas antes de implementar na mesma
/// Referência oficial já citada acima pro <c>CutDirect</c>/<c>Fade</c>, mesma disciplina de sempre.
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
    /// Corta (ou funde) o PGM do vMix pro <paramref name="input"/> informado (número ou nome
    /// cadastrado no vMix).
    ///
    /// Fix v1.0.78 (2026-11-14): a v1.0.77 original usava "FadeDirect" pro caso de Fade — função que
    /// NÃO EXISTE na API do vMix (conferido na Referência de Funções oficial, img.vmix.com/help/
    /// ShortcutFunctionReference.html — só existe "CutDirect", sem equivalente "Direct" pra Fade).
    /// Teria sido ignorada pelo vMix em silêncio (a API responde 200 OK mesmo pra Function
    /// desconhecida), reportando sucesso falso no Logs sem cortar nada de verdade. Corrigido ANTES
    /// do usuário ter chance de testar Fade (vMix dele ainda nem estava configurado).
    ///
    /// Corte seco (<paramref name="useFade"/> false) usa <c>CutDirect</c> — corta o PGM SEM alterar
    /// o Preview do operador (confirmado na mesma Referência: "Cuts the input directly to Output
    /// without changing Preview"). Continua a opção recomendada pra automação, por isso é o padrão
    /// quando nenhuma ação é cadastrada na aba Automação.
    ///
    /// Fade (<paramref name="useFade"/> true) usa a função genérica <c>Fade</c> com o parâmetro
    /// Input — NÃO existe variante "Direct" pra fade na API do vMix, então, diferente do Cut, isto
    /// PODE alterar o que está selecionado no Preview do operador (a função seleciona o Input no
    /// Preview antes de fazer a transição, mesmo comportamento documentado pros botões de transição
    /// da própria interface do vMix). Por isso o Corte seco continua sendo o padrão recomendado —
    /// Fade fica disponível pra quem aceitar esse efeito colateral.
    /// </summary>
    public static async Task<(bool Ok, string? Error)> CutToInputAsync(string host, string port, string input, bool useFade, int durationMs)
    {
        try
        {
            var url = useFade
                ? $"http://{host}:{port}/API/?Function=Fade&Input={Uri.EscapeDataString(input)}&Duration={Math.Max(1, durationMs)}"
                : $"http://{host}:{port}/API/?Function=CutDirect&Input={Uri.EscapeDataString(input)}";

            using var response = await Http.GetAsync(url).ConfigureAwait(false);
            return response.IsSuccessStatusCode ? (true, null) : (false, $"HTTP {(int)response.StatusCode}");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    /// <summary>
    /// Sprint GC (2026-11-17) — muda o VALOR de um campo de texto nomeado dentro de um Título (GT) do
    /// vMix, sem tirar/colocar nada no ar (ver <see cref="SetOverlayVisibleAsync"/> pra isso). Função
    /// confirmada na Referência oficial (img.vmix.com/help/ShortcutFunctionReference.html, mesma
    /// fonte já citada em <see cref="CutToInputAsync"/>): <c>Function=SetText</c>, documentada como
    /// "Change Text in Title according to Value parameter" — <c>SelectedName</c> escolhe QUAL campo
    /// de texto do Título muda (precisa casar com o nome real cadastrado no Título, dentro do vMix do
    /// usuário; esta camada nunca valida isso, só manda a chamada). Chamada uma vez POR CAMPO — um
    /// GC com "Nome"+"Cargo", por exemplo, precisa de duas chamadas (ver
    /// <c>Presentation.ViewModels.GcViewModel.SendFieldsAsync</c>, que itera o dicionário).
    /// </summary>
    public static async Task<(bool Ok, string? Error)> SetTitleTextAsync(string host, string port, string input, string fieldName, string value)
    {
        try
        {
            var url = $"http://{host}:{port}/API/?Function=SetText&Input={Uri.EscapeDataString(input)}&SelectedName={Uri.EscapeDataString(fieldName)}&Value={Uri.EscapeDataString(value)}";
            using var response = await Http.GetAsync(url).ConfigureAwait(false);
            return response.IsSuccessStatusCode ? (true, null) : (false, $"HTTP {(int)response.StatusCode}");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    /// <summary>
    /// Sprint GC (2026-11-17) — coloca ou tira o Título do GC de um canal de Overlay do vMix (1 a 8),
    /// SEM afetar o PGM normal dos Decks (Overlay é uma camada independente, por cima — mesmo
    /// conceito de "lower third" de qualquer regie de TV). Funções confirmadas na Referência oficial:
    /// <c>Function=OverlayInput{canal}In&amp;Input={título}</c> ("Transition In to Overlay{canal}
    /// with selected Input") pra mostrar, <c>Function=OverlayInput{canal}Out</c> (sem parâmetro de
    /// Input — a documentação não lista <c>SelectedName</c>/<c>Input</c> pra esta função, ela só
    /// tira o canal de overlay inteiro) pra esconder. <paramref name="overlayChannel"/> é fixado em 1
    /// a 8 (faixa real documentada); fora disso é corrigido pro limite mais próximo, nunca lança.
    /// </summary>
    public static async Task<(bool Ok, string? Error)> SetOverlayVisibleAsync(string host, string port, int overlayChannel, string input, bool visible)
    {
        try
        {
            var channel = Math.Clamp(overlayChannel, 1, 8);
            var url = visible
                ? $"http://{host}:{port}/API/?Function=OverlayInput{channel}In&Input={Uri.EscapeDataString(input)}"
                : $"http://{host}:{port}/API/?Function=OverlayInput{channel}Out";

            using var response = await Http.GetAsync(url).ConfigureAwait(false);
            return response.IsSuccessStatusCode ? (true, null) : (false, $"HTTP {(int)response.StatusCode}");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }
}
