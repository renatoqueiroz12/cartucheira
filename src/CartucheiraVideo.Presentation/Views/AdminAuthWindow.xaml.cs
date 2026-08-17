using System.Windows;
using CartucheiraVideo.Presentation.ViewModels;

namespace CartucheiraVideo.Presentation.Views;

/// <summary>
/// Diálogo modal de senha de admin (definir na primeira vez, login nas seguintes) — Sprint Config
/// Geral, parte 1 (2026-08-09). PasswordBox.Password não é bindável (restrição de segurança do
/// próprio controle), então os dois campos escrevem na ViewModel via code-behind
/// (PasswordChanged), igual ao NameTextBox do RenameBankWindow ser acessado por nome. Só fecha com
/// DialogResult=true quando <see cref="AdminAuthViewModel.TryConfirm"/> devolve sucesso — em caso
/// de erro a janela permanece aberta, mostrando ErrorMessage, pra nova tentativa.
/// </summary>
public partial class AdminAuthWindow : Window
{
    public AdminAuthWindow(AdminAuthViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        PasswordBox.Focus();
    }

    public AdminAuthViewModel ViewModel => (AdminAuthViewModel)DataContext;

    private void PasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        ViewModel.Password = PasswordBox.Password;
    }

    private void ConfirmPasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        ViewModel.ConfirmPassword = ConfirmPasswordBox.Password;
    }

    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.TryConfirm())
        {
            DialogResult = true;
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
