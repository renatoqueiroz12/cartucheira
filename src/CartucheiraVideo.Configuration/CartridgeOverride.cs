namespace CartucheiraVideo.Configuration;

/// <summary>
/// Customização persistida de UM cartucho específico (cor e/ou loop) — Sprint Cartucheira 2, parte 2
/// (2026-08-10). Identificado pelo caminho completo do arquivo de áudio (<see cref="FilePath"/>),
/// porque o cartucho em si (Core.AudioPad / Presentation.VtButtonViewModel) é recriado do zero toda
/// vez que a pasta do programa é lida (AudioBankViewModel.LoadCartridgesFromRealFolder) — sem essa
/// persistência externa, a cor/loop escolhidos pelo operador (via Ctrl+clique direito no cartucho)
/// se perderiam a cada recarregar/reiniciar o programa.
/// </summary>
public sealed class CartridgeOverride
{
    /// <summary>Caminho completo do arquivo de áudio deste cartucho — chave de correspondência com VtButtonViewModel.FilePath.</summary>
    public required string FilePath { get; set; }

    /// <summary>Cor escolhida pelo operador (hex, ex.: "#FF2D7DFF") — nulo não deveria ocorrer na lista persistida (um override sem cor nem loop é removido, ver AudioBankViewModel/MainViewModel), mas fica nulável por segurança.</summary>
    public string? ColorOverride { get; set; }

    /// <summary>Verdadeiro = este cartucho reinicia sozinho ao terminar (passado pro Engine.AudioEngine.Play a cada Play) em vez de simplesmente parar.</summary>
    public bool LoopEnabled { get; set; }
}
