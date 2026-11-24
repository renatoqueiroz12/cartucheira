using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace CartucheiraVideo.Presentation.Converters;

/// <summary>
/// Correção pós-entrega #6, Sprint Reformulação-Visual-Neon (2026-11-24) — pedido explícito do
/// usuário depois de ver o app rodando de verdade: "nos botoes dos cartuchos de VH e TR a fonte
/// branca ta sumindo dos botoes, tentar fonte preta negrito".
///
/// A correção #5 trocou o texto do pad de preto fixo pra branco fixo (`Brush.TextHi`), o que bate
/// bem com a imagem de referência — mas só porque lá os pads são azul/vermelho SATURADOS o
/// suficiente pra branco ler bem. Na prática, cada cartucho pode ter uma cor PRÓPRIA escolhida
/// pelo operador (dashboard ou "Cor..." no menu de contexto — feature da v1.0.113, mantida de
/// propósito desde a correção #4) — incluindo cores claras (amarelo, verde-claro) onde texto branco
/// quase desaparece, exatamente o que o usuário reportou no print real (cartuchos de teste com cor
/// amarela/verde puxando pro claro).
///
/// Em vez de voltar pro preto fixo (que reintroduziria o problema original nos pads azul/vermelho
/// saturados), este conversor calcula o CONTRASTE certo pra cada cor, cartucho a cartucho: luminância
/// percebida (fórmula padrão de luma, mesma usada em conversão pra escala de cinza) acima do limiar
/// vira texto escuro/negrito: abaixo vira texto quase branco. Único lugar do projeto que faz essa
/// conta — os outros brushes de texto (Brush.TextHi etc.) continuam fixos, porque eles vivem sobre
/// fundos que o próprio Theme.xaml controla, não sobre uma cor arbitrária escolhida pelo operador.
/// </summary>
public sealed class HexColorToContrastBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush DarkText = new(Color.FromRgb(0x14, 0x16, 0x1A));
    private static readonly SolidColorBrush LightText = new(Color.FromRgb(0xF2, 0xF2, 0xF2));

    static HexColorToContrastBrushConverter()
    {
        DarkText.Freeze();
        LightText.Freeze();
    }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is string hex && !string.IsNullOrWhiteSpace(hex))
        {
            try
            {
                var color = (Color)ColorConverter.ConvertFromString(hex)!;
                var luma = (0.299 * color.R) + (0.587 * color.G) + (0.114 * color.B);
                return luma > 145 ? DarkText : LightText;
            }
            catch (FormatException)
            {
                // cai pro default abaixo
            }
        }

        return LightText;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException($"{nameof(HexColorToContrastBrushConverter)} e somente para leitura (Color -> Brush).");
}
