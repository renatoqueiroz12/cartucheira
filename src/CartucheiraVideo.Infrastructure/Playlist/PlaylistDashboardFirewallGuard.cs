using System.ComponentModel;
using System.Diagnostics;

namespace CartucheiraVideo.Infrastructure.Playlist;

/// <summary>
/// Regra de Firewall do Windows que libera, pra OUTROS computadores da rede local, a porta TCP onde
/// a dashboard de Playlist escuta — Sprint Playlist-Dashboard (2026-11-14). Diferente do
/// <c>LocalTriggerApiHost</c> (só <c>localhost</c>, nunca precisa de regra de Firewall porque nunca
/// aceita conexão de fora da própria máquina), a dashboard de Playlist É PRA SER alcançada por outro
/// PC da rede (o produtor) — por isso precisa de uma regra de PERMISSÃO de entrada, mesmo princípio
/// já usado pelo <c>CartucheiraVideo.Ndi.NdiFirewallGuard</c> (regras de permissão geral pro processo
/// da Cartucheira), só que aqui é o próprio objetivo da regra, não um efeito colateral a corrigir.
///
/// Exige permissão de Administrador (o Firewall do Windows não deixa usuário comum alterar regras) —
/// mesmo padrão de elevação via <c>cmd /c ... runas</c> do <c>NdiFirewallGuard</c>. Nunca lança:
/// qualquer falha (UAC cancelado, Windows recusando o comando) vira <c>(false, motivo)</c> — quem
/// chama decide o que logar, o servidor HTTP continua tentando subir mesmo sem a regra (só não vai
/// ser alcançável de outro computador até a regra existir).
/// </summary>
public static class PlaylistDashboardFirewallGuard
{
    /// <summary>
    /// Versão do desenho da regra — mesmo princípio do <c>NdiFirewallGuard.RuleSchemaVersion</c>:
    /// incrementar aqui força toda instalação a reaplicar a regra pelo menos uma vez (um novo UAC),
    /// mesmo que a porta configurada não tenha mudado desde a última vez que funcionou.
    /// </summary>
    public const int RuleSchemaVersion = 1;

    private const string InboundAllowRuleName = "CartucheiraVideo - Permite Dashboard de Playlist (TCP entrada)";

    /// <summary>
    /// Recria a regra de permissão de entrada pra <paramref name="port"/> — porta antiga (se
    /// diferente) é sempre removida primeiro, pra nunca deixar uma porta velha aberta à toa depois
    /// do usuário trocar a porta da dashboard em Configurações. <paramref name="port"/> nulo só
    /// remove a regra (módulo desligado, pasta raiz não configurada — nada pra liberar).
    /// </summary>
    public static (bool Ok, string? Error) ApplyAllowRule(int? port)
    {
        try
        {
            var exePath = Process.GetCurrentProcess().MainModule?.FileName;
            if (string.IsNullOrWhiteSpace(exePath))
            {
                return (false, "não consegui descobrir o caminho do próprio executável da Cartucheira.");
            }

            var commandParts = new List<string>
            {
                $"netsh advfirewall firewall delete rule name=\"{InboundAllowRuleName}\" >nul 2>&1",
            };

            if (port is int validPort)
            {
                commandParts.Add($"netsh advfirewall firewall add rule name=\"{InboundAllowRuleName}\" dir=in program=\"{exePath}\" action=allow protocol=TCP localport={validPort}");
            }
            else
            {
                // Comando sempre termina com algo que dá ExitCode confiável (0), mesmo quando só
                // removendo a regra — mesmo cuidado já documentado no NdiFirewallGuard.
                commandParts.Add("netsh advfirewall firewall show currentprofile >nul 2>&1");
            }

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
                return (false, "não consegui iniciar o processo elevado do Windows pra aplicar a regra de Firewall.");
            }

            process.WaitForExit();

            if (process.ExitCode != 0)
            {
                return (false, $"o Windows recusou aplicar a regra de Firewall (código {process.ExitCode}).");
            }

            return (true, null);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return (false, "a permissão de Administrador foi cancelada na janela do Windows.");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }
}
