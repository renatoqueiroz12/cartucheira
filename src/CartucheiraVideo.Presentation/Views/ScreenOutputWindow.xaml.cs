using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
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
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;

    public ScreenOutputWindow(string deviceName)
    {
        InitializeComponent();
        DeviceName = deviceName;
    }

    /// <summary>Identificador estável do monitor Windows (<c>System.Windows.Forms.Screen.DeviceName</c>) que esta janela ocupa — usado por <c>DeckViewModel.RefreshScreenOutputs</c> pra saber qual janela já existe pra qual monitor.</summary>
    public string DeviceName { get; }

    /// <summary>
    /// Fix v1.0.58 (2026-11-11): incidente real — usuário com dois monitores de DPI (escala)
    /// diferente confirmou que marcar só "Tela 2" (o monitor menor) mandava o vídeo pro monitor
    /// ERRADO (o ultra-wide, onde ele próprio estava). Causa raiz: o truque antigo daqui (definir
    /// <c>Left</c>/<c>Top</c> em pixels FÍSICOS de <c>Screen.Bounds</c> e deixar o WPF "descobrir"
    /// o monitor maximizando) só funciona quando todos os monitores têm o MESMO fator de escala —
    /// <c>Window.Left</c>/<c>Top</c> do WPF são unidades independentes de dispositivo (96 DPI),
    /// não pixels físicos; com monitores de DPI diferentes, o mesmo valor em pixels físicos cai em
    /// posições diferentes no espaço lógico do WPF, e a janela acaba maximizando no monitor ERRADO
    /// (o comentário antigo descrevia isto como seguro — não era, nesse cenário).
    ///
    /// Corrigido indo direto pro Win32: <c>SetWindowPos</c> (<c>user32.dll</c>) posiciona/redimensiona
    /// pelo HWND em pixels FÍSICOS de verdade, sem nenhuma conversão de unidade — funciona
    /// corretamente mesmo com monitores de DPI diferentes, desde que o processo seja "Per-Monitor-V2
    /// DPI aware" (modo padrão do WPF no .NET Core 3+/.NET 5+ sem manifesto customizado, que é o caso
    /// deste projeto — confirmado sem nenhum app.manifest nem override de DPI no .csproj). Usa
    /// <see cref="WindowInteropHelper.EnsureHandle"/> pra garantir o HWND nativo antes do
    /// <c>Show()</c> (mesmo ponto de chamada de antes, em <c>DeckViewModel.RefreshScreenOutputs</c>).
    /// <see cref="WindowState"/> fica em <see cref="WindowState.Normal"/> (nunca <c>Maximized</c>) —
    /// como já fixamos o retângulo exatamente nos limites físicos do monitor, maximizar seria
    /// redundante e reintroduziria o mesmo risco de conversão de unidade que estamos evitando.
    /// </summary>
    public void PositionOnScreen(System.Windows.Forms.Screen screen)
    {
        WindowState = WindowState.Normal;

        var hwnd = new WindowInteropHelper(this).EnsureHandle();
        SetWindowPos(hwnd, IntPtr.Zero, screen.Bounds.X, screen.Bounds.Y, screen.Bounds.Width, screen.Bounds.Height, SWP_NOZORDER | SWP_NOACTIVATE);
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

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
