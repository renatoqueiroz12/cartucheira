namespace CartucheiraVideo.Engine;

public sealed class EnginePositionChangedEventArgs : EventArgs
{
    public EnginePositionChangedEventArgs(TimeSpan position, TimeSpan? duration)
    {
        Position = position;
        Duration = duration;
    }

    public TimeSpan Position { get; }

    /// <summary>Nulo enquanto o LibVLC ainda nao conhece a duracao total (comum nos primeiros milissegundos apos o Play).</summary>
    public TimeSpan? Duration { get; }
}
