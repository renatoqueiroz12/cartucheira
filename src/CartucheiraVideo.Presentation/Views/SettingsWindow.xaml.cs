using System.Windows;
using CartucheiraVideo.Presentation.ViewModels;

namespace CartucheiraVideo.Presentation.Views;

/// <summary>
/// Janela de Configurações — Sprint Config Geral, parte 1 (2026-08-09). Não-modal (Show(), não
/// ShowDialog()) por decisão do usuário: o operador precisa continuar mexendo nos Decks com esta
/// janela aberta. Quem controla abrir/fechar/evitar duplicata é o MainViewModel (guarda a
/// instância e chama Activate() se já estiver aberta) — este code-behind só fecha a própria janela
/// quando "Fechar" é clicado.
/// </summary>
public partial class SettingsWindow : Window
{
    public SettingsWindow(SettingsViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
