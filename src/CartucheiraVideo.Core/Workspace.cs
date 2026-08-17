namespace CartucheiraVideo.Core;

/// <summary>
/// Sugestao do usuario na aprovacao da Sprint 6: representa um projeto/evento completo — tudo que
/// precisa ser salvo/carregado de uma vez pra o operador trocar de evento sem reconfigurar tudo na
/// mao. Hoje praticamente sem uso (nenhuma sprint ainda salva/carrega Workspace), mas nasce
/// modelada agora pra nao precisar reorganizar o dominio quando "salvar/abrir projeto" entrar no
/// roadmap.
/// </summary>
public sealed class Workspace
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public required string Name { get; set; }

    public MediaLibrary MediaLibrary { get; init; } = new();

    public IList<Deck> Decks { get; init; } = new List<Deck>();

    public IList<AudioPad> AudioPads { get; init; } = new List<AudioPad>();

    /// <summary>Configuracoes livres do workspace (formato ainda nao decidido — provavelmente JSON, ver Configuration na arquitetura §2).</summary>
    public IDictionary<string, string> Settings { get; init; } = new Dictionary<string, string>();
}
