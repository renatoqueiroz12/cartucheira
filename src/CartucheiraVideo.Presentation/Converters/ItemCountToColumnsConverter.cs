using System.Globalization;
using System.Windows.Data;

namespace CartucheiraVideo.Presentation.Converters;

/// <summary>
/// Sprint Reformulação-Visual-Neon, correção pós-entrega #4 (2026-11-24) — pedido do usuário depois
/// de testar a correção #2 (<c>WrapPanel</c>): "os botões devem distribuir-se horizontalmente. Cada
/// botão deve crescer para ocupar o espaço disponível. Com poucos itens, os botões devem ficar
/// maiores, sem deixar uma grande área vazia. Com mais itens, devem formar novas linhas. Trabalhar
/// com até quatro colunas por linha."
///
/// A correção #2 trocou <c>UniformGrid Columns="5"</c> (fixo) por <c>WrapPanel</c> porque o
/// UniformGrid sempre reservava 5 colunas IGUAIS mesmo com poucos itens (1 item = 4 colunas vazias,
/// um vão enorme). Isso resolveu o vão fantasma, mas criou um problema novo: WrapPanel empacota os
/// itens no tamanho FIXO deles (Width/Height do Button) e nunca estica — com poucos itens, eles
/// ficam agrupados num canto, deixando a mesma área vazia de antes, só que à direita em vez de em
/// colunas separadas.
///
/// A solução correta não é nenhum dos dois extremos: é um UniformGrid cujo <c>Columns</c> não é mais
/// fixo, e sim CALCULADO a partir da quantidade de itens da coleção, limitado a no máximo 4 (pedido
/// explícito: "até quatro colunas por linha como referência"). Com isso:
/// - 1 item → Columns=1 → o botão ocupa 100% da largura disponível (UniformGrid estica os filhos
///   pra preencher cada célula; por isso o Button, no XAML, não tem mais Width fixa, só Height).
/// - 3 itens → Columns=3 → três botões dividindo a largura igualmente, sem vão nenhum.
/// - 6 itens → Columns=4 (teto) → UniformGrid forma automaticamente uma 2ª linha (4 + 2), já que ele
///   sempre distribui TODOS os itens entre o número de colunas informado, calculando as linhas
///   necessárias sozinho — "com mais itens, devem formar novas linhas" sai de graça dessa conta.
/// </summary>
public sealed class ItemCountToColumnsConverter : IValueConverter
{
    private const int MaxColumns = 4;

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var count = value switch
        {
            int i => i,
            System.Collections.ICollection c => c.Count,
            _ => 0
        };

        return Math.Max(1, Math.Min(MaxColumns, count));
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
