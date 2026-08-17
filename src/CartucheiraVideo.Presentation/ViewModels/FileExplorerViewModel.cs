using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows.Input;
using CartucheiraVideo.Core;
using CartucheiraVideo.Presentation.Mvvm;
using Microsoft.Win32;

namespace CartucheiraVideo.Presentation.ViewModels;

/// <summary>
/// ViewModel do painel de Explorer. Desde a Sprint 6 (arquitetura §12), o Explorer passa a
/// consumir uma <see cref="MediaLibrary"/> (Core) em vez de conhecer pastas/arquivos diretamente:
/// escolher uma pasta raiz povoa <see cref="Library"/> com uma árvore de <see cref="MediaFolder"/>,
/// e a lista de arquivos exibida (<see cref="Files"/>) é sempre a pasta selecionada na árvore
/// (<see cref="RootFolders"/>/<see cref="FolderNodeViewModel"/>).
///
/// Ganhou a árvore de pastas de volta (revertendo a Sprint 5, item 4 — decisão fechada na rodada
/// de mockup v6/v7) e uma estrutura pronta pra lazy loading (ver <see cref="MediaFolder.ChildrenLoaded"/>
/// e <see cref="FolderNodeViewModel"/>) — hoje ainda povoada de forma eager (carrega tudo de uma
/// vez), mas sem precisar reescrever a classe quando isso mudar.
///
/// Usa Microsoft.Win32.OpenFolderDialog (nativo do WPF desde o .NET 8) — nenhuma biblioteca de
/// terceiros necessaria para escolher pasta.
/// </summary>
public sealed class FileExplorerViewModel : ViewModelBase
{
    private static readonly string[] VideoExtensions = { ".mp4", ".mov", ".mkv", ".avi", ".wmv" };

    /// <summary>Quantos arquivos recentes ficam visíveis na trilha "RECENTES" do Explorer (mockup v8) antes do mais antigo cair fora.</summary>
    private const int MaxRecentFiles = 5;

    private string _statusMessage = "Nenhuma pasta selecionada.";
    private string _searchText = string.Empty;
    private FolderNodeViewModel? _selectedFolder;
    private bool _isExplorerTabSelected = true;

    public FileExplorerViewModel()
    {
        ChooseFolderCommand = new RelayCommand(_ => ChooseFolder());
        ReloadCommand = new RelayCommand(_ => Reload(), _ => SelectedFolder is not null);
        SelectExplorerTabCommand = new RelayCommand(_ => IsExplorerTabSelected = true);
        SelectPlaylistTabCommand = new RelayCommand(_ => IsExplorerTabSelected = false);
        ActivateRecentCommand = new RelayCommand(parameter =>
        {
            if (parameter is FileSystemItemViewModel item)
            {
                ActivateFile(item);
            }
        });

        Playlist = new PlaylistViewModel(message => StatusMessage = message);

        RecentFiles.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasRecentFiles));
        FavoriteFolders.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasFavorites));

        var defaultFolder = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
        if (!string.IsNullOrWhiteSpace(defaultFolder) && Directory.Exists(defaultFolder))
        {
            LoadRoot(defaultFolder);
        }
    }

    /// <summary>Disparado quando o usuario ativa um arquivo (duplo clique, ou item recente) para carrega-lo no deck ativo.</summary>
    public event Action<FileSystemItemViewModel>? FileActivated;

    /// <summary>
    /// Explorer (pastas no disco) x Playlist (sequência externa) — mockup v8, nota #11. As duas
    /// abas dividem o mesmo painel (FileExplorerPanel.xaml); esta propriedade controla qual delas
    /// está visível.
    /// </summary>
    public bool IsExplorerTabSelected
    {
        get => _isExplorerTabSelected;
        private set
        {
            if (SetProperty(ref _isExplorerTabSelected, value))
            {
                OnPropertyChanged(nameof(IsPlaylistTabSelected));
            }
        }
    }

    public bool IsPlaylistTabSelected => !IsExplorerTabSelected;

    /// <summary>Aba Playlist — ver comentário de classe em <see cref="PlaylistViewModel"/>.</summary>
    public PlaylistViewModel Playlist { get; }

    public ICommand SelectExplorerTabCommand { get; }

    public ICommand SelectPlaylistTabCommand { get; }

    /// <summary>
    /// Arquivos recentemente carregados em algum Deck (trilha "RECENTES" do rail do Explorer,
    /// mockup v8, nota #4) — mais recente primeiro. Diferente de Favoritos (ver
    /// <see cref="FavoriteFolders"/>), esta lista é populada automaticamente e de verdade: toda
    /// vez que MainViewModel.LoadIntoDeck carrega um arquivo, ele entra aqui via
    /// <see cref="PushRecent"/> — não é dado de exemplo.
    /// </summary>
    public ObservableCollection<FileSystemItemViewModel> RecentFiles { get; } = new();

    public bool HasRecentFiles => RecentFiles.Count > 0;

    public ICommand ActivateRecentCommand { get; }

    /// <summary>
    /// Pastas favoritas (trilha "FAVORITOS" do rail, mockup v8, nota #4). Gerenciar favoritos
    /// (adicionar/remover) ainda não foi implementado — fica vazia por enquanto, honestamente,
    /// em vez de nascer com pastas de exemplo que o operador não escolheu.
    /// </summary>
    public ObservableCollection<FolderNodeViewModel> FavoriteFolders { get; } = new();

    public bool HasFavorites => FavoriteFolders.Count > 0;

    /// <summary>Biblioteca de midia (Core) — o Explorer consome isto, nao pastas/arquivos crus.</summary>
    public MediaLibrary Library { get; } = new();

    /// <summary>Raizes da arvore de pastas exibida no Explorer (tipicamente uma, a pasta escolhida pelo usuario).</summary>
    public ObservableCollection<FolderNodeViewModel> RootFolders { get; } = new();

    public FolderNodeViewModel? SelectedFolder
    {
        get => _selectedFolder;
        private set
        {
            if (SetProperty(ref _selectedFolder, value))
            {
                ((RelayCommand)ReloadCommand).RaiseCanExecuteChanged();
                OnPropertyChanged(nameof(FolderPath));
            }
        }
    }

    /// <summary>Caminho da pasta atualmente selecionada na arvore — exibido na barra inferior.</summary>
    public string FolderPath => SelectedFolder?.Path ?? string.Empty;

    /// <summary>Arquivos de midia da pasta selecionada (apos filtro de busca).</summary>
    public ObservableCollection<FileSystemItemViewModel> Files { get; } = new();

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value))
            {
                ApplyFilter();
            }
        }
    }

    public RelayCommand ChooseFolderCommand { get; }

    public ICommand ReloadCommand { get; }

    /// <summary>Chamado pela View quando o usuario da duplo clique num arquivo da lista.</summary>
    public void ActivateFile(FileSystemItemViewModel item) => FileActivated?.Invoke(item);

    /// <summary>
    /// Registra um arquivo como "recentemente carregado" (chamado por MainViewModel.LoadIntoDeck,
    /// pra todo carregamento real, não só os que vieram do Explorer) — remove duplicata existente
    /// e insere no topo, mantendo no máximo <see cref="MaxRecentFiles"/> itens.
    /// </summary>
    public void PushRecent(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return;
        }

        var existing = RecentFiles.FirstOrDefault(f => string.Equals(f.FullPath, filePath, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            RecentFiles.Remove(existing);
        }

        RecentFiles.Insert(0, new FileSystemItemViewModel(filePath));

        while (RecentFiles.Count > MaxRecentFiles)
        {
            RecentFiles.RemoveAt(RecentFiles.Count - 1);
        }
    }

    private void ChooseFolder()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Selecionar pasta de vídeos"
        };

        if (dialog.ShowDialog() == true)
        {
            LoadRoot(dialog.FolderName);
        }
    }

    private void Reload()
    {
        if (SelectedFolder is not null)
        {
            LoadRoot(RootFolders[0].Path);
        }
    }

    /// <summary>
    /// Escaneia só o nível raiz (1 nível: subpastas diretas + arquivos diretos) e monta o nó raiz
    /// da árvore. Os demais níveis são escaneados sob demanda, na primeira expansão de cada nó
    /// (ver <see cref="LoadChildrenInto"/>) — decisão da Sprint 6 pra não arriscar latência
    /// escaneando recursivamente pastas profundas no startup (ver comentário de classe em
    /// <see cref="FolderNodeViewModel"/>).
    /// </summary>
    private void LoadRoot(string path)
    {
        try
        {
            var rootFolder = new MediaFolder
            {
                Name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar)) is { Length: > 0 } name ? name : path,
                Path = path,
            };
            ScanInto(rootFolder);

            Library.Folders.Clear();
            Library.Folders.Add(rootFolder);

            RootFolders.Clear();
            var rootNode = CreateNode(rootFolder);
            RootFolders.Add(rootNode);

            SelectFolder(rootNode);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            Files.Clear();
            StatusMessage = $"Não foi possível abrir a pasta: {ex.Message}";
        }
    }

    /// <summary>
    /// Popula <see cref="MediaFolder.Folders"/>/<see cref="MediaFolder.Items"/> de UMA pasta (1
    /// nível, sem recursão) e marca <see cref="MediaFolder.ChildrenLoaded"/>.
    /// </summary>
    private static void ScanInto(MediaFolder folder)
    {
        try
        {
            folder.Folders.Clear();
            foreach (var subPath in Directory.GetDirectories(folder.Path).OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
            {
                folder.Folders.Add(new MediaFolder { Name = Path.GetFileName(subPath), Path = subPath });
            }

            folder.Items.Clear();
            foreach (var filePath in Directory.GetFiles(folder.Path)
                         .Where(file => VideoExtensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase))
                         .OrderBy(file => file, StringComparer.OrdinalIgnoreCase))
            {
                folder.Items.Add(new MediaItem { Name = Path.GetFileName(filePath), Path = filePath, MediaType = MediaType.Video });
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            // Pasta sem permissao de leitura, ou removida entre a listagem e aqui —
            // deixa a pasta sem filhos em vez de derrubar o scan inteiro.
        }

        folder.ChildrenLoaded = true;
    }

    /// <summary>Existe pelo menos uma subpasta? Checagem barata (para no primeiro achado) — nunca lista o conteúdo inteiro.</summary>
    private static bool HasAnySubfolder(string path)
    {
        try
        {
            return Directory.EnumerateDirectories(path).Any();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    /// <summary>
    /// Cria o nó de um MediaFolder. Se ele já foi escaneado (<see cref="MediaFolder.ChildrenLoaded"/>),
    /// cria um nó filho por subpasta conhecida (cada um, por sua vez, ainda não escaneado). Senão,
    /// só verifica (checagem barata) se vale a pena mostrar a seta de expandir — nunca escaneia
    /// mais de 1 nível de cada vez.
    /// </summary>
    private FolderNodeViewModel CreateNode(MediaFolder folder)
    {
        var node = new FolderNodeViewModel(folder, SelectFolder, LoadChildrenInto);

        if (folder.ChildrenLoaded)
        {
            foreach (var childFolder in folder.Folders)
            {
                node.Children.Add(CreateNode(childFolder));
            }
        }
        else if (HasAnySubfolder(folder.Path))
        {
            node.Children.Add(FolderNodeViewModel.CreatePlaceholder());
        }

        return node;
    }

    /// <summary>Chamado por FolderNodeViewModel na primeira expansão de um nó: escaneia esse nível e substitui o placeholder pelos filhos de verdade.</summary>
    private void LoadChildrenInto(FolderNodeViewModel node)
    {
        ScanInto(node.Folder);

        node.Children.Clear();
        foreach (var childFolder in node.Folder.Folders)
        {
            node.Children.Add(CreateNode(childFolder));
        }
    }

    private void SelectFolder(FolderNodeViewModel node)
    {
        SelectedFolder = node;
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        Files.Clear();

        if (SelectedFolder is null)
        {
            StatusMessage = "Nenhuma pasta selecionada.";
            return;
        }

        var items = SelectedFolder.Folder.Items.AsEnumerable();

        if (!string.IsNullOrWhiteSpace(SearchText))
        {
            items = items.Where(item => item.Name.Contains(SearchText, StringComparison.OrdinalIgnoreCase));
        }

        foreach (var item in items)
        {
            Files.Add(new FileSystemItemViewModel(item.Path));
        }

        StatusMessage = Files.Count == 0
            ? "Nenhum arquivo de vídeo encontrado."
            : $"{Files.Count} arquivo(s) encontrado(s).";
    }
}
