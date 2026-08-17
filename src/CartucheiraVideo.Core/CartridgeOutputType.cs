namespace CartucheiraVideo.Core;

/// <summary>
/// Tipo de saída de um cartucho da Cartucheira (arquitetura §14.5) — desde 2026-08-08 cada cartucho
/// pode ser do tipo áudio OU vídeo, revisando a regra antiga "Botoneira só áudio" (§10/§6 item 7).
/// Sprint Cartucheira (2026-08-09): só <see cref="Audio"/> é consumido de ponta a ponta (motor de
/// áudio real, ver <c>Engine.AudioEngine</c>) — <see cref="Video"/> existe aqui como modelagem de
/// domínio (arquitetura §12, regra 3: domínio nasce antes, conexão só quando o módulo entra em
/// desenvolvimento), mas nenhum cartucho de vídeo tem destino de saída implementado ainda (§14.6
/// tem perguntas em aberto sobre pra onde essa saída vai — NDI próprio? preview própria? — que
/// não foram decididas).
/// </summary>
public enum CartridgeOutputType
{
    Audio,
    Video
}
