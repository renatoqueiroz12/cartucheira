namespace CartucheiraVideo.Core;

/// <summary>
/// Tipo de destino de uma <see cref="OutputRoute"/>. Cada valor tera sua propria configuracao
/// (ver <see cref="OutputRoute.Configuration"/>) quando a integracao correspondente for
/// implementada — vMix e Resolume sao as do escopo atual (arquitetura §10); os demais valores
/// existem so pra o enum nao precisar ser reaberto quando o roadmap crescer.
///
/// <see cref="Cartucheira"/> (Sprint Deck-pausa-Cartucheira v2, 2026-08-11) e diferente dos
/// demais: nao e uma <see cref="OutputRoute"/> real de verdade (nenhum <c>Deck</c> cria uma rota
/// desse tipo em <c>OutputRoutes</c>) — reaproveita este mesmo enum so pra aparecer como opcao de
/// <see cref="AutomationAction.Destination"/> na lista "Ao dar Play" da aba Automacao, evitando
/// duplicar vocabulario (mesmo racional ja documentado em <see cref="AutomationAction"/>).
/// Diferente de VMix/Resolume (ainda so dado salvo, sem disparo real), uma acao "Ao dar Play →
/// Cartucheira" JA FUNCIONA DE VERDADE: pausa as duas Cartucheiras — ver
/// <c>MainViewModel.PauseCartucheiraFor</c>.
/// </summary>
public enum OutputRouteType
{
    Unknown,
    VMix,
    Resolume,
    Ndi,
    CasparCg,
    Obs,
    File,
    Streaming,
    Gpio,
    Osc,
    Midi,
    Cartucheira
}
