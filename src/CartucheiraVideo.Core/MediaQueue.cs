using System.Collections.ObjectModel;

namespace CartucheiraVideo.Core;

/// <summary>
/// Uma fila dinamica de <see cref="QueueItem"/> (ex.: a Fila Rapida acima dos Decks). Nasce sem
/// limite fixo de posicoes — quem decide quantas posicoes mostrar e a interface (hoje: 3), nunca
/// o Core. Suporta multiplas instancias (o usuario pode querer "criar varias filas" no futuro),
/// por isso e uma classe com <see cref="Id"/>/<see cref="Name"/> proprios, nao um singleton.
///
/// Por dentro usa <see cref="ObservableCollection{T}"/> (System.Collections.ObjectModel, parte do
/// BCL — nao e WPF) para que a Presentation possa fazer binding direto em <see cref="Items"/> sem
/// precisar de uma copia paralela. As operacoes abaixo sao manipulacao pura de colecao (adicionar,
/// remover, reordenar, limpar) — nao e logica de negocio (isso continua no Engine/Services quando
/// existirem).
/// </summary>
public sealed class MediaQueue
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public required string Name { get; set; }

    /// <summary>Quantas posicoes a interface deve exibir por padrao. Puramente uma sugestao de exibicao — nao limita <see cref="Items"/>.</summary>
    public int VisibleSlotCount { get; set; } = 3;

    public ObservableCollection<QueueItem> Items { get; } = new();

    public void Add(QueueItem item) => Items.Add(item);

    public void RemoveAt(int index)
    {
        if (index >= 0 && index < Items.Count)
        {
            Items.RemoveAt(index);
        }
    }

    public void Clear() => Items.Clear();

    /// <summary>Move um item de uma posicao pra outra dentro da fila.</summary>
    public void Reorder(int fromIndex, int toIndex)
    {
        if (fromIndex < 0 || fromIndex >= Items.Count || toIndex < 0 || toIndex >= Items.Count || fromIndex == toIndex)
        {
            return;
        }

        var item = Items[fromIndex];
        Items.RemoveAt(fromIndex);
        Items.Insert(toIndex, item);
    }

    /// <summary>Ajusta quantas posicoes a interface deve exibir (ex.: operador aumenta de 3 pra 5).</summary>
    public void Resize(int visibleSlotCount)
    {
        if (visibleSlotCount > 0)
        {
            VisibleSlotCount = visibleSlotCount;
        }
    }
}
