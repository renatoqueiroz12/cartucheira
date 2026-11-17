using CartucheiraVideo.Configuration;
using CartucheiraVideo.Presentation.Mvvm;

namespace CartucheiraVideo.Presentation.ViewModels;

/// <summary>
/// Sprint GC (2026-11-17): uma linha editável da lista "Campos do GC" em Configurações → GC — mesmo
/// padrão de <see cref="BackgroundArtItemViewModel"/> (edição em memória, só grava quando o Salvar
/// global do <c>SettingsWindow</c> for clicado, ver <see cref="GcSettingsViewModel.ApplyTo"/>), mas
/// sem <c>Id</c> nenhum (<see cref="GcFieldLayout"/> não tem um — a lista inteira é regravada do
/// zero a cada Salvar, não precisa de identidade estável entre edições).
///
/// X/Y/Width/Height/FontSizePt definem a posição aproximada deste campo no "mockup configurável" do
/// monitoramento remoto (ver comentário completo de <see cref="GcFieldLayout"/>) — um retângulo de
/// referência de 1920x1080, a mesma lógica de referência já usada pela fonte do espelho do
/// Teleprompter (<c>app.js</c> <c>TP_MIRROR_REFERENCE_WIDTH_PX</c>).
/// </summary>
public sealed class GcFieldLayoutItemViewModel : ViewModelBase
{
    private string _name;
    private double _x;
    private double _y;
    private double _width;
    private double _height;
    private double _fontSizePt;

    public GcFieldLayoutItemViewModel(GcFieldLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        _name = layout.Name;
        _x = layout.X;
        _y = layout.Y;
        _width = layout.Width;
        _height = layout.Height;
        _fontSizePt = layout.FontSizePt;
    }

    /// <summary>Precisa casar com um <c>SelectedName</c> real do Título configurado no vMix — mesmo nome usado nos campos de GC de cada Lauda/GC padrão do Programa.</summary>
    public string Name
    {
        get => _name;
        set => SetProperty(ref _name, value);
    }

    public double X
    {
        get => _x;
        set => SetProperty(ref _x, value);
    }

    public double Y
    {
        get => _y;
        set => SetProperty(ref _y, value);
    }

    public double Width
    {
        get => _width;
        set => SetProperty(ref _width, value);
    }

    public double Height
    {
        get => _height;
        set => SetProperty(ref _height, value);
    }

    public double FontSizePt
    {
        get => _fontSizePt;
        set => SetProperty(ref _fontSizePt, value);
    }
}
