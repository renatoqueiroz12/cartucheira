namespace CartucheiraVideo.Core;

/// <summary>
/// Um item (vídeo) dentro da Playlist de um Programa numa Data (<see cref="PlaylistEdition"/>) —
/// Sprint Playlist-Dashboard (2026-11-14). Pedido do usuário: dashboard web pros produtores
/// organizarem o que vai ao ar, com ingest de vídeo e campos extras (GC) que a Cartucheira usa pra
/// disparar o Título do vMix quando o vídeo for ao ar. Ver arquitetura/playlist-modulo-roteiro.md
/// pro roteiro completo.
///
/// Sprint 1 (este arquivo) só modela o formato — nenhum vídeo é recebido/validado ainda (Sprint 2:
/// upload pela dashboard + validação real via LibVLC, mesmo princípio já usado em
/// <c>PlaybackEngine.TryValidateMediaIsPlayable</c>), e <see cref="GcFields"/> fica sempre vazio até
/// a Sprint 3 (formulário de GC na dashboard + disparo real do Título no vMix).
/// </summary>
public sealed class PlaylistEntry
{
    public Guid Id { get; init; } = Guid.NewGuid();

    /// <summary>Posição do item dentro da Playlist do dia — define a ordem de exibição e, mais pra frente (Sprint 4), o avanço automático do modo Auto.</summary>
    public int Order { get; set; }

    /// <summary>Nome do arquivo de vídeo dentro da pasta desta Edição — vazio até o ingest (Sprint 2) acontecer de verdade.</summary>
    public string FileName { get; set; } = string.Empty;

    public PlaylistEntryStatus Status { get; set; } = PlaylistEntryStatus.PendingIngest;

    /// <summary>
    /// Campos de texto livres (nome do campo → valor) que a Cartucheira vai setar no Título (GT) do
    /// vMix quando este item for ao ar — os nomes de campo precisam casar com os nomes reais usados
    /// no Título configurado no vMix do usuário. Disparo real ainda não implementado (Sprint 3) —
    /// este dicionário só existe pra já ter o formato certo pronto.
    /// </summary>
    public Dictionary<string, string> GcFields { get; init; } = new();

    /// <summary>Duração real do arquivo — preenchida na validação do ingest (Sprint 2); nula até lá.</summary>
    public string? Duration { get; set; }

    public string? Resolution { get; set; }

    public string? Codec { get; set; }
}
