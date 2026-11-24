using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace CartucheiraVideo.Presentation.Converters;

/// <summary>
/// Correção pós-entrega #7, Sprint Reformulação-Visual-Neon (2026-11-24) — pedido do usuário
/// ("ainda precisa ter mais destaque nas cores"). Igual a <see cref="HexColorToBrushConverter"/>,
/// mas devolve um <see cref="Color"/> puro em vez de um <see cref="SolidColorBrush"/> — necessário
/// porque <c>DropShadowEffect.Color</c> é tipado como <c>Color</c>, não aceita <c>Brush</c>. Usado
/// pra fazer o brilho (glow) de cada pad usar a MESMA cor do próprio cartucho (em vez de um brilho
/// branco/azul genérico), deixando a cor de cada pad mais "acesa" — reforça o pedido de "mais
/// destaque nas cores" sem precisar deixar o preenchimento do pad mais saturado ainda (já estava
/// correto desde a correção #5).
/// </summary>
public sealed class HexColorToColorConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is string hex && !string.IsNullOrWhiteSpace(hex))
        {
            try
            {
                return (Color)ColorConverter.ConvertFromString(hex)!;
            }
            catch (FormatException)
            {
                // cai pro default abaixo
            }
        }

        return Colors.Gray;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException($"{nameof(HexColorToColorConverter)} e somente para leitura (hex -> Color).");
}
