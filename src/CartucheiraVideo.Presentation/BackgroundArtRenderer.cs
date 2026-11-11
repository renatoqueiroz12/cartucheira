using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace CartucheiraVideo.Presentation;

/// <summary>
/// Sprint Arte-de-Fundo (2026-11-10): decodifica um arquivo de imagem (PNG/JPG/BMP) pro mesmo
/// formato de quadro que a saída NDI de um Deck já espera — UYVY 4:2:2, canvas fixo 1920x1080,
/// mesma regra de arredondamento (innerWidth múltiplo de 4, innerHeight múltiplo de 2) que
/// <c>Engine.PlaybackEngine.ComputeLetterboxSize</c> já usa pro vídeo normal — ver comentário
/// completo lá pro porquê desse arredondamento (um grupo UYVY de 2 pixels/4 bytes nunca pode ser
/// cortado ao meio). Deliberadamente reimplementado aqui, não compartilhado com o Engine — mesmo
/// princípio de isolamento entre projetos-irmãos já seguido no resto do projeto (Ndi e Engine
/// também não compartilham esse cálculo, cada um fala seu próprio vocabulário); é pouca conta
/// matemática, não vale criar uma dependência cruzada só por isto.
///
/// Conversão RGB→YUV usa a fórmula clássica BT.601, faixa limitada (mesma convenção de vídeo
/// "broadcast" clássico) — "boa o suficiente" pra uma arte estática/logo; não há tentativa de
/// casar exatamente a colorimetria que o LibVLC produz pro vídeo real (BT.601 vs. BT.709 etc.),
/// diferença imperceptível a olho nu neste caso de uso. Redimensionamento usa
/// <see cref="TransformedBitmap"/> (amostragem padrão do WPF) — não é a melhor qualidade possível
/// pra reduções grandes, mas suficiente pra uma arte de fundo exibida em tela cheia.
///
/// Falha honesta em qualquer problema (arquivo não existe, formato não suportado, decodificação
/// falha): retorna null, nunca lança — quem chama (<c>DeckViewModel</c>) trata null como "sem arte
/// configurada", caindo no mesmo comportamento de hoje (só para de mandar quadro quando parado).
/// </summary>
public static class BackgroundArtRenderer
{
    public const int FrameWidth = 1920;

    public const int FrameHeight = 1080;

    /// <summary>UYVY empacota 2 pixels em 4 bytes (2 bytes/pixel em média) — mesmo layout que <see cref="Ndi.NdiSender.SendFrame"/> já espera.</summary>
    public const int StrideBytes = FrameWidth * 2;

    /// <summary>Tamanho total de um quadro completo 1920x1080 UYVY — os dois buffers "ping-pong" do <c>DeckViewModel</c> usam este tamanho.</summary>
    public const int FrameSizeBytes = FrameHeight * StrideBytes;

    /// <summary>Decodifica e letterboxa a imagem em <paramref name="imagePath"/> — null em qualquer falha (ver comentário de classe) ou se o caminho for nulo/vazio.</summary>
    public static byte[]? RenderToUyvy(string? imagePath)
    {
        if (string.IsNullOrWhiteSpace(imagePath))
        {
            return null;
        }

        try
        {
            if (!File.Exists(imagePath))
            {
                return null;
            }

            BitmapFrame frame;
            using (var stream = File.OpenRead(imagePath))
            {
                // BitmapCacheOption.OnLoad forca a decodificacao completa pra memoria agora, antes
                // do stream fechar no fim deste using — sem isso, o acesso aos pixels abaixo
                // poderia tentar ler de um stream ja fechado (carregamento tardio/lazy e o padrao
                // normal do WPF quando o cache option e OnDemand).
                var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);

                if (decoder.Frames.Count == 0)
                {
                    return null;
                }

                frame = decoder.Frames[0];
            }

            var converted = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);

            var nativeWidth = converted.PixelWidth;
            var nativeHeight = converted.PixelHeight;

            if (nativeWidth <= 0 || nativeHeight <= 0)
            {
                return null;
            }

            var (innerWidth, innerHeight) = ComputeLetterboxSize(nativeWidth, nativeHeight);

            var scaled = new TransformedBitmap(
                converted,
                new ScaleTransform((double)innerWidth / nativeWidth, (double)innerHeight / nativeHeight));

            var rgbaStride = innerWidth * 4;
            var rgba = new byte[innerHeight * rgbaStride];
            scaled.CopyPixels(rgba, rgbaStride, 0);

            var buffer = new byte[FrameSizeBytes];
            FillBlack(buffer);

            var offsetXBytes = (FrameWidth - innerWidth) / 2 * 2; // 2 bytes/pixel em UYVY
            var offsetYBytes = (FrameHeight - innerHeight) / 2 * StrideBytes;

            for (var y = 0; y < innerHeight; y++)
            {
                var srcRowOffset = y * rgbaStride;
                var dstRowOffset = offsetYBytes + (y * StrideBytes) + offsetXBytes;

                for (var x = 0; x < innerWidth; x += 2)
                {
                    var i0 = srcRowOffset + (x * 4);
                    var secondX = Math.Min(x + 1, innerWidth - 1);
                    var i1 = srcRowOffset + (secondX * 4);

                    // FormatConvertedBitmap em Bgra32 guarda B,G,R,A nesta ordem em memoria.
                    var (y0, u0, v0) = RgbToYuv(rgba[i0 + 2], rgba[i0 + 1], rgba[i0 + 0]);
                    var (y1, u1, v1) = RgbToYuv(rgba[i1 + 2], rgba[i1 + 1], rgba[i1 + 0]);

                    var u = (byte)((u0 + u1) / 2);
                    var v = (byte)((v0 + v1) / 2);

                    var o = dstRowOffset + (x * 2);
                    buffer[o + 0] = u;
                    buffer[o + 1] = y0;
                    buffer[o + 2] = v;
                    buffer[o + 3] = y1;
                }
            }

            return buffer;
        }
        catch (Exception)
        {
            // Falha honesta — qualquer problema de decodificacao vira "sem arte configurada" pro
            // chamador, nunca derruba o Deck.
            return null;
        }
    }

    /// <summary>Mesmo cálculo de "fit"/letterbox de <c>Engine.PlaybackEngine.ComputeLetterboxSize</c> (ver comentário de classe pro porquê do arredondamento múltiplo de 4/2).</summary>
    private static (int Width, int Height) ComputeLetterboxSize(int nativeWidth, int nativeHeight)
    {
        var sourceAspect = (double)nativeWidth / nativeHeight;
        var canvasAspect = (double)FrameWidth / FrameHeight;

        int innerWidth, innerHeight;
        if (sourceAspect > canvasAspect)
        {
            innerWidth = FrameWidth;
            innerHeight = (int)Math.Round(FrameWidth / sourceAspect);
        }
        else
        {
            innerHeight = FrameHeight;
            innerWidth = (int)Math.Round(FrameHeight * sourceAspect);
        }

        innerWidth -= innerWidth % 4;
        innerHeight -= innerHeight % 2;

        if (innerWidth <= 0)
        {
            innerWidth = 4;
        }

        if (innerHeight <= 0)
        {
            innerHeight = 2;
        }

        return (innerWidth, innerHeight);
    }

    private static void FillBlack(byte[] buffer)
    {
        // Preto em UYVY, faixa limitada (mesma convencao de video classico): Y=16, U=V=128.
        for (var i = 0; i < buffer.Length; i += 4)
        {
            buffer[i + 0] = 128; // U
            buffer[i + 1] = 16;  // Y0
            buffer[i + 2] = 128; // V
            buffer[i + 3] = 16;  // Y1
        }
    }

    /// <summary>BT.601, faixa limitada (16-235 luma, 16-240 croma) — mesma convenção "vídeo" clássica.</summary>
    private static (byte Y, byte U, byte V) RgbToYuv(byte r, byte g, byte b)
    {
        var y = 16.0 + ((0.257 * r) + (0.504 * g) + (0.098 * b));
        var u = 128.0 + ((-0.148 * r) - (0.291 * g) + (0.439 * b));
        var v = 128.0 + ((0.439 * r) - (0.368 * g) - (0.071 * b));

        return ((byte)Clamp(y), (byte)Clamp(u), (byte)Clamp(v));
    }

    private static double Clamp(double value) => Math.Clamp(value, 0, 255);
}
