namespace CartucheiraVideo.Core;

/// <summary>
/// Representa uma reproducao em andamento (ou pausada) de uma midia — tipicamente associada a um
/// <see cref="Deck"/>. Separado de <see cref="Deck"/> porque o Deck e o "aparelho" (identidade
/// fixa, sempre existe) enquanto a PlaybackSession e o que esta tocando nele agora (muda a cada
/// Load). Puramente dado — quem atualiza CurrentPosition/State de verdade e o Engine, numa sprint
/// futura.
/// </summary>
public sealed class PlaybackSession
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public required MediaItem Media { get; set; }

    public DateTime? StartTime { get; set; }

    public TimeSpan CurrentPosition { get; set; }

    public TimeSpan? Duration { get; set; }

    public DeckPlaybackState State { get; set; } = DeckPlaybackState.Cue;
}
