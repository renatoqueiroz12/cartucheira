using System.Text.Json;
using System.Text.Json.Nodes;

namespace CartucheiraVideo.Ndi;

/// <summary>
/// Sprint NDI-Placa-de-Rede (2026-09-13): restringe a QUAL placa de rede o NDI transmite/recebe —
/// pedido do usuário depois de dedicar uma placa só pro Livewire ("o NDI vai precisar ter o
/// tratamento de qual placa de rede ele vai transmitir, pois agora ele está transmitindo pela placa
/// do Livewire, e não pode").
///
/// A SDK do NDI usada aqui (ver <see cref="NdiInterop"/>) NÃO tem nenhum parâmetro de IP/placa na
/// criação do transmissor (<c>NDIlib_send_create_t</c> só tem nome do canal e as duas flags de
/// clock) — não existe "escolher a placa" por chamada de código. O mecanismo real, documentado pelo
/// fabricante, é um arquivo do PRÓPRIO WINDOWS que vale pra máquina inteira (todo programa NDI que
/// rodar nela, não só a Cartucheira): <c>C:\ProgramData\NDI\ndi-config.v1.json</c>, campo
/// <c>ndi.adapters.allowed</c> (lista de IPv4). Fontes: docs.ndi.video/all/developing-with-ndi/sdk/
/// configuration-files e support.medialooks.com (artigo "Use a specific network adapter"), ambas
/// consultadas nesta sprint.
///
/// Por isso esta classe NÃO fala com a SDK nativa da NDI (diferente de <see cref="NdiSender"/>) — ela
/// só lê/mescla/grava esse arquivo JSON do Windows, preservando QUALQUER outra chave que já esteja
/// nele (vendor, machine name, groups, sourcefilter, etc. — ver documentação) pra não quebrar
/// configuração de outro software NDI que porventura rode na mesma máquina. Mexe só na chave
/// <c>ndi.adapters.allowed</c>.
///
/// Efeito colateral aceito, avisado na tela de Configurações: isto vale pra QUALQUER programa NDI da
/// máquina, não só a Cartucheira — e não há confirmação de que a SDK relê esse arquivo com o
/// programa já aberto, então o mais seguro é orientar a reiniciar todo programa NDI depois de mudar.
/// </summary>
public static class NdiNetworkAdapterConfig
{
    private const string NodeKey = "ndi";
    private const string AdaptersKey = "adapters";
    private const string AllowedKey = "allowed";

    /// <summary>
    /// Caminho do arquivo de configuração do NDI no Windows — <c>%ProgramData%\NDI\ndi-config.v1.json</c>.
    /// <see cref="Environment.SpecialFolder.CommonApplicationData"/> resolve pra <c>C:\ProgramData</c>
    /// em qualquer instalação padrão do Windows, sem precisar hardcodar a letra do disco.
    /// </summary>
    public static string ConfigFilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "NDI",
        "ndi-config.v1.json");

    /// <summary>
    /// Aplica (ou remove) a restrição de placa de rede do NDI nesta máquina. <paramref name="ipv4"/>
    /// nulo/vazio REMOVE a restrição (some com <c>ndi.adapters.allowed</c>, devolvendo o NDI ao
    /// comportamento padrão de escolher sozinho); um IP grava uma lista de um elemento só, com ele.
    /// Nunca lança — devolve <c>(false, motivo)</c> em qualquer falha (arquivo/pasta sem permissão de
    /// escrita, JSON pré-existente corrompido que não dá pra interpretar com segurança, etc.), pra
    /// quem chama decidir o que mostrar ao operador sem derrubar a tela de Configurações.
    /// </summary>
    public static (bool Ok, string? Error) ApplyAllowedAdapter(string? ipv4)
    {
        try
        {
            var path = ConfigFilePath;
            var directory = Path.GetDirectoryName(path)!;

            JsonObject root;
            if (File.Exists(path))
            {
                var existingText = File.ReadAllText(path);
                if (string.IsNullOrWhiteSpace(existingText))
                {
                    root = new JsonObject();
                }
                else
                {
                    var parsed = JsonNode.Parse(existingText, documentOptions: new JsonDocumentOptions
                    {
                        CommentHandling = JsonCommentHandling.Skip,
                        AllowTrailingCommas = true,
                    });

                    if (parsed is not JsonObject parsedObject)
                    {
                        return (false, $"o arquivo {path} já existe mas não é um objeto JSON válido — não mexi nele pra não arriscar apagar configuração de outro programa NDI");
                    }

                    root = parsedObject;
                }
            }
            else
            {
                root = new JsonObject();
            }

            if (root[NodeKey] is not JsonObject ndiNode)
            {
                ndiNode = new JsonObject();
                root[NodeKey] = ndiNode;
            }

            if (string.IsNullOrWhiteSpace(ipv4))
            {
                // "Deixar o sistema escolher" — remove só a lista, deixa o resto (se "adapters"
                // tiver outras chaves além de "allowed" no futuro, elas sobrevivem).
                if (ndiNode[AdaptersKey] is JsonObject existingAdapters)
                {
                    existingAdapters.Remove(AllowedKey);
                }
            }
            else
            {
                if (ndiNode[AdaptersKey] is not JsonObject adapters)
                {
                    adapters = new JsonObject();
                    ndiNode[AdaptersKey] = adapters;
                }

                adapters[AllowedKey] = new JsonArray(JsonValue.Create(ipv4));
            }

            Directory.CreateDirectory(directory);

            // Grava num arquivo temporário e troca por Move — nunca deixa o arquivo compartilhado
            // (lido por QUALQUER programa NDI da máquina, não só a Cartucheira) pela metade se o
            // processo cair no meio da escrita.
            var tempPath = path + ".tmp";
            File.WriteAllText(tempPath, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tempPath, path, overwrite: true);

            return (true, null);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }
}
