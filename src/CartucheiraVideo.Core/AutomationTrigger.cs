namespace CartucheiraVideo.Core;

/// <summary>
/// Momento em que uma <see cref="AutomationAction"/> dispara — corresponde exatamente aos dois
/// eventos descritos na arquitetura §10 ("ações On Play / On End, cada uma com destino, transição,
/// tempo"): ao dar Play no Deck, ou ao vídeo terminar/ser parado (Stop dispara as mesmas ações de
/// On End — §10, "Comportamento do Stop").
/// </summary>
public enum AutomationTrigger
{
    OnPlay,
    OnEnd
}
