using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace CartucheiraVideo.Presentation.Converters;

/// <summary>
/// Converte uma cor em hexadecimal (ex.: "#FF3B82F6") para um SolidColorBrush.
/// Conversao somente de ida — nao ha cenario de binding bidirecional para cor de botao.
/// </summary>
public sealed class HexColorToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is string hex && !string.IsNullOrWhiteSpace(hex))
        {
            try
            {
                var color = (Color)ColorConverter.ConvertFromString(hex)!;
                return new SolidColorBrush(color);
            }
            catch (FormatException)
            {
                return Brushes.Gray;
            }
        }

        return Brushes.Gray;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException($"{nameof(HexColorToBrushConverter)} e somente para leitura (Color -> Brush).");
    }
}
