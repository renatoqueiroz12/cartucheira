namespace CartucheiraVideo.Core;

/// <summary>
/// Um item posicionado numa <see cref="MediaQueue"/> (ex.: a Fila Rapida acima dos Decks).
/// Puramente dado — quem decide o que fazer com um QueueItem (ex.: "enviar pro Deck 1") e a
/// Presentation/Core.PlayoutController numa sprint futura, nao esta classe.
/// </summary>
public sealed class QueueItem
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public required string Name { get; set; }

    public required MediaItem Media { get; set; }
}
