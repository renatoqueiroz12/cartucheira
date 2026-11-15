namespace CartucheiraVideo.Core;

/// <summary>
/// Uma Lauda — a unidade de conteúdo do Espelho (a lista ordenada de <see cref="PlaylistEdition.Laudas"/>
/// de uma Edição) — Sprint Playlist-Dashboard, Sprint 2 (2026-11-15). Renomeado de
/// <c>PlaylistEntry</c> (Sprint 1, que só modelava "um vídeo") pra <c>PlaylistLauda</c>, terminologia
/// confirmada com o usuário a partir da hierarquia real de redação de TV: Programa → Edição →
/// Espelho → Lauda → Elementos. Importante, nas palavras do usuário: "não confunda lauda com VT. Uma
/// lauda pode conter um VT, mas também pode ser uma nota, uma entrevista ao vivo, uma passagem de
/// apresentador ou uma abertura" — ou seja, uma Lauda sem nenhum vídeo é perfeitamente válida.
///
/// Elementos possíveis dentro de uma Lauda, confirmados com o usuário: VT (vídeo, <see cref="FileName"/>),
/// GC (gerador de caracteres, <see cref="GcFields"/>), TP (teleprompter, <see cref="TpText"/>), OFF
/// (texto de locução, <see cref="OffText"/>), Imagens de apoio (<see cref="ImageFileNames"/>), Duração
/// (prevista agora, realizada só depois de uma validação de ingest real que ainda não existe) e
/// Observações de produção (<see cref="Notes"/>).
///
/// <see cref="Type"/> é texto livre (decisão explícita do usuário — sem lista fechada de tipos, pra
/// não travar casos fora do padrão; o produtor digita "VT", "Nota", "Entrevista", "Passagem",
/// "Abertura", "Encerramento", "Link ao vivo" etc. como quiser).
///
/// Ainda não implementados (sprints futuras): disparo automático do GC no vMix quando a Lauda for ao
/// ar, validação real de VT/Imagens (LibVLC, mesmo princípio de
/// <c>PlaybackEngine.TryValidateMediaIsPlayable</c>), e reordenação do Espelho (sem drag-and-drop
/// ainda — uma Lauda nova sempre entra no fim da lista).
/// </summary>
public sealed class PlaylistLauda
{
    public Guid Id { get; init; } = Guid.NewGuid();

    /// <summary>Posição da Lauda dentro do Espelho — define a ordem de exibição e, mais pra frente (Sprint 4), o avanço automático do modo Auto. Atribuída automaticamente ao fim da lista na criação.</summary>
    public int Order { get; set; }

    public required string Title { get; set; }

    /// <summary>Tipo da Lauda — texto livre (ex.: "VT", "Nota", "Entrevista", "Passagem", "Abertura", "Encerramento", "Link ao vivo"). Pode ficar vazio.</summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>Nome do arquivo de vídeo (VT) dentro da pasta desta Edição — vazio se esta Lauda não tiver VT (ex.: Entrevista ao vivo, Passagem de apresentador).</summary>
    public string FileName { get; set; } = string.Empty;

    public PlaylistLaudaStatus Status { get; set; } = PlaylistLaudaStatus.PendingIngest;

    /// <summary>
    /// Campos de texto livres (nome do campo → valor) que a Cartucheira vai setar no Título (GT) do
    /// vMix quando esta Lauda for ao ar — os nomes de campo precisam casar com os nomes reais usados
    /// no Título configurado no vMix do usuário. Disparo real ainda não implementado (sprint futura).
    /// </summary>
    public Dictionary<string, string> GcFields { get; init; } = new();

    /// <summary>Texto de teleprompter (TP) desta Lauda — opcional.</summary>
    public string? TpText { get; set; }

    /// <summary>Texto de locução (OFF) desta Lauda — opcional.</summary>
    public string? OffText { get; set; }

    /// <summary>Nomes dos arquivos de imagem de apoio, dentro da subpasta "imagens" desta Edição.</summary>
    public IList<string> ImageFileNames { get; init; } = new List<string>();

    /// <summary>Duração prevista, digitada livremente pelo produtor (ex.: "02:30") — não tem relação com a duração real de nenhum arquivo.</summary>
    public string? DurationPlanned { get; set; }

    /// <summary>Duração real do arquivo de VT — só preenchida quando existir validação real de ingest (sprint futura); nula até lá.</summary>
    public string? Duration { get; set; }

    public string? Resolution { get; set; }

    public string? Codec { get; set; }

    /// <summary>Observações livres de produção.</summary>
    public string? Notes { get; set; }
}
