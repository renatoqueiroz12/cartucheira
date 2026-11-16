using System.Linq;
using System.Windows;
using System.Windows.Input;
using CartucheiraVideo.Infrastructure.Playlist;

namespace CartucheiraVideo.Presentation.Views;

/// <summary>
/// Diálogo "Abrir Playlist" — Sprint 3 (2026-11-16, pedido do usuário "tem que ter lá a opção
/// abrir o dia, aí abre o programa e carrega"). Mesmo padrão de diálogo pontual sem ViewModel
/// dedicado já usado por <c>RenameBankWindow</c>/<c>AdminAuthWindow</c> (um diálogo que só pergunta
/// uma escolha não precisa de MVVM completo) — lê direto do <see cref="PlaylistFileStore"/>
/// (Infrastructure); esta janela já é Presentation, pode conhecer os dois lados, mesmo papel que
/// <c>PlaylistDashboardCoordinator</c> cumpre pro lado HTTP da mesma Infrastructure.
///
/// Dois passos na mesma janela (Programa → Edição/Data), mesmo fluxo de dois passos já usado pela
/// dashboard web (<c>wwwroot/index.html</c>: programs-section → editions-section). Quem chama (ver
/// <c>MainViewModel.OnOpenPlaylistRequested</c>) só lê <see cref="SelectedProgram"/>/
/// <see cref="SelectedEdition"/> depois de <see cref="Window.ShowDialog"/> devolver <c>true</c>.
///
/// Nunca lança: qualquer erro de disco ao listar Programas/Edições vira só uma mensagem na janela,
/// sem derrubar a Cartucheira — mesma defesa de sempre neste projeto.
/// </summary>
public partial class OpenPlaylistWindow : Window
{
    private readonly string _rootFolder;
    private IReadOnlyList<PlaylistProgramRecord> _programs = Array.Empty<PlaylistProgramRecord>();
    private IReadOnlyList<PlaylistEditionRecord> _editions = Array.Empty<PlaylistEditionRecord>();
    private PlaylistProgramRecord? _currentProgram;

    public OpenPlaylistWindow(string rootFolder)
    {
        InitializeComponent();
        _rootFolder = rootFolder;
        LoadPrograms();
    }

    /// <summary>Programa escolhido — só preenchido quando <see cref="Window.DialogResult"/> fica <c>true</c>.</summary>
    public PlaylistProgramRecord? SelectedProgram { get; private set; }

    /// <summary>Edição (Data) escolhida — só preenchida quando <see cref="Window.DialogResult"/> fica <c>true</c>.</summary>
    public PlaylistEditionRecord? SelectedEdition { get; private set; }

    private void LoadPrograms()
    {
        HideError();

        var (ok, error, programs) = PlaylistFileStore.ListPrograms(_rootFolder);
        if (!ok)
        {
            ShowError(error ?? "erro desconhecido ao listar Programas.");
            return;
        }

        _programs = programs;
        _currentProgram = null;
        ProgramsListBox.ItemsSource = _programs.Select(p => p.Name).ToList();
        ProgramsPanel.Visibility = Visibility.Visible;
        EditionsPanel.Visibility = Visibility.Collapsed;
        PrimaryActionButton.Content = "Avançar";
    }

    private void LoadEditions(PlaylistProgramRecord program)
    {
        HideError();

        var (ok, error, editions) = PlaylistFileStore.ListEditions(_rootFolder, program.Id);
        if (!ok)
        {
            ShowError(error ?? "erro desconhecido ao listar Datas.");
            return;
        }

        _currentProgram = program;
        // Mais recente primeiro — mesma lógica de "qual Edição o produtor provavelmente quer
        // abrir agora" já usada implicitamente pela dashboard (lista crescente lá, mas lá o
        // produtor está criando Datas; aqui o operador está procurando a de hoje/mais recente).
        _editions = editions.OrderByDescending(e => e.DateIso).ToList();
        EditionsListBox.ItemsSource = _editions.Select(e => e.DateIso).ToList();
        EditionsTitle.Text = $"Datas de \"{program.Name}\"";
        ProgramsPanel.Visibility = Visibility.Collapsed;
        EditionsPanel.Visibility = Visibility.Visible;
        PrimaryActionButton.Content = "Abrir";
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }

    private void HideError()
    {
        ErrorText.Visibility = Visibility.Collapsed;
        ErrorText.Text = string.Empty;
    }

    /// <summary>
    /// Mesmo botão muda de ação conforme o passo atual — "Avançar" (escolhe o Programa, mostra as
    /// Datas) enquanto <see cref="ProgramsPanel"/> está visível, "Abrir" (confirma a Data e fecha o
    /// diálogo) quando já está em <see cref="EditionsPanel"/>.
    /// </summary>
    private void PrimaryActionButton_Click(object sender, RoutedEventArgs e)
    {
        if (ProgramsPanel.Visibility == Visibility.Visible)
        {
            OpenSelectedProgram();
        }
        else
        {
            ConfirmSelectedEdition();
        }
    }

    private void ProgramsListBox_MouseDoubleClick(object sender, MouseButtonEventArgs e) => OpenSelectedProgram();

    private void EditionsListBox_MouseDoubleClick(object sender, MouseButtonEventArgs e) => ConfirmSelectedEdition();

    private void OpenSelectedProgram()
    {
        var index = ProgramsListBox.SelectedIndex;
        if (index < 0 || index >= _programs.Count)
        {
            ShowError("Escolha um Programa primeiro.");
            return;
        }

        LoadEditions(_programs[index]);
    }

    private void ConfirmSelectedEdition()
    {
        var index = EditionsListBox.SelectedIndex;
        if (_currentProgram is null || index < 0 || index >= _editions.Count)
        {
            ShowError("Escolha uma Data primeiro.");
            return;
        }

        SelectedProgram = _currentProgram;
        SelectedEdition = _editions[index];
        DialogResult = true;
    }

    private void BackToProgramsButton_Click(object sender, RoutedEventArgs e) => LoadPrograms();

    private void CancelButton_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
