using System.IO;
using CartucheiraVideo.Configuration;
using CartucheiraVideo.Presentation.Mvvm;

namespace CartucheiraVideo.Presentation.ViewModels;

/// <summary>
/// Sprint Arte-de-Fundo (2026-11-10): uma linha editável da lista "Artes configuradas" em
/// Configurações → Arte de Fundo — mesmo padrão de <see cref="LivewireOutputItemViewModel"/>
/// (Id imutável, Name editável na hora via TextBox da lista). <see cref="FilePath"/> é fixado na
/// criação (escolhido pelo diálogo de arquivo do <see cref="BackgroundArtSettingsViewModel.AddCommand"/>)
/// — trocar o arquivo de uma arte já criada não é suportado nesta sprint; o operador remove e
/// cria de novo se precisar apontar pra outro arquivo.
/// </summary>
public sealed class BackgroundArtItemViewModel : ViewModelBase
{
    private string _name;

    public BackgroundArtItemViewModel(BackgroundArtPreset preset)
    {
        ArgumentNullException.ThrowIfNull(preset);
        Id = preset.Id;
        _name = preset.Name;
        FilePath = preset.FilePath;
    }

    public string Id { get; }

    public string Name
    {
        get => _name;
        set => SetProperty(ref _name, value);
    }

    public string FilePath { get; }

    /// <summary>Só o nome do arquivo (sem a pasta inteira) — exibido ao lado do nome editável, pra o operador lembrar qual imagem está por trás de cada preset sem precisar passar o mouse em cima.</summary>
    public string FileName => Path.GetFileName(FilePath);
}
