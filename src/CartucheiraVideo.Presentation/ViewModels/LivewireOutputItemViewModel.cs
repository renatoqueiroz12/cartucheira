using CartucheiraVideo.Configuration;
using CartucheiraVideo.Presentation.Mvvm;

namespace CartucheiraVideo.Presentation.ViewModels;

/// <summary>
/// Sprint Livewire-2 (2026-09-13): uma "Saída Livewire" sendo editada na tela de Configurações —
/// envolve um <see cref="LivewireOutputSettings"/> em campos observáveis (<see cref="Name"/>/
/// <see cref="Channel"/>) pro editor (<see cref="LivewireOutputsSettingsViewModel"/>) poder usar
/// <c>TextBox</c>/<c>SetProperty</c> normalmente, mesmo padrão de "editar em memória, só grava no
/// Salvar global" já usado pelo resto da tela de Configurações.
///
/// <see cref="Id"/> é o único campo que NUNCA muda depois de criado — é ele que vira a parte
/// <c>{Id}</c> do valor prefixado (<c>"livewire:{Id}"</c>) gravado em campos como
/// <c>CartridgeSettingsSnapshot.VinhetasAudioDeviceId</c> quando esta Saída é selecionada num
/// seletor de dispositivo de áudio — trocar o Id no meio do caminho quebraria essa referência.
/// </summary>
public sealed class LivewireOutputItemViewModel : ViewModelBase
{
    private string _name;
    private int _channel;

    public LivewireOutputItemViewModel(LivewireOutputSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        Id = settings.Id;
        _name = settings.Name;
        _channel = settings.Channel;
    }

    /// <summary>Identificador estável — ver comentário de classe. Nunca exposto pra edição na UI.</summary>
    public string Id { get; }

    /// <summary>Nome exibido no seletor de dispositivo de áudio (Vinhetas/Trilhas) e nesta própria lista.</summary>
    public string Name
    {
        get => _name;
        set => SetProperty(ref _name, value);
    }

    /// <summary>Canal Livewire (1..32767) — determina o endereço multicast de transmissão. Fora desta faixa é preso (clamp), nunca aceito calado com um valor fora do intervalo válido do protocolo.</summary>
    public int Channel
    {
        get => _channel;
        set => SetProperty(ref _channel, Math.Clamp(value, 1, 32767));
    }
}
