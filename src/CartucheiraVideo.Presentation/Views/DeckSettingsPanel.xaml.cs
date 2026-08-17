using System.Windows.Controls;

namespace CartucheiraVideo.Presentation.Views;

/// <summary>
/// Code-behind mínimo — todo o comportamento vive no <c>DeckSettingsViewModel</c> (definido via
/// binding externo, pelo <c>SettingsWindow</c> que hospeda este controle, não aqui dentro). Sem
/// Salvar/Cancelar próprios (ver comentário no .xaml) — não há handler de clique nenhum pra
/// escrever, diferente do antigo <c>DeckSettingsWindow.xaml.cs</c> que este arquivo substitui.
/// </summary>
public partial class DeckSettingsPanel : UserControl
{
    public DeckSettingsPanel()
    {
        InitializeComponent();
    }
}
