using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CartucheiraVideo.Presentation.ViewModels;

namespace CartucheiraVideo.Presentation.Views;

public partial class FileExplorerPanel : UserControl
{
    private Point _dragStartPoint;
    private bool _dragCandidate;

    public FileExplorerPanel()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Duplo clique num item da lista carrega o arquivo no deck ativo. Tratado aqui (code-behind)
    /// porque ListBox nao tem um comando nativo de "ativar item" sem trazer um pacote extra de
    /// behaviors — o unico papel deste handler e repassar o item pro ViewModel.
    /// </summary>
    private void FilesListBox_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is FrameworkElement { DataContext: FileSystemItemViewModel item } &&
            DataContext is FileExplorerViewModel viewModel)
        {
            viewModel.ActivateFile(item);
        }
    }

    /// <summary>Mesmo papel do handler acima, so que pra aba Playlist (mockup v8, nota #11) — repassa pro PlaylistViewModel.ActivateItem.</summary>
    private void PlaylistListBox_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is FrameworkElement { DataContext: PlaylistItemViewModel item } &&
            DataContext is FileExplorerViewModel viewModel)
        {
            viewModel.Playlist.ActivateItem(item);
        }
    }

    /// <summary>
    /// Selecionar um nó na árvore de pastas troca a lista de arquivos exibida. TreeViewItem.IsSelected
    /// não tem um "comando de seleção" nativo, então a troca é feita chamando o SelectCommand do
    /// FolderNodeViewModel diretamente — o mesmo papel que MouseDoubleClick cumpre pra lista de
    /// arquivos, só que pro evento Selected do TreeViewItem.
    /// </summary>
    private void TreeViewItem_Selected(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is FrameworkElement { DataContext: FolderNodeViewModel node })
        {
            node.SelectCommand.Execute(null);
        }

        e.Handled = true;
    }

    private void FilesListBox_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragStartPoint = e.GetPosition(null);
        _dragCandidate = e.OriginalSource is FrameworkElement { DataContext: FileSystemItemViewModel };
    }

    /// <summary>
    /// Inicia um drag nativo do WPF quando o usuario arrasta um item da lista alem da distancia
    /// minima de arraste do sistema. O payload e o CAMINHO COMPLETO do arquivo (FullPath — desde
    /// a Sprint Engine; antes era so o nome de exibicao, o que bastava pra simulacao mas nao da
    /// pra abrir o arquivo de verdade no LibVLC), num formato proprio da aplicacao (evita
    /// confundir com drag de arquivo do Windows Explorer de verdade).
    /// </summary>
    private void FilesListBox_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragCandidate || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        var current = e.GetPosition(null);
        var distance = current - _dragStartPoint;

        if (Math.Abs(distance.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(distance.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        if (e.OriginalSource is not FrameworkElement { DataContext: FileSystemItemViewModel item })
        {
            return;
        }

        _dragCandidate = false;

        var data = new DataObject(DragPayload.MediaPathFormat, item.FullPath);
        DragDrop.DoDragDrop((DependencyObject)sender, data, DragDropEffects.Copy);
    }
}
