using System;
using System.IO;

namespace CartucheiraVideo.Infrastructure;

/// <summary>
/// Fix v1.0.59 (2026-11-11): incidente real em produção — o usuário relatou "dei Play e o programa
/// fechou", sem nenhuma mensagem de erro na tela ("sumiu sem avisar nada"). Investigação encontrou
/// a causa raiz dessa parte: <c>App.xaml.cs</c> não tinha NENHUM tratamento de exceção não
/// capturada — nem <c>Application.DispatcherUnhandledException</c> (thread de UI),
/// nem <c>AppDomain.UnhandledException</c> (qualquer outra thread), nem
/// <c>TaskScheduler.UnobservedTaskException</c> (Task "fire-and-forget" que falha sem ninguém
/// observar). Qualquer exceção não tratada em QUALQUER lugar do programa (Engine, NDI, Livewire,
/// etc.) sempre derrubava o processo inteiro na hora, sem log nenhum — exatamente o sintoma
/// relatado, e sem nenhum jeito de diagnosticar a causa de verdade sem um debugger anexado (que
/// não existe em produção, nem neste sandbox, que não tem SDK .NET pra sequer compilar).
///
/// Esta classe grava qualquer exceção não tratada em <c>%AppData%\CartucheiraVideo\crash.log</c>
/// (mesma pasta de <c>Configuration.SettingsStore</c>) ANTES do processo morrer (ou, no caso da
/// thread de UI, em vez de morrer — ver <see cref="App.OnDispatcherUnhandledException"/>), pra que
/// a PRÓXIMA vez que isto acontecer a gente tenha o tipo da exceção e o stack trace de verdade, em
/// vez de só "fechou sem avisar nada". Nunca lança — qualquer falha ao tentar escrever o log é
/// silenciosamente ignorada (prioridade: nunca fazer o tratamento de crash causar OUTRO crash).
/// </summary>
public static class CrashLogger
{
    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "CartucheiraVideo",
        "crash.log");

    /// <summary>Grava <paramref name="exception"/> (tipo, mensagem, stack trace, e todas as InnerException encadeadas) no log de crash, com timestamp e uma etiqueta de origem (<paramref name="source"/>, ex.: "UI thread", "thread em segundo plano", "Task não observada").</summary>
    public static void Log(string source, Exception exception)
    {
        try
        {
            var directory = Path.GetDirectoryName(LogPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var entry = $"""
                ================================================================
                {DateTime.Now:yyyy-MM-dd HH:mm:ss} — {source}
                {FormatExceptionChain(exception)}

                """;

            File.AppendAllText(LogPath, entry);
        }
        catch
        {
            // Falha honesta, mas silenciosa de propósito — ver comentário da classe: o tratamento
            // de crash nunca pode ele próprio virar outro crash (ex.: disco cheio, sem permissão de
            // escrita em %AppData% por algum motivo raro).
        }
    }

    private static string FormatExceptionChain(Exception exception)
    {
        var current = exception;
        var depth = 0;
        var text = string.Empty;

        while (current is not null)
        {
            var prefix = depth == 0 ? "" : $"--- InnerException (nível {depth}) ---{Environment.NewLine}";
            text += $"{prefix}{current.GetType().FullName}: {current.Message}{Environment.NewLine}{current.StackTrace}{Environment.NewLine}";
            current = current.InnerException;
            depth++;
        }

        return text;
    }
}
