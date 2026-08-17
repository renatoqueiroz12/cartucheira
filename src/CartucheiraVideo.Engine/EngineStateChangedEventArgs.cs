namespace CartucheiraVideo.Engine;

public sealed class EngineStateChangedEventArgs : EventArgs
{
    public EngineStateChangedEventArgs(EnginePlaybackState state)
    {
        State = state;
    }

    public EnginePlaybackState State { get; }
}
