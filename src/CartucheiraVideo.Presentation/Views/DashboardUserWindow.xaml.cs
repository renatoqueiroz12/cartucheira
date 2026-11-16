using System.Windows;
using CartucheiraVideo.Presentation.ViewModels;

namespace CartucheiraVideo.Presentation.Views;

/// <summary>
/// Diálogo de criar/editar usuário da dashboard de Playlist — Sprint 5 (2026-11-16). PasswordBox.Password
/// não é bindável (restrição de segurança do próprio controle, mesmo motivo documentado em
/// AdminAuthWindow) — escrito na ViewModel via code-behind (PasswordChanged), igual ao padrão já
/// usado lá. <see cref="EnteredPassword"/> fica vazio se o campo não for preenchido — editando um
/// usuário existente, vazio significa "manter a senha atual" (ver
/// PlaylistSettingsViewModel.SaveDashboardUser); criando um usuário novo, vazio é rejeitado na
/// validação (<see cref="Save_Click"/>).
/// </summary>
public partial class DashboardUserWindow : Window
{
    public DashboardUserWindow(DashboardUserEditViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel ?? throw new ArgumentNullException(nameof(viewModel));

        PasswordLabel.Text = viewModel.IsNewUser
            ? "Senha"
            : "Nova senha (deixe em branco para manter a atual)";

        if (viewModel.IsNewUser)
        {
            UsernameTextBox.Focus();
        }
        else
        {
            PasswordBox.Focus();
        }
    }

    public DashboardUserEditViewModel ViewModel => (DashboardUserEditViewModel)DataContext;

    /// <summary>Senha digitada no <see cref="PasswordBox"/> — vazia se o campo não foi tocado (ver comentário de classe).</summary>
    public string EnteredPassword { get; private set; } = string.Empty;

    private void PasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        EnteredPassword = PasswordBox.Password;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.ErrorMessage = string.Empty;

        if (string.IsNullOrWhiteSpace(ViewModel.Username))
        {
            ViewModel.ErrorMessage = "o nome de usuário não pode ser vazio.";
            return;
        }

        if (ViewModel.IsNewUser && string.IsNullOrWhiteSpace(EnteredPassword))
        {
            ViewModel.ErrorMessage = "defina uma senha pra este usuário novo.";
            return;
        }

        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
