using System;
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
        _libVlc = new LibVLC();

        _mainViewModel = new MainViewModel(_libVlc);
        var mainWindow = new MainWindow(_mainViewModel);
        mainWindow.Show();

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
