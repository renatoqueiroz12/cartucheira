using System.Collections.ObjectModel;
using System.Windows.Input;
using CartucheiraVideo.Presentation.Mvvm;

namespace CartucheiraVideo.Presentation.ViewModels;

/// <summary>
/// ViewModel da aba Playlist do Explorer (mockup v8, nota #11) — substitui a antiga Fila Rápida
/// (Core.MediaQueue), que saiu da coluna dos Decks e virou esta aba, ao lado de Explorer, no
/// mesmo painel de mídia.
///
/// Carregar uma playlist de verdade (arquivo gerado por um programa externo, formato ainda não
/// definido) e o avanço automático real de item (ligado ao Modo Automático + Auto/Manual de cada
/// Deck) ainda não existem — isso entra numa sprint futura de Integração (roteiro item 5, "Playlist
/// real por pasta"). Os itens abaixo continuam sendo dados de exemplo fictícios só pra a lista não
/// nascer vazia e mostrar a densidade real da tela (mesma decisão, e mesmo precedente, da Sprint 8
/// pros bancos de exemplo da Botoneira — que deixaram de ser fictícios na Sprint Cartucheira,
/// 2026-08-09, quando ganharam população real por pasta; a Playlist ainda não passou por essa
/// mesma migração): sem caminho de arquivo real associado
/// (<see cref="PlaylistItemViewModel.HasRealFile"/> = false), então ativar um deles avisa
/// honestamente em vez de tentar carregar um arquivo que não existe.
/// </summary>
public sealed class PlaylistViewModel : ViewModelBase
{
    private readonly Action<string> _onStatus;
    private bool _autoModeEnabled = true;

    public PlaylistViewModel(Action<string> onStatus)
    {
        _onStatus = onStatus ?? throw new ArgumentNullException(nameof(onStatus));

        foreach (var item in CreateSampleItems())
        {
            Items.Add(item);
        }

        LoadPlaylistCommand = new RelayCommand(_ => _onStatus("Carregar Playlist ainda não implementado (formato do arquivo de playlist externo entra numa sprint futura)."));
        ToggleAutoModeCommand = new RelayCommand(_ => ToggleAutoMode());
    }

    /// <summary>Disparado quando o operador ativa (duplo clique) um item com arquivo real associado — mesmo papel do FileExplorerViewModel.FileActivated.</summary>
    public event Action<PlaylistItemViewModel>? ItemActivated;

    public ObservableCollection<PlaylistItemViewModel> Items { get; } = new();

    public string Name => "Playlist_Jornal18h_v3";

    /// <summary>
    /// "Modo Automático" geral da Playlist (mockup v8, nota #12) — o "disjuntor geral": quando
    /// ligado, o item seguinte fica livre pra carregar sozinho, mas só nos Decks que também
    /// estiverem em modo Auto (o "disjuntor de cada tomada", ver DeckViewModel.IsAutoMode). Ainda
    /// não avança nada de verdade — ver comentário de classe.
    /// </summary>
    public bool AutoModeEnabled
    {
        get => _autoModeEnabled;
        private set => SetProperty(ref _autoModeEnabled, value);
    }

    public ICommand LoadPlaylistCommand { get; }

    public ICommand ToggleAutoModeCommand { get; }

    /// <summary>Chamado pela View (duplo clique numa linha, code-behind) — mesmo papel de FileExplorerViewModel.ActivateFile.</summary>
    public void ActivateItem(PlaylistItemViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        if (!item.HasRealFile)
        {
            _onStatus($"\"{item.Name}\" é um item de exemplo da Playlist (sem arquivo real associado ainda) — carregamento por Playlist real entra numa sprint futura.");
            return;
        }

        ItemActivated?.Invoke(item);
    }

    private void ToggleAutoMode()
    {
        AutoModeEnabled = !AutoModeEnabled;
        _onStatus(AutoModeEnabled
            ? "Modo Automático da Playlist ligado — ainda não avança itens sozinho (Playlist real entra numa sprint futura)."
            : "Modo Automático da Playlist desligado.");
    }

    private static IEnumerable<PlaylistItemViewModel> CreateSampleItems()
    {
        yield return new PlaylistItemViewModel(1, "abertura_jornal_18h.mp4", TimeSpan.FromSeconds(28), string.Empty, isCurrent: true, isNext: false);
        yield return new PlaylistItemViewModel(2, "chamada_comercial_bloco2.mp4", TimeSpan.FromSeconds(15), string.Empty, isCurrent: false, isNext: true);
        yield return new PlaylistItemViewModel(3, "entrevista_convidado_final.mp4", new TimeSpan(0, 3, 42), string.Empty, isCurrent: false, isNext: false);
        yield return new PlaylistItemViewModel(4, "nota_pe_clima.mp4", new TimeSpan(0, 1, 12), string.Empty, isCurrent: false, isNext: false);
        yield return new PlaylistItemViewModel(5, "vinheta_esportes.mp4", TimeSpan.FromSeconds(5), string.Empty, isCurrent: false, isNext: false);
        yield return new PlaylistItemViewModel(6, "encerramento_bloco1.mp4", TimeSpan.FromSeconds(8), string.Empty, isCurrent: false, isNext: false);
        yield return new PlaylistItemViewModel(7, "bg_grafico_cotacoes.mp4", TimeSpan.FromSeconds(20), string.Empty, isCurrent: false, isNext: false);
        yield return new PlaylistItemViewModel(8, "sweeper_azul.mp4", TimeSpan.FromSeconds(3), string.Empty, isCurrent: false, isNext: false);
    }
}
