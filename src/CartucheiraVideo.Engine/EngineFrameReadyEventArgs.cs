namespace CartucheiraVideo.Engine;

/// <summary>
/// Um quadro de video bruto pronto pra ser consumido (Sprint NDI, 2026-08-09) — disparado pelo
/// callback de display do player headless de captura de quadros do <see cref="PlaybackEngine"/>.
///
/// <see cref="Data"/> aponta pra um buffer FIXO e REUSADO a cada quadro — quem ouvir este evento
/// precisa terminar de ler/copiar o conteudo ANTES de retornar do handler (ex.: repassar direto pra
/// uma chamada NDI que copia o buffer internamente, como NDIlib_send_send_video_v2). Guardar este
/// ponteiro para uso posterior e um bug — o conteudo pode ja ter sido sobrescrito pelo proximo
/// quadro.
/// </summary>
public sealed class EngineFrameReadyEventArgs : EventArgs
{
    public EngineFrameReadyEventArgs(IntPtr data, int width, int height, int strideBytes)
    {
        Data = data;
        Width = width;
        Height = height;
        StrideBytes = strideBytes;
    }

    /// <summary>Ponteiro pro buffer BGRA — valido so durante o handler deste evento (ver comentario da classe).</summary>
    public IntPtr Data { get; }

    public int Width { get; }

    public int Height { get; }

    /// <summary>Bytes por linha — pra BGRA, normalmente Width * 4.</summary>
    public int StrideBytes { get; }
}
