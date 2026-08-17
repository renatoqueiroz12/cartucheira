using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace CartucheiraVideo.Presentation.Converters;

/// <summary>
/// String vazia/nula vira Collapsed, qualquer outra vira Visible — Sprint Config Geral, parte 1
/// (2026-08-09), usado pelo <c>AdminAuthWindow</c> pra só mostrar a linha de erro (ErrorMessage)
/// quando ela tiver algo pra mostrar, sem precisar de uma propriedade booleana auxiliar separada
/// na ViewModel só pra isso.
/// </summary>
public sealed class StringToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        string.IsNullOrEmpty(value as string) ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException("StringToVisibilityConverter é usado só pra leitura (ErrorMessage → Visibility), nunca no sentido inverso.");
}
