namespace CartucheiraVideo.Presentation;

/// <summary>
/// Sprint Telas-Saida-Direta (2026-11-10): converte um quadro UYVY 4:2:2 (o MESMO formato que já
/// circula por todo o pipeline deste Deck — entrada de <c>Ndi.NdiSender.SendFrame</c>, saída de
/// <see cref="BackgroundArtRenderer.RenderToUyvy"/>) pra BGRA32, formato que o
/// <c>System.Windows.Media.Imaging.WriteableBitmap</c> das janelas de telão (<c>Views.ScreenOutputWindow</c>)
/// precisa pra exibir o vídeo direto numa tela do Windows. É a ÚNICA conversão UYVY→BGRA32 desta
/// base — fica aqui (Presentation) porque só a Presentation precisa falar a língua do WPF; o resto
/// do pipeline (Engine/Ndi) continua falando só UYVY entre si, mesmo princípio de "cada camada fala
/// seu próprio vocabulário" já seguido no resto do projeto.
///
/// Fórmula inversa da já usada por <see cref="BackgroundArtRenderer"/> — BT.601, faixa limitada
/// (16-235 luma, 16-240 croma). Escreve direto num buffer BGRA32 fornecido por quem chama (nunca
/// aloca um buffer novo por quadro) — pensado pra ser chamado a cada quadro recebido do Engine
/// (callback de alta frequência, mesmo caminho que já alimenta o NDI), então alocação por quadro
/// seria desperdício real.
/// </summary>
public static class UyvyBgraConverter
{
    /// <summary>
    /// Converte <paramref name="uyvy"/> (largura <paramref name="width"/>, altura
    /// <paramref name="height"/>, passo de linha <paramref name="srcStrideBytes"/>) pra
    /// <paramref name="destBgra"/>, que precisa ter pelo menos <c>width * height * 4</c> bytes
    /// (passo de linha fixo <c>width * 4</c>, sem padding — é o que <c>WriteableBitmap.WritePixels</c>
    /// espera quando a região coberta é a largura inteira). Não valida tamanhos por performance (é
    /// chamado por quadro) — quem chama (<c>ViewModels.DeckViewModel</c>) já conhece as dimensões
    /// fixas do canvas (1920x1080, mesmo canvas de sempre) e aloca os buffers uma única vez.
    /// </summary>
    public static void ConvertToBgra32(byte[] uyvy, int width, int height, int srcStrideBytes, byte[] destBgra)
    {
        var destStrideBytes = width * 4;

        for (var row = 0; row < height; row++)
        {
            var srcRowOffset = row * srcStrideBytes;
            var destRowOffset = row * destStrideBytes;

            for (var x = 0; x < width; x += 2)
            {
                var srcOffset = srcRowOffset + (x * 2);
                var u = uyvy[srcOffset + 0];
                var y0 = uyvy[srcOffset + 1];
                var v = uyvy[srcOffset + 2];
                var y1 = uyvy[srcOffset + 3];

                var (r0, g0, b0) = YuvToRgb(y0, u, v);
                WriteBgra(destBgra, destRowOffset + (x * 4), r0, g0, b0);

                if (x + 1 < width)
                {
                    var (r1, g1, b1) = YuvToRgb(y1, u, v);
                    WriteBgra(destBgra, destRowOffset + ((x + 1) * 4), r1, g1, b1);
                }
            }
        }
    }

    /// <summary>
    /// Sprint Teleprompter-NDI (2026-11-17): direção INVERSA de <see cref="ConvertToBgra32"/> — pra
    /// alimentar <c>Ndi.NdiSender.SendFrame</c> (que só fala UYVY) a partir de um quadro RENDERIZADO
    /// pelo WPF (<c>Views.TeleprompterOutputWindow.RenderToUyvy</c>, via
    /// <c>System.Windows.Media.Imaging.RenderTargetBitmap</c>, que só entrega Bgra32/Pbgra32) — é a
    /// primeira vez neste projeto que um conteúdo gerado pela própria UI (não um vídeo
    /// decodificado/uma imagem estática) precisa virar quadro NDI, então esta direção não existia
    /// até agora. Mesma fórmula BT.601 faixa limitada já usada por
    /// <c>BackgroundArtRenderer.RgbToYuv</c> — duplicada aqui de propósito, mesmo princípio de "cada
    /// classe fala seu próprio vocabulário" já seguido entre as outras conversões deste arquivo/
    /// <c>BackgroundArtRenderer</c> (ver comentário de classe).
    ///
    /// <paramref name="width"/> ÍMPAR é arredondado pra baixo (sempre par) antes de converter — um
    /// grupo UYVY de 2 pixels/4 bytes nunca pode ser cortado ao meio (mesma regra de
    /// <c>BackgroundArtRenderer.ComputeLetterboxSize</c>); a última coluna de uma largura ímpar
    /// simplesmente não entra no quadro NDI (perda de 1px, imperceptível, nunca acontece na prática —
    /// resoluções de monitor e o canvas fixo 1920x1080 off-screen são sempre pares).
    /// </summary>
    /// <param name="bgra">Buffer de origem Bgra32 (<paramref name="width"/> * <paramref name="height"/> * 4 bytes, passo de linha fixo width*4, sem padding — o que <c>RenderTargetBitmap.CopyPixels</c> entrega pedindo esse stride).</param>
    /// <param name="width">Largura em pixels (arredondada pra baixo pra um valor par antes de converter).</param>
    /// <param name="height">Altura em pixels.</param>
    /// <returns>Buffer UYVY novo, largura efetiva (par) x <paramref name="height"/>, passo de linha = largura efetiva * 2.</returns>
    public static byte[] ConvertFromBgra32(byte[] bgra, int width, int height)
    {
        var evenWidth = width - (width % 2);
        var srcStrideBytes = width * 4;
        var destStrideBytes = evenWidth * 2;
        var dest = new byte[height * destStrideBytes];

        for (var row = 0; row < height; row++)
        {
            var srcRowOffset = row * srcStrideBytes;
            var destRowOffset = row * destStrideBytes;

            for (var x = 0; x < evenWidth; x += 2)
            {
                var i0 = srcRowOffset + (x * 4);
                var i1 = srcRowOffset + ((x + 1) * 4);

                // Bgra32: B,G,R,A nesta ordem em memória.
                var (y0, u0, v0) = RgbToYuv(bgra[i0 + 2], bgra[i0 + 1], bgra[i0 + 0]);
                var (y1, u1, v1) = RgbToYuv(bgra[i1 + 2], bgra[i1 + 1], bgra[i1 + 0]);

                var u = (byte)((u0 + u1) / 2);
                var v = (byte)((v0 + v1) / 2);

                var o = destRowOffset + (x * 2);
                dest[o + 0] = u;
                dest[o + 1] = y0;
                dest[o + 2] = v;
                dest[o + 3] = y1;
            }
        }

        return dest;
    }

    /// <summary>BT.601, faixa limitada (16-235 luma, 16-240 croma) — mesma convenção de <c>BackgroundArtRenderer.RgbToYuv</c>, duplicada aqui de propósito (ver comentário de <see cref="ConvertFromBgra32"/>).</summary>
    private static (byte Y, byte U, byte V) RgbToYuv(byte r, byte g, byte b)
    {
        var y = 16.0 + ((0.257 * r) + (0.504 * g) + (0.098 * b));
        var u = 128.0 + ((-0.148 * r) - (0.291 * g) + (0.439 * b));
        var v = 128.0 + ((0.439 * r) - (0.368 * g) - (0.071 * b));

        return ((byte)Clamp(y), (byte)Clamp(u), (byte)Clamp(v));
    }

    private static void WriteBgra(byte[] dest, int offset, byte r, byte g, byte b)
    {
        dest[offset + 0] = b;
        dest[offset + 1] = g;
        dest[offset + 2] = r;
        dest[offset + 3] = 255; // A — sempre opaco, este quadro nunca tem transparência.
    }

    /// <summary>Inversa de <c>BackgroundArtRenderer.RgbToYuv</c> — BT.601, faixa limitada.</summary>
    private static (byte R, byte G, byte B) YuvToRgb(byte y, byte u, byte v)
    {
        var yF = y - 16.0;
        var uF = u - 128.0;
        var vF = v - 128.0;

        var r = (1.164 * yF) + (1.596 * vF);
        var g = (1.164 * yF) - (0.813 * vF) - (0.391 * uF);
        var b = (1.164 * yF) + (2.018 * uF);

        return ((byte)Clamp(r), (byte)Clamp(g), (byte)Clamp(b));
    }

    private static double Clamp(double value) => Math.Clamp(value, 0, 255);
}
