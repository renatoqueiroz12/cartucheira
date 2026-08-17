using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace CartucheiraVideo.Presentation.Converters;

/// <summary>
/// Igual ao <see cref="BooleanToVisibilityConverter"/> nativo do WPF, so que invertido: true vira
/// Collapsed, false vira Visible. Usado pra textos "vazio" (ex.: "Nenhum favorito ainda") que só
/// devem aparecer quando a coleção correspondente NÃO tem itens (mockup v8, rail do Explorer).
/// </summary>
public sealed class InverseBooleanToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is Visibility.Visible ? false : true;
}
