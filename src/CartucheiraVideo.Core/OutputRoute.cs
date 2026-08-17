namespace CartucheiraVideo.Core;

/// <summary>
/// Um destino de saida configurado num <see cref="Deck"/> (ex.: "vMix, input 2"). Puramente dado —
/// quem envia frames/comandos de verdade pro destino e a Infrastructure/Ndi numa sprint futura.
/// </summary>
public sealed class OutputRoute
{
    public Guid Id { get; init; } = Guid.NewGuid();

    /// <summary>Rotulo do destino (ex.: "vMix", "Resolume — Layer 3"). Descritivo, nao um identificador interno.</summary>
    public required string Destination { get; set; }

    public bool Enabled { get; set; } = true;

    /// <summary>Ordem de prioridade quando um Deck tem mais de um destino habilitado.</summary>
    public int Priority { get; set; }

    public OutputRouteType OutputType { get; set; } = OutputRouteType.Unknown;

    /// <summary>Configuracao especifica do tipo de destino (ex.: host/porta do vMix, nome do layer no Resolume) — formato livre ate cada integracao ser implementada.</summary>
    public IDictionary<string, string> Configuration { get; init; } = new Dictionary<string, string>();

    /// <summary>Espaco reservado pra metadata que ainda nao virou propriedade de primeira classe.</summary>
    public IDictionary<string, object> FutureMetadata { get; init; } = new Dictionary<string, object>();
}
