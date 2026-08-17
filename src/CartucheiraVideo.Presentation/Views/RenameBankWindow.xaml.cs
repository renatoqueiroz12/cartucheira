using System.Windows;

namespace CartucheiraVideo.Presentation.Views;

/// <summary>
/// Dialogo minimo pra renomear um banco de botoes. Unica janela nova desta sprint — usada so
/// pra essa acao de configuracao pontual, nao faz parte do fluxo de operacao ao vivo.
/// </summary>
public partial class RenameBankWindow : Window
{
    public RenameBankWindow(string currentName)
    {
        InitializeComponent();
        NameTextBox.Text = currentName;
        NameTextBox.Focus();
        NameTextBox.SelectAll();
    }

    public string? ResultName { get; private set; }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        ResultName = NameTextBox.Text;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
