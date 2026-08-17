namespace CartucheiraVideo.Core;

/// <summary>
/// Representa um cartucho (botão) da Cartucheira/Botoneira. Criado na Sprint 6 só como modelagem —
/// ficou <b>desconectado</b> até a Sprint Cartucheira (2026-08-09), quando
/// <c>Presentation.ViewModels.VtButtonViewModel</c> migrou de <c>VtButtonConfig</c> fictício pra
/// este tipo real (arquitetura §12, regra 3: domínio nasce antes, conexão só quando o módulo entra
/// em desenvolvimento) — hoje é populado a partir de arquivos de verdade lidos da subpasta de um
/// programa aberto pelo operador (<c>AudioBankViewModel.LoadCartridgesFromRealFolder</c>, dentro
/// de uma pasta raiz configurada em Configurações → Cartucheira), não mais inventado.
/// </summary>
public sealed class AudioPad
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public required string Name { get; set; }

    public required MediaItem Media { get; set; }

    /// <summary>Cor de fundo do pad, em hexadecimal (ex.: "#FF3B82F6").</summary>
    public required string Color { get; set; }

    /// <summary>Categoria livre (ex.: "Vinhetas", "Efeitos", "Jingles").</summary>
    public string? Category { get; set; }

    /// <summary>
    /// Áudio ou vídeo (arquitetura §14.5) — <see cref="CartridgeOutputType.Audio"/> por padrão.
    /// Sprint Cartucheira (2026-08-09) só implementa o motor pra <c>Audio</c> de ponta a ponta;
    /// <c>Video</c> existe na modelagem mas não tem destino de saída implementado ainda (§14.6).
    /// </summary>
    public CartridgeOutputType OutputType { get; set; } = CartridgeOutputType.Audio;
}
