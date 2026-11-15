using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace CartucheiraVideo.Presentation.Converters;

/// <summary>
/// Fix v1.0.81 (2026-11-15): a aba Logs (<c>Views.LogsWindow</c>) sempre mostrou todas as linhas na
/// mesma cor neutra — usuário reportou, depois de caçar um erro real de Firewall/urlacl da Playlist
/// no meio de dezenas de linhas de inicialização, que "o log está bem ruim pra visualizar". Este
/// conversor colore cada linha pelo texto dela mesma (sem precisar guardar um nível de severidade
/// separado em <see cref="MainViewModel.StatusHistory"/>, que é só uma lista de strings já formatadas
/// com timestamp — mudar isso pra uma lista de objetos tipados reescreveria um histórico usado em
/// dezenas de lugares só por causa da cor): qualquer linha com "não foi possível"/"erro"/"falha"/
/// "recusou" (case-insensitive) vira vermelha (mesmo problema real que o usuário estava procurando);
/// linha contendo "salvas"/"pronto"/"disponível"/"confirmad" vira verde; o resto fica na cor neutra
/// de texto de sempre (<c>Brush.TextHi</c>, aplicado no XAML como valor padrão do item, não por este
/// conversor). Casamento por palavra-chave é deliberadamente simples e nunca lança — na pior hipótese
/// (nenhuma palavra-chave bate) a linha fica neutra, nunca errada.
/// </summary>
public sealed class LogLineSeverityToBrushConverter : IValueConverter
{
    private static readonly string[] ErrorKeywords = { "não foi possível", "erro", "falha", "recusou", "negad" };
    private static readonly string[] SuccessKeywords = { "salvas", "pronto", "disponível", "confirmad" };

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var line = (value as string) ?? string.Empty;

        if (ContainsAny(line, ErrorKeywords))
        {
            return Application.Current?.TryFindResource("Brush.Red") as Brush ?? Brushes.OrangeRed;
        }

        if (ContainsAny(line, SuccessKeywords))
        {
            return Application.Current?.TryFindResource("Brush.Green") as Brush ?? Brushes.LimeGreen;
        }

        return Application.Current?.TryFindResource("Brush.TextHi") as Brush ?? Brushes.WhiteSmoke;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException("LogLineSeverityToBrushConverter é usado só pra leitura (texto → cor), nunca no sentido inverso.");

    private static bool ContainsAny(string line, string[] keywords)
    {
        foreach (var keyword in keywords)
        {
            if (line.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
