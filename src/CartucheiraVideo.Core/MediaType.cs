namespace CartucheiraVideo.Core;

/// <summary>
/// Tipo de uma <see cref="MediaItem"/>. O Core nao conhece "video" — conhece "midia": o mesmo
/// objeto MediaItem representa qualquer coisa que possa ser carregada num Deck ou disparada pra
/// uma saida. Quem decide como reproduzir cada tipo e o Engine (sprint futura), nao o Core.
///
/// Preparado para crescer sem quebrar quem ja usa o enum — valores adicionados aqui sao sempre
/// no final da lista.
/// </summary>
public enum MediaType
{
    /// <summary>Tipo nao identificado ainda (ex.: arquivo recem-adicionado, metadata nao lida).</summary>
    Unknown,

    Video,

    Audio,

    Image,

    /// <summary>Pagina HTML local ou embutida (ex.: overlay, grafico).</summary>
    Html,

    /// <summary>URL/stream externo (ex.: um link de video ao vivo).</summary>
    Url,

    /// <summary>Fonte NDI externa, tratada como midia carregavel num Deck.</summary>
    Ndi,

    /// <summary>Fonte ao vivo generica (camera, capture card) que nao e um arquivo.</summary>
    Live,

    /// <summary>Uma playlist (sequencia de outras midias) tratada como uma midia so.</summary>
    Playlist
}
