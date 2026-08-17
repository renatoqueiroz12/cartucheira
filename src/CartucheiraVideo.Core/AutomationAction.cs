namespace CartucheiraVideo.Core;

/// <summary>
/// Uma ação de automação configurada num <see cref="Deck"/> (aba "Automação" da tela de
/// Configuração do Deck, arquitetura §10): "ao dar Play" ou "ao terminar", com um Destino, uma
/// transição e um tempo configuráveis. Pra vMix/Resolume isto ainda é puramente dado — quem envia
/// o comando HTTP/TCP de verdade é a <c>Infrastructure</c> numa sprint futura (vMix/Resolume
/// Controller, passos 4/5 do roteiro em <c>status-implementacao.md</c>).
///
/// Exceção, desde a Sprint Deck-pausa-Cartucheira v2 (2026-08-11):
/// <see cref="OutputRouteType.Cartucheira"/> como <see cref="Destination"/> de uma ação "Ao dar
/// Play" JÁ FUNCIONA DE VERDADE, sem esperar nenhuma sprint futura — pausa as duas Cartucheiras
/// (Vinhetas/Trilhas), que retomam sozinhas assim que este Deck termina de tocar (sozinho ou por
/// Stop manual). Ver <c>MainViewModel.PauseCartucheiraFor</c>/<c>ResumeCartucheiraFor</c> e
/// <c>DeckSettingsViewModel.OnPlayDestinationOptions</c> (só a lista "Ao dar Play" oferece esta
/// opção — o retorno é automático, não uma segunda ação configurável em "Ao terminar").
/// </summary>
public sealed class AutomationAction
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public AutomationTrigger Trigger { get; set; }

    /// <summary>Vocabulário compartilhado com <see cref="OutputRoute.OutputType"/> em vez de um tipo novo, pra não duplicar — ver <see cref="OutputRouteType"/> pra nota sobre <see cref="OutputRouteType.Cartucheira"/>, que não é uma <see cref="OutputRoute"/> de verdade.</summary>
    public OutputRouteType Destination { get; set; } = OutputRouteType.Unknown;

    public TransitionType Transition { get; set; } = TransitionType.Cut;

    /// <summary>Só relevante quando <see cref="Transition"/> é <see cref="TransitionType.Fade"/>. Zero quando é Cut.</summary>
    public int TransitionDurationMs { get; set; }
}
