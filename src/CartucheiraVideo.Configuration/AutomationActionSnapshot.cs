namespace CartucheiraVideo.Configuration;

/// <summary>
/// Uma ação "On Play"/"On End" persistida (arquitetura §10) — espelha <c>Core.AutomationAction</c>,
/// mas com <see cref="Destination"/>/<see cref="Transition"/> como string (nome do enum do Core, ex.
/// "VMix"/"Fade") em vez do tipo direto, já que este projeto não referencia o Core (ver comentário
/// do <c>.csproj</c>). Quem converte de volta pro enum de verdade é a Presentation, ao aplicar o
/// snapshot num Deck.
/// </summary>
public sealed class AutomationActionSnapshot
{
    public string Destination { get; set; } = string.Empty;

    public string Transition { get; set; } = string.Empty;

    public int TransitionDurationMs { get; set; }
}
