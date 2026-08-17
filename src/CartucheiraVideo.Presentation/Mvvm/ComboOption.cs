namespace CartucheiraVideo.Presentation.Mvvm;

/// <summary>
/// Par valor/rotulo pra alimentar um ComboBox ligado a um enum, com rotulo em portugues em vez do
/// ToString() cru do enum (ex.: TransitionType.Fade -> "Fade (com duracao)"). Uso: ItemsSource pra
/// uma lista de ComboOption&lt;T&gt;, DisplayMemberPath="Label", SelectedValuePath="Value",
/// SelectedValue ligado a propriedade do tipo T. Generico e reutilizavel — primeiro uso e nas duas
/// listas da aba Automacao do DeckSettingsPanel (Destino, Transicao; ate a Sprint Config Geral,
/// parte 1, isto vivia no antigo DeckSettingsWindow), mas serve pra qualquer combo futuro do mesmo
/// tipo.
/// </summary>
public sealed class ComboOption<T>
{
    public required T Value { get; init; }

    public required string Label { get; init; }

    public override string ToString() => Label;
}
