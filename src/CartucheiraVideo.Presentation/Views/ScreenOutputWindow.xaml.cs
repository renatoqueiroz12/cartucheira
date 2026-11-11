using System.Windows;
using System.Windows.Media.Imaging;

namespace CartucheiraVideo.Presentation.Views;

/// <summary>
/// Sprint Telas-Saida-Direta (2026-11-10): uma janela "telão" por monitor selecionado na aba Telas
/// da Configuração do Deck — sem borda, sem ícone na barra de tarefas, sempre no topo, mostrando o
/// vídeo (ou a arte de fundo, quando parado) deste Deck em cima de um monitor físico específico
/// ligado na placa de vídeo do PC. Gerenciada inteiramente pelo <c>Presentation.ViewModels.DeckViewModel</c>
/// (<c>RefreshScreenOutputs</c>) — não tem ViewModel própria, é puramente um "monitor renderizado".
/// </summary>
public partial class ScreenOutputWindow : Window
{
    public ScreenOutputWindow(string deviceName)
    {
        InitializeComponent();
        DeviceName = deviceName;
    }

    /// <summary>Identificador estável do monitor Windows (<c>System.Windows.Forms.Screen.DeviceName</c>) que esta janela ocupa — usado por <c>DeckViewModel.RefreshScreenOutputs</c> pra saber qual janela já existe pra qual monitor.</summary>
    public string DeviceName { get; }

    /// <summary>
    /// Posiciona e maximiza esta janela sobre o monitor informado — truque padrão do WPF pra ocupar
    /// de forma confiável UM monitor específico em telas com DPI diferentes entre si: definir
    /// Left/Top num ponto que cai DENTRO dos limites físicos daquele monitor e só então maximizar
    /// (WindowState=Maximized) deixa o próprio Windows calcular os limites corretos pro monitor que
    /// a posição atual sobrepõe, sem o programa precisar converter pixels físicos
    /// (<c>System.Windows.Forms.Screen.Bounds</c>) pra unidades independentes de dispositivo do WPF
    /// (Window.Left/Top, 96 DPI de referência) — conversão que seria um risco real de erro dado que
    /// cada monitor pode ter um fator de escala diferente.
    /// </summary>
    public void PositionOnScreen(System.Windows.Forms.Screen screen)
    {
        WindowState = WindowState.Normal;
        Left = screen.Bounds.X + 10;
        Top = screen.Bounds.Y + 10;
        Width = 800;
        Height = 450;
        WindowState = WindowState.Maximized;
    }

    /// <summary>
    /// Define o bitmap exibido — chamado uma vez quando a janela é aberta/reaproveitada. O MESMO
    /// objeto <see cref="WriteableBitmap"/> é compartilhado por todas as janelas deste Deck (ver
    /// <c>DeckViewModel</c>), então escrever nele uma vez (<c>WritePixels</c>) já atualiza todas as
    /// telas simultaneamente — não é preciso chamar isto de novo a cada quadro.
    /// </summary>
    public void SetSource(WriteableBitmap bitmap)
    {
        DisplayImage.Source = bitmap;
    }
}
