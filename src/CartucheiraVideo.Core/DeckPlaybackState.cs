namespace CartucheiraVideo.Core;

/// <summary>
/// Estado de reproducao de um Deck (ou de uma <see cref="PlaybackSession"/>), em vocabulario de
/// playout. Migrou de Presentation/ViewModels pro Core na Sprint 6 (arquitetura §12/item 17):
/// toda a aplicacao usa esta unica definicao — Engine, Api, Automacao, NDI, Logs, integracoes
/// vMix/Resolume vao consumir o mesmo enum, sem duplicar.
///
/// Os 11 valores vem da especificacao visual definitiva (mockup v7, tela "Estados do Deck").
/// Nem todos sao produzidos pela simulacao visual desta sprint (que ainda nao tem Engine real por
/// tras) — a maquina de estado completa (quando cada transicao acontece de verdade) e definida na
/// sprint do Engine.
/// </summary>
public enum DeckPlaybackState
{
    /// <summary>Vazio, ou marcado/pronto pro proximo Play.</summary>
    Cue,

    /// <summary>Lendo metadata / preparando o arquivo pra tocar.</summary>
    Loading,

    /// <summary>Carregado, aguardando Play.</summary>
    Ready,

    /// <summary>Tocando e enviando pros destinos marcados.</summary>
    Playing,

    /// <summary>Tempo congelado no ponto da pausa.</summary>
    Paused,

    /// <summary>Operador movendo o scrubber.</summary>
    Seeking,

    /// <summary>Tocando, mas aguardando dados (rede/disco lento).</summary>
    Buffering,

    /// <summary>Interrompido manualmente (Stop).</summary>
    Stopped,

    /// <summary>Chegou ao fim, antes de voltar pra Cue.</summary>
    Finished,

    /// <summary>Falha ao carregar ou tocar o arquivo.</summary>
    Error,

    /// <summary>Saida/engine indisponivel.</summary>
    Offline
}
