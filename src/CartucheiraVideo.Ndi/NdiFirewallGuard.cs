using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;

namespace CartucheiraVideo.Ndi;

/// <summary>
/// Sprint NDI-Firewall-Livewire (2026-09-13): reforço por Firewall do Windows pra garantir que o
/// NDI da Cartucheira nunca saia pela placa de rede dedicada ao Livewire — pedido depois que o
/// usuário confirmou, com testes reais (reboot completo, "Preferred NIC" já certo no NDI Access
/// Manager, receptor reconectado do zero), que a config oficial do NDI
/// (<see cref="NdiNetworkAdapterConfig"/>, Sprint NDI-Placa-de-Rede/v1.0.22) sozinha NÃO impediu o
/// NDI de usar a placa errada nesta máquina ("tiro o cabo da placa livewire, o pc para de receber o
/// vídeo — está pela rede livewire").
///
/// **Fix pós-entrega da v1.0.23 (v1.0.24, mesmo dia):** a primeira versão desta classe só bloqueava
/// SAÍDA (<c>dir=out</c>) de TCP — e não segurou nada. Causa encontrada com o usuário via
/// <c>netstat -ano</c>: a conexão de vídeo/áudio do NDI (porta TCP 5961 no caso testado) é uma
/// conexão de ENTRADA do ponto de vista da Cartucheira — é o RECEPTOR que conecta NELA, não o
/// contrário (endereço local fixo, porta remota variando a cada conexão, a assinatura clássica de
/// quem está sendo procurado). O Windows Firewall tem estado (stateful): uma vez que uma conexão de
/// ENTRADA é aceita, as respostas dela saem livres, MESMO com uma regra de saída bloqueando aquele
/// IP — por isso a regra de saída sozinha nunca chegou a barrar o handshake. Corrigido adicionando
/// também uma regra de ENTRADA (<c>dir=in</c>) com os mesmos critérios, que impede a conexão de
/// sequer ser aceita. De brinde, bloqueia nos dois sentidos a porta UDP 5353 (mDNS, o mecanismo de
/// descoberta do NDI — visto ativo nessa mesma placa no <c>netstat</c> do teste), pra evitar que a
/// Cartucheira nem se anuncie disponível naquela rede — porta fixa e bem conhecida, bem diferente
/// das portas do Livewire (4001/5004), sem risco de conflito.
///
/// Por que bloquear por PROTOCOLO/PORTA fixos (TCP qualquer porta + UDP 5353), nunca por uma faixa
/// de porta adivinhada: o transporte de vídeo/áudio de verdade do NDI é sempre TCP (confirmado nos
/// dois testes desta máquina, incluindo o que revelou o bug acima), e a descoberta é sempre mDNS
/// (porta 5353, padrão IANA, não é algo que o NDI escolhe). Já o Livewire da própria Cartucheira
/// (<c>CartucheiraVideo.Infrastructure.Livewire.LivewireSender</c>/<c>LivewireAdvertiser</c>) é 100%
/// UDP nas portas 4001 (anúncio multicast ADVT) e 5004 (RTP/L24) — nunca abre TCP nenhum, e nunca usa
/// 5353. Isso permite bloquear o NDI (TCP inteiro + a porta 5353 específica) sem NENHUM risco de
/// afetar o Livewire, e sem depender de faixa de porta nenhuma que o NDI possa mudar entre versões.
///
/// Isso é DIFERENTE do <see cref="NdiNetworkAdapterConfig"/> em dois jeitos importantes: (1) aqui o
/// alvo é só o processo da PRÓPRIA Cartucheira (identificado pelo caminho do .exe em execução) — não
/// a máquina inteira, outros programas NDI continuam livres; (2) aqui é uma restrição de verdade,
/// imposta pelo próprio Windows por fora do NDI, não um "pedido" que o NDI pode ignorar. As duas
/// camadas convivem — esta é o reforço, não substitui a outra.
///
/// Exige permissão de Administrador (o Firewall do Windows não deixa usuário comum alterar regras).
/// Quem chama <see cref="ApplyBlockRule"/> deve fazer isso só quando o valor realmente mudou desde a
/// última vez que funcionou (ver
/// <c>CartucheiraVideo.Configuration.AppSettingsSnapshot.NdiFirewallGuardAppliedForLivewireIp</c> e,
/// desde o fix da v1.0.25, também <see cref="RuleSchemaVersion"/>/
/// <c>AppSettingsSnapshot.NdiFirewallGuardAppliedRuleSchemaVersion</c>, ambos persistidos em disco),
/// pra não pedir a janela de permissão (UAC) toda vez que a Cartucheira abre à toa — essa classe em
/// si não guarda esse controle, é só o mecanismo de aplicar/remover as regras.
///
/// **Fix pós-entrega da v1.0.24 (v1.0.25, mesmo dia):** ver o comentário de <see cref="RuleSchemaVersion"/>
/// pro bug encontrado (a v1.0.24 nunca chegou a rodar na máquina do usuário, porque a memória
/// persistida só considerava o IP, e ele não tinha mudado) e a correção.
///
/// **Fix pós-entrega das v1.0.24/v1.0.25/v1.0.26 (v1.0.27, mesmo dia) — A CAUSA RAIZ DE VERDADE,
/// achada só depois de diagnóstico ao vivo na máquina do usuário:** as três versões anteriores
/// corrigiram problemas REAIS (direção da regra, lógica de reaplicação, ordem de inicialização) —
/// mas nenhuma delas era a causa do sintoma que continuava (vídeo instável com o cabo do Livewire
/// conectado, só 100% confiável removendo o cabo antes de ligar a máquina). Diagnóstico ao vivo
/// (`netstat`/`Get-NetTCPConnection` capturados com o usuário, comparando `CreationTime` da conexão
/// contra `LastBootUpTime` do Windows e o `StartTime` do próprio processo): mesmo com a regra de
/// bloqueio confirmada presente, habilitada e com o programa/IP corretos via
/// `netsh advfirewall firewall show rule ... verbose`, uma conexão de vídeo NOVA (criada bem depois
/// do boot E do início do processo, sem nenhuma corrida possível) se estabelecia pela placa do
/// Livewire do mesmo jeito. Causa: <c>netsh advfirewall show allprofiles state</c> revelou que os
/// perfis de Firewall Particular e Público da máquina estavam **DESLIGADOS** — só o perfil de
/// Domínio (que essa máquina nunca usa) estava ligado. Com o perfil desligado, o Firewall do Windows
/// não filtra NADA naquela rede — nenhuma regra, por mais correta que esteja desenhada, chega a ser
/// avaliada. As quatro sprints anteriores (v1.0.23 a v1.0.26) estavam, sem saber, ajustando o desenho
/// de uma regra que nunca teve chance de rodar, porque o interruptor geral daquele tipo de rede
/// estava desligado o tempo todo — explica por que toda regra sempre conferia "perfeita" no
/// `show rule verbose` e mesmo assim nunca bloqueava nada de verdade.
///
/// Corrigido em duas partes, e a segunda parte é OBRIGATÓRIA por causa da primeira: (1)
/// <see cref="ApplyBlockRule"/> agora também liga os perfis Particular e Público do Firewall do
/// Windows (<c>netsh advfirewall set privateprofile/publicprofile state on</c>) toda vez que roda —
/// sem isso as regras de bloqueio abaixo continuam sem efeito nenhum, do jeito que estavam até a
/// v1.0.26. (2) Ligar o Firewall muda o comportamento PADRÃO da máquina pra QUALQUER programa: o
/// perfil Público, confirmado em teste real nesta mesma sessão, usa a política
/// <c>BlockInbound,AllowOutbound</c> — ou seja, TODA conexão de entrada não solicitada passa a ser
/// bloqueada por padrão, a não ser que exista uma regra de PERMISSÃO explícita. Como a Cartucheira
/// nunca precisou de uma (o Firewall estava desligado desde sempre), ligar o Firewall sem mais nada
/// bloquearia o próprio NDI da Cartucheira em QUALQUER placa, não só na do Livewire — confirmado ao
/// vivo: depois de ligar os perfis, o vídeo simplesmente parou de chegar em qualquer placa, e voltou
/// a funcionar (pela placa CERTA, confirmado por <c>Get-NetTCPConnection</c>) só depois de criar duas
/// regras novas de PERMISSÃO pro processo da própria Cartucheira — TCP de entrada (qualquer IP/porta)
/// e mDNS/UDP-5353 de entrada (qualquer IP) — ver <see cref="InboundTcpAllowRuleName"/>/
/// <see cref="InboundMdnsAllowRuleName"/>. Essas regras de permissão são gerais (sem `localip`), mas
/// isso não reabre a placa do Livewire: o Firewall do Windows sempre dá prioridade pra uma regra de
/// BLOQUEIO quando ela e uma de PERMISSÃO se aplicam ao mesmo pacote — confirmado ao vivo também (as
/// quatro conexões de vídeo testadas depois foram todas pela placa correta, nenhuma pela do
/// Livewire, mesmo com a regra de permissão geral criada).
///
/// **Efeito colateral avisado ao usuário, ainda sem solução automática:** ligar o Firewall (que
/// antes estava desligado pra QUALQUER programa da máquina) pode afetar outros programas que nunca
/// precisaram de regra de permissão própria — confirmado na mesma noite: o driver de áudio da Axia
/// (IP-Driver, usado pelos Decks hoje pra Livewire) parou de levar áudio depois da mudança. Se
/// aparecer algum outro programa/recurso de rede que pare de funcionar depois desta versão, o
/// caminho é o mesmo já usado aqui: achar o `.exe`/serviço responsável e criar uma regra de permissão
/// pra ele — não é algo a Cartucheira pode prever/cobrir sozinha por outros programas.
/// </summary>
public static class NdiFirewallGuard
{
    /// <summary>
    /// Fix pós-entrega da v1.0.24 (v1.0.25, mesmo dia): identifica o DESENHO ATUAL do conjunto de
    /// regras (hoje: as quatro regras — TCP entrada+saída, mDNS/UDP-5353 entrada+saída). Existe
    /// porque <c>MainViewModel.ReapplyNdiFirewallGuard</c> só reaplica a regra quando algo persistido
    /// em disco muda — e até a v1.0.24 essa memória era só o IP da placa do Livewire
    /// (<c>AppSettingsSnapshot.NdiFirewallGuardAppliedForLivewireIp</c>). Resultado real, confirmado
    /// pelo usuário ("tudo igual, funcionando pela placa do livewire"): como o IP da placa não mudou
    /// entre a v1.0.23 (que já tinha "aplicado com sucesso", só que com o desenho ERRADO de regra —
    /// só saída) e a v1.0.24 (que corrigiu o desenho, acrescentando entrada), o programa nunca chamou
    /// <see cref="ApplyBlockRule"/> de novo — o código novo simplesmente nunca rodou na máquina do
    /// usuário, mesmo já compilado e instalado. Agora <c>MainViewModel</c> compara IP E este número
    /// (via novo campo persistido <c>AppSettingsSnapshot.NdiFirewallGuardAppliedRuleSchemaVersion</c>)
    /// — qualquer mudança futura no desenho das regras exige incrementar este valor, o que força uma
    /// reaplicação (um novo UAC) mesmo que o IP não tenha mudado, em vez de ficar preso a um "já
    /// apliquei" desatualizado.
    ///
    /// Fix pós-entrega da v1.0.26 (v1.0.27, mesmo dia): bumped pra <c>3</c> — o desenho mudou de novo,
    /// desta vez pra corrigir a causa raiz de verdade (ver comentário de classe): agora
    /// <see cref="ApplyBlockRule"/> também liga os perfis de Firewall Particular/Público (sem isso
    /// nenhuma regra tem efeito) e cria duas regras novas de PERMISSÃO geral pro processo da
    /// Cartucheira (<see cref="InboundTcpAllowRuleName"/>/<see cref="InboundMdnsAllowRuleName"/>),
    /// necessárias porque ligar o Firewall muda o padrão da máquina pra bloquear entrada não
    /// solicitada de QUALQUER programa. Incrementar de novo força quem já rodou uma versão anterior
    /// (mesmo já com o IP certo persistido) a passar por essa aplicação nova pelo menos uma vez.
    /// </summary>
    public const int RuleSchemaVersion = 3;

    /// <summary>
    /// Nomes fixos das regras de BLOQUEIO — sempre os mesmos, independente do IP, pra nunca duplicar
    /// nem deixar regra de IP antigo esquecida no Firewall quando a placa do Livewire muda (todas são
    /// sempre apagadas antes de recriar, na mesma elevação). Quatro regras: TCP saída, TCP entrada (o
    /// fix de verdade — ver comentário de classe), mDNS saída e mDNS entrada. Só existem/fazem sentido
    /// quando existe uma placa de Livewire configurada pra proteger — por isso dependem do IP.
    /// </summary>
    private const string OutboundTcpRuleName = "CartucheiraVideo - Bloqueia NDI (TCP saida) na placa do Livewire";
    private const string InboundTcpRuleName = "CartucheiraVideo - Bloqueia NDI (TCP entrada) na placa do Livewire";
    private const string OutboundMdnsRuleName = "CartucheiraVideo - Bloqueia anuncio NDI (mDNS saida) na placa do Livewire";
    private const string InboundMdnsRuleName = "CartucheiraVideo - Bloqueia anuncio NDI (mDNS entrada) na placa do Livewire";

    private static readonly string[] AllRuleNames =
    {
        OutboundTcpRuleName,
        InboundTcpRuleName,
        OutboundMdnsRuleName,
        InboundMdnsRuleName,
    };

    /// <summary>
    /// Fix pós-entrega da v1.0.26 (v1.0.27, mesmo dia): nomes fixos das duas regras de PERMISSÃO
    /// geral pro processo da Cartucheira — ver comentário de classe pro motivo de existirem (ligar o
    /// Firewall muda o padrão da máquina pra bloquear entrada não solicitada de QUALQUER programa).
    /// Diferente das regras de bloqueio acima, estas NÃO dependem do IP do Livewire (não têm
    /// `localip`, valem em qualquer placa) e por isso são sempre mantidas, mesmo quando nenhuma placa
    /// de Livewire está configurada pra proteger — sem elas o NDI da própria Cartucheira para de
    /// funcionar em QUALQUER placa assim que o Firewall estiver ligado, confirmado em teste real.
    /// Seguras de deixar gerais (sem `localip`) porque o Firewall do Windows sempre prioriza uma
    /// regra de BLOQUEIO sobre uma de PERMISSÃO quando as duas se aplicam ao mesmo pacote — a regra
    /// de bloqueio da placa do Livewire (acima) continua valendo por cima desta permissão geral.
    /// </summary>
    private const string InboundTcpAllowRuleName = "CartucheiraVideo - Permite NDI (TCP entrada)";
    private const string InboundMdnsAllowRuleName = "CartucheiraVideo - Permite anuncio NDI (mDNS entrada)";

    /// <summary>
    /// Recria as quatro regras de Firewall (TCP entrada+saída, mDNS/UDP-5353 entrada+saída) pra
    /// bloquear o NDI da Cartucheira quando a origem/destino for <paramref name="livewireIp"/>.
    /// <paramref name="livewireIp"/> nulo/vazio só remove essas quatro regras (sem placa de Livewire
    /// dedicada configurada, não tem nada pra proteger nessa placa).
    ///
    /// Fix pós-entrega da v1.0.26 (v1.0.27, mesmo dia) — ver comentário de classe pra causa raiz
    /// completa: além das quatro regras acima, ESTE MÉTODO AGORA TAMBÉM (sempre, independente de
    /// <paramref name="livewireIp"/> estar preenchido ou não): (1) liga os perfis de Firewall
    /// Particular e Público da máquina — sem isso NENHUMA regra, nem a de bloqueio nem a de
    /// permissão, tem efeito nenhum; (2) recria as duas regras de PERMISSÃO geral pro processo da
    /// Cartucheira (<see cref="InboundTcpAllowRuleName"/>/<see cref="InboundMdnsAllowRuleName"/>),
    /// necessárias porque ligar o Firewall muda o padrão da máquina pra bloquear entrada não
    /// solicitada de QUALQUER programa — sem elas o próprio NDI da Cartucheira pararia de funcionar
    /// em QUALQUER placa, não só na do Livewire.
    ///
    /// Sempre apaga as regras antigas primeiro (mesmos nomes fixos) antes de criar as novas, tudo
    /// numa ÚNICA elevação (um só comando via <c>cmd /c</c>, encadeando ligar-perfis + apagar+criar
    /// todas as regras), pra pedir UAC uma vez só. Nunca lança — devolve <c>(false, motivo)</c> em
    /// qualquer falha, incluindo o usuário cancelar a janela de permissão (código de erro 1223 do
    /// Windows).
    /// </summary>
    public static (bool Ok, string? Error) ApplyBlockRule(string? livewireIp)
    {
        try
        {
            var exePath = Process.GetCurrentProcess().MainModule?.FileName;
            if (string.IsNullOrWhiteSpace(exePath))
            {
                return (false, "não consegui descobrir o caminho do próprio executável da Cartucheira");
            }

            var isRemovalOnly = string.IsNullOrWhiteSpace(livewireIp);

            var commandParts = new List<string>
            {
                // Fix pós-entrega da v1.0.26 (v1.0.27): liga os perfis que hoje protegem esta
                // máquina (Particular/Público) — achado em diagnóstico ao vivo que os dois estavam
                // DESLIGADOS, fazendo toda regra das quatro sprints anteriores (v1.0.23 a v1.0.26)
                // nunca ter efeito nenhum, mesmo perfeitamente correta. Idempotente (repetir "on"
                // num perfil já ligado não tem efeito colateral).
                "netsh advfirewall set privateprofile state on",
                "netsh advfirewall set publicprofile state on",
            };

            // Regras de BLOQUEIO da placa do Livewire — dependem do IP, sempre apagadas antes de
            // recriar (ou de deixar removidas, se isRemovalOnly).
            commandParts.AddRange(AllRuleNames.Select(name => $"netsh advfirewall firewall delete rule name=\"{name}\" >nul 2>&1"));

            if (!isRemovalOnly)
            {
                commandParts.Add($"netsh advfirewall firewall add rule name=\"{OutboundTcpRuleName}\" dir=out program=\"{exePath}\" action=block protocol=TCP localip={livewireIp}");
                commandParts.Add($"netsh advfirewall firewall add rule name=\"{InboundTcpRuleName}\" dir=in program=\"{exePath}\" action=block protocol=TCP localip={livewireIp}");
                commandParts.Add($"netsh advfirewall firewall add rule name=\"{OutboundMdnsRuleName}\" dir=out program=\"{exePath}\" action=block protocol=UDP localip={livewireIp} localport=5353");
                commandParts.Add($"netsh advfirewall firewall add rule name=\"{InboundMdnsRuleName}\" dir=in program=\"{exePath}\" action=block protocol=UDP localip={livewireIp} localport=5353");
            }

            // Fix pós-entrega da v1.0.26 (v1.0.27): regras de PERMISSÃO geral pro próprio processo —
            // sempre recriadas, independente de ter placa de Livewire configurada ou não (ver
            // comentário do método/classe pro motivo). Ficam por ÚLTIMO de propósito: o código de
            // saída do comando encadeado (`cmd /c "a & b & c"`) reflete só o ÚLTIMO comando, então
            // terminar sempre com um "add rule" de verdade (não um "delete" que pode legitimamente
            // não achar nada) deixa o ExitCode confiável nos dois casos (com ou sem Livewire IP).
            commandParts.Add($"netsh advfirewall firewall delete rule name=\"{InboundTcpAllowRuleName}\" >nul 2>&1");
            commandParts.Add($"netsh advfirewall firewall add rule name=\"{InboundTcpAllowRuleName}\" dir=in program=\"{exePath}\" action=allow protocol=TCP");
            commandParts.Add($"netsh advfirewall firewall delete rule name=\"{InboundMdnsAllowRuleName}\" >nul 2>&1");
            commandParts.Add($"netsh advfirewall firewall add rule name=\"{InboundMdnsAllowRuleName}\" dir=in program=\"{exePath}\" action=allow protocol=UDP localport=5353");

            var command = string.Join(" & ", commandParts);

            var startInfo = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c \"{command}\"",
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
            };

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return (false, "não consegui iniciar o processo elevado do Windows pra aplicar as regras de Firewall");
            }

            process.WaitForExit();

            // Fix pós-entrega da v1.0.26 (v1.0.27): antes disto aqui só relaxava (aceitava qualquer
            // código de saída) quando isRemovalOnly, porque nesse caso o comando encadeado terminava
            // num "delete" que podia legitimamente não achar nada pra apagar. Agora o comando SEMPRE
            // termina com um "add rule" de permissão de verdade (ver comentário acima), então o
            // código de saída é confiável nos dois casos — sempre exige 0.
            if (process.ExitCode != 0)
            {
                return (false, $"o Windows recusou aplicar as regras de Firewall (código {process.ExitCode})");
            }

            return (true, null);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return (false, "a permissão de Administrador foi cancelada na janela do Windows");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }
}
