using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using CartucheiraVideo.Infrastructure;
using CartucheiraVideo.Ndi;
using CartucheiraVideo.Presentation.ViewModels;
using CartucheiraVideo.Presentation.Views;
using LibVLC = LibVLCSharp.Shared.LibVLC;

namespace CartucheiraVideo.Presentation;

/// <summary>
/// Composition root da aplicacao. A partir da Sprint Engine (2026-08-08), tambem e responsavel
/// por criar a UNICA instancia de <see cref="LibVLC"/> do processo — criar uma por Deck seria
/// desperdicio (cada instancia carrega os plugins do libvlc do zero); a instancia compartilhada e
/// injetada em cada <see cref="DeckViewModel"/> (via <see cref="MainViewModel"/>), que cria seu
/// proprio <c>Engine.PlaybackEngine</c> (e portanto seu proprio <c>MediaPlayer</c>) em cima dela.
///
/// Desde a Sprint NDI (2026-08-09), tambem inicializa a NDI SDK (<see cref="NdiRuntime.Initialize"/>)
/// UMA vez por processo, antes de qualquer <see cref="DeckViewModel"/> ser criado (cada um tenta
/// criar seu proprio NdiSender no construtor) — mesmo padrao do LibVLC compartilhado acima.
/// <see cref="NdiRuntime.Initialize"/> nunca lanca excecao (ver comentario da propria classe), entao
/// isto e seguro de chamar mesmo numa maquina sem a NDI SDK instalada.
///
/// Fix v1.0.59 (2026-11-11): incidente real — "dei Play e o programa fechou", sem NENHUMA mensagem
/// de erro. Até esta versão, nenhuma exceção não tratada tinha onde cair: qualquer uma (Engine,
/// NDI, Livewire, UI) derrubava o processo inteiro na hora, sem log. Agora os três pontos de
/// exceção não tratada do .NET (thread de UI, qualquer outra thread, Task não observada) são
/// capturados e gravados em <see cref="CrashLogger"/> ANTES de decidir o que fazer — ver cada
/// handler abaixo pro porquê de cada um reagir diferente.
/// </summary>
public partial class App : Application
{
    private LibVLC? _libVlc;
    private MainViewModel? _mainViewModel;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Fix v1.0.71 (2026-11-12): incidente real GRAVE — usuario reportou "mesmo a cartucheira
        // fechada, o software fechado, ta mandando imagem pro NDI e pra tela". Causa raiz: o
        // padrao WPF (ShutdownMode.OnLastWindowClose, nunca definido explicitamente neste projeto
        // antes desta correcao) so chama OnExit() — e portanto todo o Dispose() que destroi o
        // NdiSender de cada Deck e fecha as ScreenOutputWindow (ver DeckViewModel.Dispose) —
        // quando a ULTIMA janela da aplicacao fechar. As ScreenOutputWindow (Sprint
        // Telas-Saida-Direta, 2026-11-10 — sem borda, sem entrada na barra de tarefas, entao
        // invisveis como "janela" pro operador) sao janelas WPF de verdade, continuam na colecao
        // Application.Windows mesmo depois do operador fechar a MainWindow — e NADA fechava elas
        // nesse momento (MainWindow nao tinha nenhum handler de Closing). Resultado: a MainWindow
        // fecha (parece "fechado" pro operador — sem janela visivel, sem entrada na barra de
        // tarefas), mas o PROCESSO continua rodando pra sempre em segundo plano, porque ainda
        // existem ScreenOutputWindow abertas — e so o processo rodando e suficiente pro timer de
        // Arte de Fundo (DeckViewModel) e o NdiSender continuarem mandando quadro, indefinidamente,
        // sem ningum conseguir desligar exceto matando o processo no Gerenciador de Tarefas. Isso
        // tambem explica o sintoma relatado antes desta correcao ("NDI parou, mandando a imagem
        // padrao"): um processo "zumbi" anterior, parado nesse estado, continua anunciando a MESMA
        // fonte NDI (mesmo nome de canal) na rede, brigando com a instancia nova que o operador
        // acabou de abrir — o monitor NDI do usuario pode estar vendo o zumbi antigo (congelado na
        // arte de fundo), nao a instancia nova. CORRIGIDO: ShutdownMode.OnMainWindowClose — agora a
        // aplicacao sempre desliga de vez (chamando OnExit, que desliga tudo corretamente, incluindo
        // fechar as ScreenOutputWindow e destruir cada NdiSender) no instante em que a MainWindow
        // fechar, independente de quaisquer outras janelas (ScreenOutputWindow ou futuras) ainda
        // abertas. Ver tambem MainWindow.xaml.cs (handler de Closing, segunda camada de seguranca:
        // fecha as ScreenOutputWindow na hora, sem depender do timing do Dispose).
        ShutdownMode = ShutdownMode.OnMainWindowClose;

        // Fix v1.0.59 (2026-11-11): registrados o MAIS CEDO possível no startup — antes de
        // qualquer coisa que possa falhar (NDI, LibVLC, Decks) — pra nunca deixar uma janela sem
        // cobertura nenhuma.
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnAppDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        NdiRuntime.Initialize();

        // Totalmente qualificado de proposito: "Core" sozinho colidiria com o namespace
        // CartucheiraVideo.Core (o projeto de dominio) se algum dia for importado neste arquivo.
        LibVLCSharp.Shared.Core.Initialize();

        // Fix v1.0.60 (2026-11-11): incidente real em investigacao — "dei Play num Deck e o
        // programa fechou" se repete mesmo com os handlers de excecao da v1.0.59 no lugar (eles
        // nunca disparam, crash.log nunca e criado). Confirmado via Visualizador de Eventos do
        // Windows: codigo de excecao 0xc0000005 (STATUS_ACCESS_VIOLATION), modulo com falha
        // ntdll.dll — uma categoria de falha nativa que NENHUM handler gerenciado do .NET consegue
        // capturar (ver comentario de classe acima). crash.log so registra excecoes do CLR; nunca
        // vai ver isto. A UNICA forma de capturar uma pista real da causa nativa (qual arquivo,
        // qual codec, qual modulo interno do libvlc) e pelo proprio log nativo do libvlc — por isso
        // estas duas opcoes abaixo, passadas direto pro libvlc_new nativo (nao e codigo gerenciado,
        // nao passa por nenhum dos handlers acima). "--verbose=2" e o nivel de log mais detalhado
        // documentado da VLC CLI (debug); "--file-logging"/"--logfile=" sao o modulo "logger" oficial
        // do libvlc que grava esse log em arquivo, em vez de so na saida padrao (que este processo,
        // rodando sem console, nao tem onde mostrar). Mesma pasta de sempre
        // (%AppData%\CartucheiraVideo), igual ao crash.log e ao settings.json.
        var libVlcLogPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "CartucheiraVideo",
            "libvlc.log");
        try
        {
            var libVlcLogDirectory = Path.GetDirectoryName(libVlcLogPath);
            if (!string.IsNullOrEmpty(libVlcLogDirectory))
            {
                Directory.CreateDirectory(libVlcLogDirectory);
            }
        }
        catch
        {
            // Falha honesta e silenciosa — mesma postura do CrashLogger: diagnostico nunca pode
            // impedir o programa de abrir. Sem a pasta criada aqui, o libvlc so vai falhar em criar
            // o proprio arquivo de log (sem log nativo nesta sessao), nunca derruba o startup.
        }

        // Fix v1.0.63 (2026-11-12): incidente real — o rastro de chamadas nativas (v1.0.62,
        // Engine.PlaybackEngine.LogNativeCallTrace) confirmou com certeza que o crash de acesso
        // invalido (0xc0000005) ao dar Pause/Stop acontece DENTRO da propria chamada
        // `_mediaPlayer.Stop()` — o player da TELA (preview local, nunca o player headless que
        // alimenta o NDI). Primeira hipotese testada (v1.0.63, "--avcodec-hw=none", desligar
        // decodificacao por HARDWARE): usuario reproduziu o mesmo crash de novo, no MESMO ponto
        // exato (rastro confirmou: "Stop() — antes de _mediaPlayer.Stop()", sem o "depois de"),
        // mesmo com essa opcao aplicada — hipotese REFUTADA, decodificacao por hardware nao era
        // (ou nao era so) a causa.
        //
        // Fix v1.0.64 (2026-11-12): revisao apos a refutacao acima — "--avcodec-hw" controla so a
        // DECODIFICACAO (avcodec escolhendo um decoder com aceleracao de hardware); o modulo de
        // SAIDA/renderizacao de video (vout) e uma coisa SEPARADA, e o log nativo do libvlc (v1.0.60)
        // mostrou que o player da tela estava usando "direct3d11" como vout (confirmado:
        // "using vout display module "direct3d11"") — o renderizador em si, nao o decoder, que
        // continua em uso independente de "--avcodec-hw". Teardown do vout Direct3D11 (o que
        // `MediaPlayer.Stop()` faz — destroi o vout atual, o proximo Play recria do zero) embutido
        // numa janela WPF (VideoView) e uma combinacao com historico documentado de instabilidade
        // na comunidade LibVLCSharp, especialmente em placas Intel integradas (confirmado no log:
        // "Intel WDDM driver 32.0.101.7088") — mais madura/testada ha mais anos que o vout Direct3D9
        // antigo. "--vout=direct3d9" forca o RENDERIZADOR do player da tela pro modulo antigo
        // (ainda acelerado por GPU, sem perda de fluidez esperada) — so afeta <c>_mediaPlayer</c>
        // (o player da tela): <c>_framePlayer</c>/<c>_framePlayerB</c> (o player headless que
        // alimenta o NDI) chamam <c>SetVideoCallbacks</c> explicitamente, o que forca o modulo
        // "vmem" pra ELES independente de qualquer "--vout" global aqui — zero impacto no sinal de
        // transmissao real, que nunca passou pelo vout Direct3D11/Direct3D9 pra comecar. Mantido
        // "--avcodec-hw=none" tambem (defesa em profundidade — nao comprovado necessario, mas sem
        // custo real pra um unico Deck 1080p, e nao foi ele quem causou o crash refutado acima, so
        // nao foi suficiente por si so). NAO comprovado 100% ainda — depende do proximo teste do
        // usuario confirmar.
        //
        // Fix v1.0.69 (2026-11-12): PRIMEIRA PROVA REAL desta investigacao inteira — usuario capturou
        // um dump nativo de verdade (Visualizador de Erros do Windows + WinDbg, "!analyze -v"/"kb"
        // depois de ".ecxr") do crash que sobrevivia ate a v1.0.68 (trava _nativeCallLock). O resultado
        // muda a conclusao de toda a investigacao: a pilha de chamadas nao tem NENHUM frame gerenciado
        // (`!clrstack` confirmou: "current thread is likely not a managed thread" — ou seja, nao e
        // nenhuma thread nossa, nem a UI, nem nenhuma Task/Timer deste programa) e mostra, do topo pro
        // fundo: ntdll!RtlFreeHeap -> msvcrt!free -> msvcrt!aligned_free -> tres frames dentro de
        // "libavcodec_plugin" (o modulo da libvlc que decodifica video via FFmpeg) -> a thread nativa
        // sendo iniciada (msvcrt!_threadstartex/kernel32!BaseThreadInitThunk). A excecao: acesso
        // invalido tentando LER do endereco 0xFFFFFFFFFFFFFFFF (todos os bits ligados — a assinatura
        // classica de um ponteiro corrompido/invalido sendo passado pra free()), dentro de
        // RtlFreeHeap — ou seja, CORRUPCAO DE MEMORIA (heap) dentro do proprio decodificador
        // libavcodec, numa thread de decodificacao interna do FFmpeg, nada a ver com
        // Play/Pause/Stop/Seek/SetOutputDevice do nosso codigo gerenciado (PlaybackEngine.cs) — nenhum
        // deles aparece em nenhum lugar desta pilha. Isso explica, em retrospecto, o atraso de "alguns
        // segundos" que motivou a v1.0.68: corrupcao de heap nao crasha no instante em que acontece,
        // so na proxima vez que aquele bloco de memoria for tocado de novo (aqui, liberado) — um
        // Stop/Seek perto no tempo e so coincidencia de quando esse free() seguinte aconteceu de
        // rodar, nao a causa. A trava `_nativeCallLock` da v1.0.68 continua sendo uma correcao real e
        // valida (defeito de concorrencia genuino, documentado la), mas a evidencia agora mostra que
        // ela nao tinha chance de corrigir ESTE crash especifico — ele acontece inteiramente dentro do
        // FFmpeg, numa thread que nosso codigo nem enxerga.
        //
        // "--avcodec-hw=none" (ja aplicado acima desde a v1.0.63) prova que NAO e decodificacao por
        // HARDWARE — o crash deste dump aconteceu com hardware ja desligado, dentro do caminho de
        // SOFTWARE do FFmpeg. O proximo suspeito mais provavel pra corrupcao de heap especificamente
        // dentro do decodificador por software e a DECODIFICACAO MULTI-THREAD do proprio FFmpeg (um
        // decoder busy pode usar varias threads internas pra decodificar quadros em paralelo —
        // categoria conhecida de bugs de concorrencia/corrupcao em codecs especificos de certas
        // versoes do FFmpeg). "--avcodec-threads=1" forca o libavcodec a decodificar numa UNICA thread
        // interna, eliminando essa classe inteira de bug ao custo de CPU extra (aceitavel pra um unico
        // Deck 1080p). NAO comprovado ainda — e a primeira tentativa de correcao desta investigacao
        // baseada em evidencia forense real (dump+WinDbg) em vez de inferencia a partir de log
        // gerenciado proprio; so o proximo teste do usuario, com outro dump se crashar de novo, confirma.
        _libVlc = new LibVLC("--verbose=2", "--file-logging", $"--logfile={libVlcLogPath}", "--avcodec-hw=none", "--vout=direct3d9", "--avcodec-threads=1");

        _mainViewModel = new MainViewModel(_libVlc);
        var mainWindow = new MainWindow(_mainViewModel);
        mainWindow.Show();

        // Fix v1.0.71 (2026-11-12): explicito de proposito, nunca depender do WPF "adivinhar"
        // sozinho qual janela e a principal (ele normalmente acerta — a primeira Show() da
        // aplicacao — mas o ShutdownMode.OnMainWindowClose acima depende 100% deste valor estar
        // certo, entao e barato demais deixar implicito algo tao critico).
        MainWindow = mainWindow;

        // Fix v1.0.54 (2026-11-11): tem que vir DEPOIS do Show() acima, nunca dentro do construtor
        // do MainViewModel — ver comentário completo em MainViewModel.InitializeAfterMainWindowShown
        // pro porquê (a checagem de segurança de "Telas" precisa da janela principal já visível pra
        // saber qual monitor é o do operador e nunca abrir uma saída de tela em cima dele).
        _mainViewModel.InitializeAfterMainWindowShown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // Ordem importa: os MediaPlayer dos Decks (dentro do MainViewModel) precisam ser
        // descartados antes do LibVLC compartilhado que os criou — e todo NdiSender (tambem dentro
        // de cada DeckViewModel, descartado junto no MainViewModel.Dispose) precisa ser destruido
        // antes do NdiRuntime.Shutdown() global.
        _mainViewModel?.Dispose();
        _libVlc?.Dispose();
        NdiRuntime.Shutdown();
        base.OnExit(e);
    }

    /// <summary>
    /// Fix v1.0.59 (2026-11-11): exceção não tratada na thread de UI (ex.: dentro de um Command,
    /// um binding, um evento de botão) — a ÚNICA categoria das três aqui que dá pra realmente
    /// EVITAR o crash (<see cref="DispatcherUnhandledExceptionEventArgs.Handled"/> = true impede o
    /// WPF de derrubar o processo). Decisão deliberada: marcar como tratada e deixar o programa
    /// CONTINUANDO no ar, em vez de deixar morrer — isto é uma ferramenta de transmissão ao vivo,
    /// e um Deck/Cartucheira continuando de pé (mesmo com uma função pontual quebrada) é sempre
    /// preferível a derrubar a transmissão inteira no meio de um programa. O log é o que permite
    /// corrigir a causa raiz depois, sem precisar que o operador perca a saída ao vivo pra isso.
    /// </summary>
    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        CrashLogger.Log("Thread de UI (Dispatcher)", e.Exception);
        e.Handled = true;
    }

    /// <summary>
    /// Fix v1.0.59 (2026-11-11): exceção não tratada em QUALQUER outra thread (ex.: callback do
    /// LibVLC, timer do Engine, thread do NdiSender/LivewireAdvertiser). Diferente da de UI, esta
    /// categoria o .NET NUNCA permite suprimir — <c>IsTerminating</c> já vem sempre verdadeiro
    /// aqui, o processo VAI morrer de qualquer forma assim que este handler retornar. O único
    /// valor possível é logar antes disso acontecer, pra pelo menos saber a causa depois.
    /// </summary>
    private void OnAppDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception exception)
        {
            CrashLogger.Log("Thread em segundo plano (processo vai encerrar)", exception);
        }
    }

    /// <summary>
    /// Fix v1.0.59 (2026-11-11): uma <see cref="Task"/> "fire-and-forget" (nunca aguardada/
    /// observada por ninguém) que falhou — categoria historicamente arriscada no .NET (chegou a
    /// derrubar o processo por padrão em versões antigas do runtime). <see cref="UnobservedTaskExceptionEventArgs.SetObserved"/>
    /// marca a exceção como tratada pra nunca correr esse risco aqui, além de logar.
    /// </summary>
    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        CrashLogger.Log("Task não observada (fire-and-forget)", e.Exception);
        e.SetObserved();
    }
}
