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

    /// <summary>
    /// Sprint Arte-de-Fundo (2026-11-10): verdadeiro = quando este Deck NÃO estiver tocando (Cue,
    /// Stopped ou Finished — ver <c>Presentation.ViewModels.DeckViewModel.IsBackgroundArtIdleState</c>),
    /// a saída NDI dele passa a mandar continuamente a arte de fundo ATIVA (configurada em Configurações → Arte
    /// de Fundo), em vez de simplesmente parar de mandar quadro nenhum (comportamento de sempre,
    /// ainda usado quando este toggle está desligado ou nenhuma arte está marcada como ativa).
    /// Pedido do usuário: o Deck 2 vai para o Resolume/telões, e um VT que termina deve dar lugar a
    /// uma arte de espera na tela, em vez de ficar com o último quadro congelado ou sem sinal. Padrão
    /// falso — precisa ser ligado explicitamente na aba Resolume da tela de Configuração de cada
    /// Deck (o usuário só precisa disso no Deck 2, já que o Deck 1 vai para o vMix, não pros telões).
    /// </summary>
    public bool UseBackgroundArt { get; set; }

    /// <summary>
    /// Sprint Telas-Saida-Direta (2026-11-10): verdadeiro = alem do NDI (pra Resolume/vMix), este
    /// Deck tambem desenha seu video DIRETAMENTE em janelas sem borda sobre os monitores do Windows
    /// listados em <c>ScreenOutputDeviceNames</c> — usando a mesma placa de video do PC conectada aos
    /// telões, sem passar pelo Resolume. Pedido do usuário: ele conectou uma placa com varias saidas
    /// de video nesse PC e quer mandar o Deck 2 direto pra essas telas, deixando de depender do
    /// Resolume pra isso ("não vou mais mandar pro resolume, vou mandar pra essas telas"). Nao
    /// exclui nem desliga o Resolume/vMix — e puramente aditivo, o usuario so deixa de marcar
    /// "Enviar para Resolume" quando migrar de verdade. Padrao falso.
    /// </summary>
    public bool SendToScreens { get; set; }

    /// <summary>
    /// Identificadores estaveis (<c>System.Windows.Forms.Screen.DeviceName</c>, ex.: <c>"\\.\DISPLAY2"</c>)
    /// dos monitores do Windows escolhidos pra receber o video deste Deck quando <c>SendToScreens</c>
    /// estiver ligado. Populado/editado na aba "Telas" da tela de Configuracao do Deck — ver
    /// <c>Presentation.ViewModels.ScreenOptionViewModel</c>. Mantido mesmo se o monitor estiver
    /// temporariamente desconectado (o usuario pode religar a placa depois sem perder a escolha).
    /// </summary>
    public IList<string> ScreenOutputDeviceNames { get; init; } = new List<string>();
}
