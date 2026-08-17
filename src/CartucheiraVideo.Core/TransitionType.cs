namespace CartucheiraVideo.Core;

/// <summary>
/// Tipo de transição usado por uma <see cref="AutomationAction"/> ao cortar o vMix/Resolume pra um
/// Deck. Corte seco é o padrão recomendado pela pesquisa técnica (arquitetura §13.3, "switching sem
/// black") — Fade é a opção configurável com duração em milissegundos (<see
/// cref="AutomationAction.TransitionDurationMs"/>), teto sugerido ~150ms pela mesma pesquisa.
/// </summary>
public enum TransitionType
{
    Cut,
    Fade
}
