namespace CartucheiraVideo.Presentation.ViewModels;

/// <summary>
/// Exibicao de uma saida NDI configurada (ex.: vMix, Resolume) na barra superior. Nesta sprint e
/// so estado visual — IsConnected fica sempre false porque nao ha integracao real com NDI/vMix/
/// Resolume ainda (isso e Etapa 2). Mostrar "nao conectado" em vez de fingir uma conexao.
/// </summary>
public sealed class NdiOutputStatusViewModel
{
    public required string Name { get; init; }

    public bool IsConnected { get; init; }
}
