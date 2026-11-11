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
