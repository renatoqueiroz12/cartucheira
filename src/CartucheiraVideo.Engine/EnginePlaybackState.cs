namespace CartucheiraVideo.Engine;

/// <summary>
/// Estado bruto do <see cref="PlaybackEngine"/> — vocabulario de motor, nao de playout. Quem
/// traduz isso para <c>Core.DeckPlaybackState</c> (Cue/Ready/Seeking/Buffering/Finished/Offline
/// etc.) e a Presentation (<c>DeckViewModel</c>): o Engine so sabe o que o LibVLC de fato reporta.
/// </summary>
public enum EnginePlaybackState
{
    /// <summary>Nenhuma midia carregada.</summary>
    Idle,

    /// <summary>Media atribuida ao player, aguardando o primeiro Play.</summary>
    Loaded,

    Playing,

    Paused,

    /// <summary>Parado explicitamente (Stop) — posicao volta a zero no proximo Play.</summary>
    Stopped,

    /// <summary>Chegou ao fim do arquivo (sem loop ativo).</summary>
    Ended,

    Error
}
