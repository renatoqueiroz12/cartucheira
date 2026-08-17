using System.Collections.ObjectModel;
using System.Windows.Input;
using CartucheiraVideo.Core;
using CartucheiraVideo.Presentation.Mvvm;

namespace CartucheiraVideo.Presentation.ViewModels;

/// <summary>
/// Representa um <see cref="MediaFolder"/> num TreeView. Envolve o objeto de dominio (arquitetura
/// §12: o Core so conhece MediaFolder — quem sabe exibir numa arvore expansivel e esta classe, que
/// vive na Presentation).
///
/// Lazy loading real (nao so "estrutura pronta"): cada no so sabe seus filhos diretos quando
/// <see cref="Folder"/>.<see cref="MediaFolder.ChildrenLoaded"/> for verdadeiro. Um no cujos filhos
/// ainda nao foram escaneados, mas que tem pelo menos uma subpasta (checagem barata, sem listar o
/// conteudo — ver FileExplorerViewModel.HasAnySubfolder), ganha um unico filho placeholder so pra
/// o TreeView desenhar a seta de expandir; o scan de verdade (1 nivel) acontece na primeira
/// expansao, via <see cref="EnsureChildrenLoaded"/> — decisao tomada na Sprint 6 pra nao arriscar
/// latencia/estabilidade escaneando recursivamente pastas profundas no startup (prioridade #1 do
/// projeto), mesmo o usuario tendo aceitado eager loading como opcao valida.
/// </summary>
public sealed class FolderNodeViewModel : ViewModelBase
{
    private readonly Action<FolderNodeViewModel> _onSelected;
    private readonly Action<FolderNodeViewModel>? _onLoadChildren;
    private bool _isExpanded;
    private bool _isSelected;

    public FolderNodeViewModel(MediaFolder folder, Action<FolderNodeViewModel> onSelected, Action<FolderNodeViewModel>? onLoadChildren = null)
    {
        Folder = folder ?? throw new ArgumentNullException(nameof(folder));
        _onSelected = onSelected ?? throw new ArgumentNullException(nameof(onSelected));
        _onLoadChildren = onLoadChildren;

        SelectCommand = new RelayCommand(_ => _onSelected(this));
    }

    /// <summary>Cria um nó temporário só pra o TreeView mostrar a seta de expandir antes do scan real (ver comentário de classe).</summary>
    public static FolderNodeViewModel CreatePlaceholder() =>
        new(new MediaFolder { Name = "Carregando...", Path = string.Empty, ChildrenLoaded = true }, _ => { });

    /// <summary>O objeto de dominio que este no representa.</summary>
    public MediaFolder Folder { get; }

    public string Name => Folder.Name;

    public string Path => Folder.Path;

    public ObservableCollection<FolderNodeViewModel> Children { get; } = new();

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (SetProperty(ref _isExpanded, value) && value)
            {
                EnsureChildrenLoaded();
            }
        }
    }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    public ICommand SelectCommand { get; }

    /// <summary>
    /// Dispara o scan raso (1 nível) deste nó na primeira expansão, substituindo o placeholder
    /// pelos filhos de verdade. No-op se já carregado ou se este nó não tem carregador (ex.: o
    /// próprio placeholder).
    /// </summary>
    public void EnsureChildrenLoaded()
    {
        if (!Folder.ChildrenLoaded)
        {
            _onLoadChildren?.Invoke(this);
        }
    }
}
