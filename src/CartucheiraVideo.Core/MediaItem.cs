namespace CartucheiraVideo.Core;

/// <summary>
/// O objeto mais importante do sistema: representa qualquer midia (video, audio, imagem, GIF, e
/// futuramente HTML/URL/NDI — ver <see cref="MediaType"/>), nao so video. E puramente um modelo
/// de dados — sem logica, sem dependencia de UI, Engine, NDI ou qualquer tecnologia externa.
///
/// Boa parte das propriedades abaixo (Duration, Resolution, Codec, FrameRate, AudioChannels,
/// Thumbnail, Hash) so e preenchida de verdade a partir da sprint do Explorer/Engine, quando
/// LibVLC.Media.Parse entrar em cena (ver arquitetura §10/§11). Elas nascem definidas aqui desde
/// ja pra nao precisar reabrir esta classe quando essa sprint chegar.
/// </summary>
public sealed class MediaItem
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public required string Name { get; set; }

    /// <summary>Caminho completo do arquivo (ou URL, para MediaType.Url).</summary>
    public required string Path { get; set; }

    public MediaType MediaType { get; set; } = MediaType.Unknown;

    public TimeSpan? Duration { get; set; }

    /// <summary>Ex.: "1920x1080". Texto livre de proposito — formato real ainda nao decidido.</summary>
    public string? Resolution { get; set; }

    public string? Codec { get; set; }

    public double? FrameRate { get; set; }

    public int? AudioChannels { get; set; }

    public long? FileSize { get; set; }

    /// <summary>Caminho de uma miniatura gerada, quando existir.</summary>
    public string? Thumbnail { get; set; }

    /// <summary>Data de criacao do arquivo/registro — usada em ordenacoes futuras.</summary>
    public DateTime? Created { get; set; }

    /// <summary>Ultima modificacao conhecida — usada para detectar alteracoes no arquivo de origem.</summary>
    public DateTime? LastModified { get; set; }

    /// <summary>
    /// Hash de conteudo (algoritmo ainda nao decidido). Util para deduplicacao, cache de
    /// thumbnails e sincronizacao — calculado numa sprint futura.
    /// </summary>
    public string? Hash { get; set; }

    /// <summary>Marcacoes livres (ex.: "Comercial", "Vinheta", "Jornal", "Esporte").</summary>
    public IList<string> Tags { get; init; } = new List<string>();

    /// <summary>Pares chave/valor livres para metadata que ainda nao virou propriedade de primeira classe.</summary>
    public IDictionary<string, string> Metadata { get; init; } = new Dictionary<string, string>();
}
