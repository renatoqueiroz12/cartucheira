using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace CartucheiraVideo.Presentation.Views;

/// <summary>
/// Janela de saída do Teleprompter — Sprint Teleprompter (2026-11-16). Ver comentário completo no
/// XAML sobre a escolha de TextBox (não TextBlock) e o espelhamento. Gerenciada inteiramente pelo
/// <see cref="ViewModels.TeleprompterViewModel"/> (mesmo papel que <c>DeckViewModel</c> tem sobre
/// <see cref="ScreenOutputWindow"/>) — sem ViewModel própria.
///
/// Todo método público aqui assume que está sendo chamado na UI thread — quem chama
/// (<see cref="ViewModels.TeleprompterViewModel"/>) é responsável por já ter marshalado antes,
/// mesmo contrato de sempre deste projeto pra eventos que nascem numa thread de áudio/captura (ver
/// comentário de <c>Engine.TeleprompterVoiceEngine.ScriptPositionChanged</c>).
/// </summary>
public partial class TeleprompterOutputWindow : Window
{
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;

    /// <summary>Onde a "linha de leitura atual" fica fixa na tela, como fração da altura da janela de cima pra baixo — 35% (um pouco acima do centro) é o valor comum em teleprompters comerciais, perto da altura dos olhos de quem está de frente pra câmera logo abaixo da tela.</summary>
    private const double CueLineFraction = 0.35;

    public TeleprompterOutputWindow(string deviceName)
    {
        InitializeComponent();
        DeviceName = deviceName;
        SizeChanged += (_, _) => PositionCueLine();
        Loaded += (_, _) => PositionCueLine();
    }

    /// <summary>Identificador estável do monitor Windows (<see cref="System.Windows.Forms.Screen.DeviceName"/>) que esta janela ocupa — mesmo papel de <see cref="ScreenOutputWindow.DeviceName"/>.</summary>
    public string DeviceName { get; }

    /// <summary>Mesmo truque de posicionamento via Win32 <c>SetWindowPos</c> (pixels físicos, sem conversão de unidade) já corrigido/documentado em <see cref="ScreenOutputWindow.PositionOnScreen"/> — ver comentário completo lá (Fix v1.0.58, monitores com DPI diferente).</summary>
    public void PositionOnScreen(System.Windows.Forms.Screen screen)
    {
        WindowState = WindowState.Normal;

        var hwnd = new WindowInteropHelper(this).EnsureHandle();
        SetWindowPos(hwnd, IntPtr.Zero, screen.Bounds.X, screen.Bounds.Y, screen.Bounds.Width, screen.Bounds.Height, SWP_NOZORDER | SWP_NOACTIVATE);
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    /// <summary>
    /// Sprint Teleprompter-NDI (2026-11-17): posiciona a janela fora dos limites de QUALQUER monitor
    /// real combinado (<see cref="SystemParameters.VirtualScreenLeft"/>/<c>VirtualScreenWidth</c> —
    /// a mesma API que o Windows usa pra descrever "todos os monitores juntos") — usado só quando a
    /// saída NDI está ligada e nenhuma tela física foi escolhida pro Teleprompter (ver
    /// <see cref="ViewModels.TeleprompterViewModel.RefreshWindow"/>). A janela continua existindo e
    /// renderizando de verdade (<see cref="RenderToUyvy"/> funciona igual, não importa onde ela
    /// esteja na tela) — só não aparece em nenhum monitor físico, mesmo truque de "janela sempre
    /// quente escondida" já usado por outras janelas deste programa, aplicado aqui pela primeira vez
    /// SEM nenhum monitor real por trás. Canvas fixo 1920x1080 (<see cref="BackgroundArtRenderer.FrameWidth"/>/
    /// <c>FrameHeight</c>, mesmo canvas padrão do resto do projeto) — sem monitor nenhum pra copiar a
    /// resolução.
    /// </summary>
    public void PositionOffScreen()
    {
        WindowState = WindowState.Normal;

        var x = (int)Math.Round(SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth) + 256;
        const int y = 0;

        var hwnd = new WindowInteropHelper(this).EnsureHandle();
        SetWindowPos(hwnd, IntPtr.Zero, x, y, BackgroundArtRenderer.FrameWidth, BackgroundArtRenderer.FrameHeight, SWP_NOZORDER | SWP_NOACTIVATE);
    }

    /// <summary>Troca o texto exibido (nova Lauda carregada no Deck fonte) e volta a rolagem pro início — chamado pelo <see cref="ViewModels.TeleprompterViewModel"/> toda vez que o texto muda.</summary>
    public void SetText(string text)
    {
        PromptTextBox.Text = text ?? string.Empty;
        PromptTextBox.ScrollToHome();
    }

    public void SetFontSize(double pointSize)
    {
        PromptTextBox.FontSize = pointSize;
    }

    /// <summary>Espelha o texto horizontalmente (ver comentário no XAML) — liga/desliga um <see cref="ScaleTransform"/> no Grid raiz.</summary>
    public void SetMirror(bool mirrored)
    {
        // RenderTransform (não LayoutTransform): só espelha os PIXELS já desenhados, sem forçar
        // um novo passo de layout — RenderTransformOrigin="0.5,0.5" (XAML) garante que o espelho
        // gira em torno do centro da janela, não do canto, então o conteúdo continua ocupando a
        // tela inteira em vez de "sair" pela esquerda.
        RootGrid.RenderTransform = mirrored ? new ScaleTransform(-1, 1) : Transform.Identity;
    }

    /// <summary>
    /// Rola o texto pra trazer o caractere <paramref name="charOffset"/> (deslocamento no MESMO
    /// texto passado a <see cref="SetText"/>) até a "linha de leitura atual" (<see cref="CueLine"/>,
    /// <see cref="CueLineFraction"/> da altura da janela) — chamado pelo acompanhamento automático
    /// por voz a cada palavra casada. Scroll manual (roda do mouse/setas, ver
    /// <see cref="OnPreviewKeyDown"/>) continua funcionando livremente entre uma chamada e outra —
    /// esta chamada só define onde a voz ACHA que a leitura está agora, nunca trava a rolagem lá.
    /// </summary>
    public void SetScriptPosition(int charOffset)
    {
        var text = PromptTextBox.Text;
        if (text.Length == 0)
        {
            return;
        }

        var clampedOffset = Math.Clamp(charOffset, 0, text.Length - 1);
        Rect rect;
        try
        {
            rect = PromptTextBox.GetRectFromCharacterIndex(clampedOffset);
        }
        catch (Exception)
        {
            // GetRectFromCharacterIndex pode falhar antes do layout do TextBox estar pronto (ex.:
            // texto trocado no mesmo instante em que a janela ainda está abrindo) — ignora esta
            // chamada em vez de derrubar o programa; a próxima palavra casada tenta de novo.
            return;
        }

        var targetContentY = PromptTextBox.VerticalOffset + rect.Top;
        var desiredViewportY = PromptTextBox.ViewportHeight * CueLineFraction;
        PromptTextBox.ScrollToVerticalOffset(Math.Max(0, targetContentY - desiredViewportY));
    }

    /// <summary>Controle manual sempre disponível — setas/Page Up/Down tratadas aqui (a própria janela, não o TextBox — ver comentário no XAML sobre Focusable="False") porque o TextBox nunca recebe foco de teclado.</summary>
    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Up:
                PromptTextBox.LineUp();
                e.Handled = true;
                break;
            case Key.Down:
                PromptTextBox.LineDown();
                e.Handled = true;
                break;
            case Key.PageUp:
                PromptTextBox.PageUp();
                e.Handled = true;
                break;
            case Key.PageDown:
                PromptTextBox.PageDown();
                e.Handled = true;
                break;
            case Key.Home:
                PromptTextBox.ScrollToHome();
                e.Handled = true;
                break;
            case Key.End:
                PromptTextBox.ScrollToEnd();
                e.Handled = true;
                break;
        }

        base.OnPreviewKeyDown(e);
    }

    /// <summary>Roda do mouse — o TextBox já rolaria sozinho, mas está com <c>Focusable="False"</c> (não recebe o evento) e, de propósito, sem VerticalScrollBarVisibility visível; trata aqui pra ficar consistente com as teclas acima.</summary>
    protected override void OnPreviewMouseWheel(MouseWheelEventArgs e)
    {
        const int linesPerNotch = 3;
        var lines = Math.Abs(e.Delta) / 120 * linesPerNotch;
        for (var i = 0; i < Math.Max(1, lines); i++)
        {
            if (e.Delta > 0)
            {
                PromptTextBox.LineUp();
            }
            else
            {
                PromptTextBox.LineDown();
            }
        }

        e.Handled = true;
        base.OnPreviewMouseWheel(e);
    }

    private void PositionCueLine()
    {
        CueLine.Margin = new Thickness(0, ActualHeight * CueLineFraction, 0, 0);
    }

    /// <summary>
    /// Sprint Teleprompter-NDI (2026-11-17): renderiza <see cref="RootGrid"/> (texto, rolagem,
    /// espelho — exatamente o que já está desenhado agora, numa tela física ou escondida, ver
    /// <see cref="PositionOffScreen"/>) pra um quadro UYVY, pronto pra <c>Ndi.NdiSender.SendFrame</c>.
    /// Chamado pelo <see cref="ViewModels.TeleprompterViewModel"/> de dentro de um
    /// <see cref="System.Windows.Threading.DispatcherTimer"/> — PRECISA rodar na UI thread
    /// (<see cref="RenderTargetBitmap.Render"/> é uma operação WPF normal, mesmo contrato de
    /// qualquer acesso à árvore visual). Usa o tamanho ATUAL da janela
    /// (<see cref="FrameworkElement.ActualWidth"/>/<c>ActualHeight</c>) — reflete tanto o canvas fixo
    /// 1920x1080 de <see cref="PositionOffScreen"/> quanto a resolução real de
    /// <see cref="PositionOnScreen"/>, sem precisar saber qual dos dois está em uso.
    ///
    /// Falha honesta: antes do primeiro layout (<see cref="ActualWidth"/>/<c>ActualHeight</c> ainda
    /// zero) ou qualquer problema de renderização (ex.: janela sendo fechada no mesmo instante deste
    /// tick do timer), retorna nulo — quem chama só pula este quadro, nunca derruba o Teleprompter
    /// nem o timer.
    /// </summary>
    public (byte[] Uyvy, int Width, int Height)? RenderToUyvy()
    {
        var width = (int)Math.Round(ActualWidth);
        var height = (int)Math.Round(ActualHeight);

        if (width <= 0 || height <= 0)
        {
            return null;
        }

        try
        {
            var renderTarget = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            renderTarget.Render(RootGrid);

            var strideBytes = width * 4;
            var bgra = new byte[height * strideBytes];
            renderTarget.CopyPixels(bgra, strideBytes, 0);

            var uyvy = UyvyBgraConverter.ConvertFromBgra32(bgra, width, height);
            var evenWidth = width - (width % 2); // mesmo arredondamento de ConvertFromBgra32 — largura REAL do buffer UYVY devolvido.

            return (uyvy, evenWidth, height);
        }
        catch (Exception)
        {
            return null;
        }
    }
}
