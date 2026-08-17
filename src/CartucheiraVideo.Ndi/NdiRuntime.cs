namespace CartucheiraVideo.Ndi;

/// <summary>
/// Inicializacao/encerramento GLOBAIS da NDI SDK (NDIlib_initialize/NDIlib_destroy) — precisam
/// acontecer exatamente uma vez por processo, nao uma vez por <see cref="NdiSender"/> (mesmo
/// padrao ja usado pro LibVLC compartilhado: quem chama isto e o composition root, App.xaml.cs,
/// no OnStartup/OnExit).
///
/// Deliberadamente NUNCA deixa uma falha de inicializacao (DLL nativa ausente, arquitetura errada,
/// etc.) virar excecao pra fora — essa e a chamada mais arriscada de toda a Sprint NDI: se ela
/// travasse o processo, um usuario sem o NDI Runtime instalado (ou numa maquina sem os requisitos
/// dele) perderia o programa inteiro por causa de uma funcionalidade opcional. Em vez disso,
/// <see cref="Initialize"/> so registra o resultado em <see cref="IsAvailable"/> — quem cria um
/// <see cref="NdiSender"/> depois confere essa flag e falha de forma limpa (excecao gerenciada,
/// sem chamar nenhuma outra funcao nativa da NDI SDK enquanto ela nao estiver inicializada, o que
/// seria comportamento indefinido do lado nativo).
/// </summary>
public static class NdiRuntime
{
    private static bool _initialized;

    /// <summary>Verdadeiro so depois de <see cref="Initialize"/> ter chamado NDIlib_initialize() com sucesso. <see cref="NdiSender"/> recusa criar um emissor enquanto isto for falso.</summary>
    public static bool IsAvailable { get; private set; }

    /// <summary>
    /// Motivo legivel da falha, preenchido so quando <see cref="IsAvailable"/> fica falso — usado
    /// pela mensagem de excecao que <see cref="NdiSender"/> lanca (e que o DeckViewModel mostra na
    /// barra de status), pra alguem sem acesso a este codigo conseguir diagnosticar sem precisar
    /// pedir o texto completo do erro toda vez.
    /// </summary>
    public static string? FailureReason { get; private set; }

    /// <summary>
    /// Chamar uma vez, antes de criar qualquer <see cref="NdiSender"/> — normalmente no
    /// composition root (App.xaml.cs OnStartup). Chamadas repetidas depois da primeira nao fazem
    /// nada (idempotente). Nunca lanca excecao: se a NDI SDK nao estiver instalada/utilizavel
    /// nesta maquina, <see cref="IsAvailable"/> simplesmente fica falso e o resto do programa
    /// continua funcionando normalmente, sem saida NDI.
    /// </summary>
    public static void Initialize()
    {
        if (_initialized)
        {
            return;
        }

        _initialized = true;

        try
        {
            IsAvailable = NativeMethods.NDIlib_initialize();
            if (!IsAvailable)
            {
                FailureReason = "NDIlib_initialize() retornou false (DLL encontrada, mas a inicializacao falhou — verifique se o processador suporta SSE4.1).";
            }
        }
        catch (DllNotFoundException)
        {
            // Processing.NDI.Lib.x64.dll nao foi encontrada nem via NDI_RUNTIME_DIR_V6
            // (NativeMethods.ResolveNdiLibrary) nem via PATH/pasta do executavel/System32 — o NDI
            // Runtime/Tools provavelmente nao esta instalado nesta maquina, ou foi instalado numa
            // versao que nao usa essa variavel de ambiente.
            IsAvailable = false;
            FailureReason = "Processing.NDI.Lib.x64.dll nao encontrada (verifique se o NDI Tools/Runtime 6 esta instalado).";
        }
        catch (BadImageFormatException)
        {
            // DLL encontrada mas de arquitetura errada (ex.: x86 num processo x64).
            IsAvailable = false;
            FailureReason = "Processing.NDI.Lib.x64.dll encontrada mas de arquitetura incompativel (esperado x64).";
        }
        catch (EntryPointNotFoundException)
        {
            // DLL encontrada, mas de uma versao da SDK sem esta funcao — improvavel, mas seguro.
            IsAvailable = false;
            FailureReason = "Processing.NDI.Lib.x64.dll encontrada, mas sem a funcao NDIlib_initialize (versao incompativel da SDK).";
        }
    }

    /// <summary>Chamar uma vez, no encerramento do processo (App.xaml.cs OnExit), depois de todo NdiSender ja ter sido descartado. Sem efeito se Initialize nunca teve sucesso.</summary>
    public static void Shutdown()
    {
        if (!_initialized || !IsAvailable)
        {
            return;
        }

        NativeMethods.NDIlib_destroy();
        IsAvailable = false;
        _initialized = false;
    }
}
