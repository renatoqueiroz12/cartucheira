namespace CartucheiraVideo.Core;

/// <summary>
/// Representa um Deck (Deck 1, Deck 2) como conceito de dominio — nao a ViewModel que a tela usa
/// pra exibi-lo. A <c>DeckViewModel</c> (Presentation) passa a representar/envolver este objeto,
/// em vez de guardar o estado do Deck em propriedades soltas. Puramente dado, sem logica: quem
/// decide transicoes de estado, valida drops, etc. e o Engine/Core.PlayoutController numa sprint
/// futura.
/// </summary>
public sealed class Deck
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public required string Name { get; set; }

    public DeckPlaybackState State { get; set; } = DeckPlaybackState.Cue;

    /// <summary>Midia atualmente associada ao Deck (Cue/Playing/Paused). Nulo quando vazio.</summary>
    public MediaItem? CurrentMedia { get; set; }

    public IList<OutputRoute> OutputRoutes { get; init; } = new List<OutputRoute>();

    public bool LoopEnabled { get; set; }

    /// <summary>
    /// Nome do canal NDI que este Deck vai gerar (arquitetura §13.1: "qual canal NDI de saida
    /// aquele Deck vai gerar"). Propriedade propria do Deck, nao um OutputRoute — e a identidade da
    /// PROPRIA saida deste Deck, nao um destino que consome ela (vMix/Resolume, que sim sao
    /// OutputRoutes, ja que "consomem" essa fonte NDI). Nulo/vazio ate a tela de Configuracao
    /// (Sprint Config) ou o operador definir um valor; o NDI Sender (proximo passo do roteiro) e
    /// quem de fato usa este valor pra registrar a fonte.
    /// </summary>
    public string? NdiChannelName { get; set; }

    /// <summary>
    /// Identificador do dispositivo de saida de audio do Windows (mmdevice) — ou, desde a Sprint
    /// Livewire-4, uma Saida Livewire nativa (prefixo <c>"livewire:"</c>) — usado pelo audio LOCAL
    /// deste Deck (Sprint Audio-Dispositivo-Deck, 2026-08-11). Nulo = dispositivo padrao do sistema,
    /// comportamento de sempre. NAO afeta o audio que ja vai pro NDI (Sprint NDI-audio, mesmo dia):
    /// esse caminho usa um player headless separado, com o audio inteiramente desviado por callback
    /// direto pra NdiSender — quem recebe o NDI continua recebendo audio independente de qual
    /// dispositivo local for escolhido aqui.
    ///
    /// Sprint Áudio-Unificado (2026-09-15): usado tanto por <c>Play</c> quanto por <c>Preview</c> —
    /// o campo separado <c>PreviewAudioDeviceId</c> que existia desde a Sprint Preview-Isolado
    /// (2026-08-15) foi REMOVIDO. Motivo: a dupla seleção (uma placa pro Play, outra pro Preview) foi
    /// a causa raiz de uma sequência de bugs sérios (áudio saindo nas duas placas ao mesmo tempo,
    /// engasgo no início, um revert de emergência por silêncio total) — o usuário decidiu que faz
    /// mais sentido usar UMA placa só pras duas funções, e controlar a diferença entre Play e Preview
    /// numa mesa/console externa através dos avisos UDP dedicados
    /// (<c>DeckSettingsSnapshot.OutboundUdpOnPreviewCommand</c>/<c>OutboundUdpOnPreviewEndCommand</c>,
    /// espelhando os que já existiam pro Play). Preview NÃO fica mais isolado da Saída Livewire por
    /// causa disso (decisão explícita do usuário): se a placa única configurada for uma Saída
    /// Livewire, o Preview agora sai por ela também, exatamente como o Play. Preview continua NUNCA
    /// alimentando o NDI (ver Engine.PlaybackEngine.PlayPreviewOnly) — essa isolação é sobre o vídeo/
    /// captura NDI, não sobre a placa de áudio local/Livewire, e não foi tocada por esta sprint.
    /// </summary>
    public string? AudioDeviceId { get; set; }

    /// <summary>
    /// Acoes "On Play"/"On End" configuradas pro Deck (aba Automacao, arquitetura §10) — cada uma
    /// corta um destino (vMix/Resolume) com uma transicao/tempo. Populada pela tela de
    /// Configuracao; disparada de verdade so quando o vMix/Resolume Controller existir (passos 4/5
    /// do roteiro).
    /// </summary>
    public IList<AutomationAction> AutomationActions { get; init; } = new List<AutomationAction>();
}
