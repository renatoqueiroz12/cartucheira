using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using CartucheiraVideo.Presentation.ViewModels;

namespace CartucheiraVideo.Presentation.Views;

public partial class MainWindow : Window
{
    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }

    /// <summary>
    /// Realca o Deck ("solte aqui") quando um arraste do Explorer passa por cima dele. So aceita
    /// o formato de payload proprio da aplicacao (DragPayload.MediaPathFormat).
    /// </summary>
    private void DeckBorder_DragEnter(object sender, DragEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: DeckViewModel deck } && e.Data.GetDataPresent(DragPayload.MediaPathFormat))
        {
            deck.IsDragOver = true;
            e.Effects = DragDropEffects.Copy;
        }
        else
        {
            e.Effects = DragDropEffects.None;
        }

        e.Handled = true;
    }

    private void DeckBorder_DragLeave(object sender, DragEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: DeckViewModel deck })
        {
            deck.IsDragOver = false;
        }
    }

    /// <summary>
    /// Solta um arquivo num Deck especifico. Nesta sprint (foco em UX) sempre aceita, mesmo se o
    /// Deck estiver tocando — a regra de bloquear isso fica para uma sprint futura, quando o
    /// estado Cue/Playing for ligado ao Engine de verdade (ver arquitetura §11).
    /// </summary>
    private void DeckBorder_Drop(object sender, DragEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: DeckViewModel deck })
        {
            deck.IsDragOver = false;

            if (e.Data.GetDataPresent(DragPayload.MediaPathFormat) &&
                e.Data.GetData(DragPayload.MediaPathFormat) is string fileName &&
                DataContext is MainViewModel mainViewModel)
            {
                mainViewModel.LoadIntoDeck(deck, fileName);
            }
        }

        e.Handled = true;
    }

    /// <summary>
    /// Disambigua clique direito simples (seleciona/deseleciona o cartucho, arquitetura §14.2 —
    /// prepara o disparo por comando externo da próxima parte) de Ctrl+clique direito (abre o
    /// ContextMenu de Cor/Loop do próprio Button, arquitetura §14.4) — Sprint Cartucheira 2, parte 2
    /// (2026-08-10). PreviewMouseRightButtonDown (tunneling) roda ANTES do WPF decidir se abre o
    /// ContextMenu sozinho: sem Ctrl, seleciona e marca e.Handled=true (cancela a abertura
    /// automática do menu); com Ctrl, não faz nada aqui e deixa o evento seguir — o WPF abre o
    /// ContextMenu do Button normalmente (comportamento padrão de clique direito num elemento com
    /// ContextMenu definido).
    /// </summary>
    private void PadButton_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            return;
        }

        if (sender is FrameworkElement { DataContext: VtButtonViewModel pad })
        {
            pad.ToggleSelected();
        }

        e.Handled = true;
    }

    /// <summary>
    /// Correção pós-entrega (2026-08-10) — usuário reportou que, rodando de verdade no Windows, um
    /// clique com o botão ESQUERDO num cartucho (sem Ctrl) também selecionava o cartucho E abria a
    /// janela de Cor/Loop, ao mesmo tempo. Isso não bate com nenhum caminho de código deste arquivo:
    /// só <see cref="PadButton_PreviewMouseRightButtonDown"/> abre esse ContextMenu (via
    /// comportamento padrão do WPF pra clique direito, deixado passar quando Ctrl está pressionado),
    /// e a Command do clique esquerdo (VtButtonViewModel.PlayAudioCommand) só toca/para ou desmarca
    /// — nunca abre menu nenhum. Ou seja, ALGUMA outra via de abertura de ContextMenu do WPF (ex.:
    /// gesto "pressionar e segurar" de touchpad/touchscreen sendo promovido a clique direito pelo
    /// Windows, tecla Aplicativos/Shift+F10, etc.) está driblando o
    /// <c>PadButton_PreviewMouseRightButtonDown</c> — que só intercepta o botão direito do MOUSE
    /// especificamente, não qualquer gesto que possa abrir um ContextMenu.
    ///
    /// Correção defensiva, cobre qualquer via: o evento <c>ContextMenuOpening</c> (roteado, dispara
    /// pouco antes do ContextMenu abrir de verdade, não importa o gesto que o disparou) agora sempre
    /// cancela a abertura (<c>e.Handled = true</c>) a menos que Ctrl esteja pressionado NAQUELE
    /// instante — última trava, depois de qualquer outra lógica que já tenha rodado antes. Não muda
    /// nada do fluxo já correto de Ctrl+clique direito (que continua deixando o evento passar e o
    /// ContextMenu abre normalmente).
    /// </summary>
    private void PadButton_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            e.Handled = true;
        }
    }

    /// <summary>
    /// Abre o seletor de cor nativo do Windows (System.Windows.Forms.ColorDialog, FullOpen=false —
    /// mostra só a grade de "Cores básicas") pra escolher a cor de UM cartucho — Sprint Cartucheira
    /// 2, parte 2, correção (2026-08-10). Substitui o antigo submenu "Cor" (7 swatches) do
    /// ContextMenu: o ControlTemplate de MenuItem em Theme.xaml é deliberadamente simples (sem
    /// Popup/ItemsPresenter pra submenu, ver comentário lá) — um MenuItem com filhos não tinha onde
    /// renderizar o flyout, causando o menu "cortado" que o usuário reportou. A cor escolhida vira
    /// hex e é aplicada via o mesmo <see cref="VtButtonViewModel.SetColorCommand"/> que o antigo
    /// submenu já usava (nenhuma mudança na ViewModel nem na persistência) — só troca de onde vem o
    /// hex: antes uma CommandParameter fixa de um swatch, agora a cor escolhida no diálogo.
    /// Tipos totalmente qualificados de propósito (sem `using System.Windows.Forms;`), pra não
    /// colidir com os tipos de mesmo nome do WPF (Application/MessageBox/etc.) já usados no resto do
    /// projeto — mesmo racional dos aliases de tipo já usados pro LibVLCSharp.
    /// </summary>
    private void ColorMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: VtButtonViewModel pad })
        {
            return;
        }

        using var dialog = new System.Windows.Forms.ColorDialog
        {
            FullOpen = false,
            AnyColor = true
        };

        if (TryParseHexColor(pad.Color, out var current))
        {
            dialog.Color = current;
        }

        var owner = new Win32Window(new WindowInteropHelper(this).Handle);

        if (dialog.ShowDialog(owner) == System.Windows.Forms.DialogResult.OK)
        {
            var hex = $"#FF{dialog.Color.R:X2}{dialog.Color.G:X2}{dialog.Color.B:X2}";
            pad.SetColorCommand.Execute(hex);
        }
    }

    /// <summary>Converte o hex do cartucho (ex.: "#FF2D7DFF") pra System.Drawing.Color, pra pré-selecionar a cor atual quando o ColorDialog abre. Retorna falso (sem pré-seleção, o ColorDialog abre na cor padrão dele) se o hex não for válido — nunca lança.</summary>
    private static bool TryParseHexColor(string hex, out System.Drawing.Color color)
    {
        color = default;

        try
        {
            var media = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex);
            color = System.Drawing.Color.FromArgb(media.A, media.R, media.G, media.B);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    /// <summary>Wrapper mínimo pra dar ao ColorDialog (WinForms) a janela WPF como "dono" (IWin32Window), pra ele abrir centralizado sobre ela em vez de solto na tela.</summary>
    private sealed class Win32Window : System.Windows.Forms.IWin32Window
    {
        public Win32Window(IntPtr handle) => Handle = handle;

        public IntPtr Handle { get; }
    }
}
