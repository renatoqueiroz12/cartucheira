namespace CartucheiraVideo.Engine;

/// <summary>
/// Um bloco de amostras de audio bruto pronto pra ser consumido (Sprint NDI-audio, 2026-08-11) —
/// disparado por <see cref="NdiAudioCapture.AudioFrameReady"/>.
///
/// Sprint NDI-audio-NAudio (2026-08-11, mesmo dia): <see cref="Data"/> passou de <c>IntPtr</c> (um
/// ponteiro pro buffer do proprio LibVLC, valido so durante o handler) pra <c>float[]</c> —
/// deliberado, migracao junto com a troca do mecanismo de captura (LibVLC callback → NAudio, ver
/// <see cref="NdiAudioCapture"/> pro historico completo/motivo). Como <see cref="NdiAudioCapture"/> ja
/// aloca um array GERENCIADO novo por bloco (nunca reusa um buffer entre chamadas), nao ha mais
/// nenhum ponteiro nativo neste caminho pra se preocupar com lifetime — <see cref="Data"/> e seguro
/// de guardar/usar mesmo DEPOIS deste handler retornar (embora nenhum consumidor atual precise disso;
/// <c>NdiSender.SendAudioFrame</c> continua consumindo dentro do proprio handler, so que agora sem
/// nenhum <c>Marshal.Copy</c> — o array em si ja e o dado).
/// </summary>
public sealed class EngineAudioFrameReadyEventArgs : EventArgs
{
    public EngineAudioFrameReadyEventArgs(float[] data, int sampleRate, int channels, int sampleCount)
    {
        Data = data;
        SampleRate = sampleRate;
        Channels = channels;
        SampleCount = sampleCount;
    }

    /// <summary>Array gerenciado de amostras PCM float32 intercalado, proprio deste bloco (ver comentario da classe).</summary>
    public float[] Data { get; }

    public int SampleRate { get; }

    public int Channels { get; }

    /// <summary>Numero de amostras POR CANAL neste bloco (nao o total) — mesma convencao de antes.</summary>
    public int SampleCount { get; }
}
