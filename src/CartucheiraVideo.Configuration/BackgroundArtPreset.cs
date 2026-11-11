namespace CartucheiraVideo.Configuration;

/// <summary>
/// Sprint Arte-de-Fundo (2026-11-10): uma arte de espera cadastrada em Configurações → Arte de
/// Fundo ("Arte 1", "Arte 2", "Arte 3" etc., no vocabulário do usuário) — uma imagem estática
/// (PNG/JPG) que algum Deck marcado com <see cref="DeckSettingsSnapshot.UseBackgroundArt"/> manda
/// pra saída NDI dele quando não está tocando nada. Ver <see cref="AppSettingsSnapshot.BackgroundArtPresets"/>
/// (lista completa) e <see cref="AppSettingsSnapshot.ActiveBackgroundArtId"/> (qual delas está em
/// uso agora — só uma por vez, decisão explícita do usuário: "a gente pode criar um preset, arte 1,
/// arte 2, arte 3, e aí selecionar qual que é pra usar").
/// </summary>
public sealed class BackgroundArtPreset
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Name { get; set; } = string.Empty;

    /// <summary>Caminho completo do arquivo de imagem no disco do usuário — nenhuma cópia é feita pra dentro do programa (Sprint 1 desta funcionalidade); se o arquivo for movido/apagado, a arte simplesmente para de carregar (falha honesta, ver <c>BackgroundArtRenderer</c>).</summary>
    public string FilePath { get; set; } = string.Empty;
}
