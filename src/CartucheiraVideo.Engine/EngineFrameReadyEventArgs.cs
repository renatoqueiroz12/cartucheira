namespace CartucheiraVideo.Engine;

/// <summary>
/// Um quadro de video bruto pronto pra ser consumido (Sprint NDI, 2026-08-09) — disparado pelo
/// callback de display do player headless de captura de quadros do <see cref="PlaybackEngine"/>.
///
/// Sprint NDI-video-async (2026-09-11): <see cref="Data"/> deixou de apontar sempre pro MESMO
/// buffer fixo — o <see cref="PlaybackEngine"/> agora alterna entre DOIS buffers a cada quadro
/// ("ping-pong", ver comentario completo em <c>PlaybackEngine._frameBuffers</c>), porque
/// <c>NdiSender.SendFrame</c> passou a usar a variante ASSINCRONA da NDI SDK — o buffer pode ainda
/// estar em transito de rede quando este evento dispara pro PROXIMO quadro. Ainda assim, quem ouve
/// este evento deve terminar de ler/repassar o conteudo ANTES de retornar do handler, sem guardar o
/// ponteiro pra uso posterior: a seguranca contra sobrescrita nao vem mais de uma copia sincrona
/// dentro da chamada NDI, e sim da alternancia de buffers do Engine — guardar o ponteiro alem do
/// escopo deste handler quebra essa garantia (o buffer sera reescrito assim que ele voltar a ser o
/// ativo, dois quadros depois).
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

    /// <summary>Ponteiro pro buffer (UYVY desde a Sprint NDI-video-uyvy, 2026-09-12 — YCbCr 4:2:2 empacotado, 2 bytes/pixel EM MEDIA, grupo de 4 bytes cobre 2 pixels; BGRX/BGRA antes disso, 4 bytes por pixel) — valido so durante o handler deste evento (ver comentario da classe); um de dois buffers alternados, nao mais um buffer fixo unico.</summary>
    public IntPtr Data { get; }

    public int Width { get; }

    public int Height { get; }

    /// <summary>Bytes por linha — normalmente Width * 2 desde a Sprint NDI-video-uyvy (2 bytes/pixel em media, UYVY); era Width * 4 (BGRX/BGRA) antes disso.</summary>
    public int StrideBytes { get; }
}
