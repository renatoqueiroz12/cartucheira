using System.Windows;
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
/// </summary>
public partial class App : Application
{
    private LibVLC? _libVlc;
    private MainViewModel? _mainViewModel;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

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
}
