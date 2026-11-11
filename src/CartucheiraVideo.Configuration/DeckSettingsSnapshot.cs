namespace CartucheiraVideo.Configuration;

/// <summary>
/// Subconjunto persistível das configurações de um Deck (arquitetura §10) — só os campos que a tela
/// de Configuração já coleta hoje (<c>DeckSettingsViewModel</c>). Deliberadamente NÃO inclui estado
/// de reprodução em tempo real (State/CurrentMedia do <c>Core.Deck</c>) — isso não é "configuração",
/// é sessão, e não faz sentido restaurar um Deck já "tocando" ao abrir o programa.
/// </summary>
public sealed class DeckSettingsSnapshot
{
    public string? NdiChannelName { get; set; }

    /// <summary>Dispositivo de saída de áudio do Windows (mmdevice), ou uma Saída Livewire nativa — Sprint Áudio-Dispositivo-Deck (2026-08-11). Nulo = padrão do sistema. Sprint Áudio-Unificado (2026-09-15): usado tanto pelo Play quanto pelo Preview deste Deck — o campo separado <c>PreviewAudioDeviceId</c> foi removido (ver comentário completo em <c>Core.Deck.AudioDeviceId</c>).</summary>
    public string? AudioDeviceId { get; set; }

    public bool VMixEnabled { get; set; }

    public string VMixHost { get; set; } = string.Empty;

    public string VMixPort { get; set; } = string.Empty;

    public string VMixInput { get; set; } = string.Empty;

    public bool ResolumeEnabled { get; set; }

    public string ResolumeHost { get; set; } = string.Empty;

    public string ResolumePort { get; set; } = string.Empty;

    public string ResolumeLayer { get; set; } = string.Empty;

    /// <summary>
    /// Sprint Arte-de-Fundo (2026-11-10): verdadeiro = a saída NDI deste Deck manda a arte ativa
    /// (<see cref="AppSettingsSnapshot.ActiveBackgroundArtId"/>) quando ele não está tocando, em vez
    /// de parar de mandar quadro nenhum. Ver <c>Core.Deck.UseBackgroundArt</c> pro
    /// comportamento completo — este campo só persiste o valor marcado na aba Resolume da tela de
    /// Configuração deste Deck. Padrão falso.
    /// </summary>
    public bool UseBackgroundArt { get; set; }

    /// <summary>
    /// Sprint Telas-Saida-Direta (2026-11-10): verdadeiro = este Deck desenha seu video direto numa
    /// janela sem borda sobre cada monitor do Windows listado em <see cref="ScreenOutputDeviceNames"/>,
    /// usando a placa de video local do PC — sem depender do Resolume. Ver <c>Core.Deck.SendToScreens</c>
    /// pro comportamento completo. Padrão falso.
    /// </summary>
    public bool SendToScreens { get; set; }

    /// <summary>
    /// Identificadores dos monitores escolhidos — ver <c>Core.Deck.ScreenOutputDeviceNames</c>. Lista
    /// vazia por padrão.
    /// </summary>
    public List<string> ScreenOutputDeviceNames { get; set; } = new();

    public List<AutomationActionSnapshot> OnPlayActions { get; set; } = new();

    public List<AutomationActionSnapshot> OnEndActions { get; set; } = new();

    /// <summary>
    /// Verdadeiro = este Deck recebe comando externo de verdade por UDP (mesa de áudio que o
    /// próprio usuário está desenvolvendo) — Sprint Comando Externo Decks (2026-08-12), mesmo
    /// padrão de "um toggle liga o canal inteiro" já usado por
    /// <see cref="CartridgeSettingsSnapshot.VinhetasExternalTriggerEnabled"/>. Só cobre Play/Pause
    /// nesta sprint (arquitetura §15) — Stop/Loop/Next ficaram de fora por decisão do usuário.
    /// </summary>
    public bool ExternalTriggerEnabled { get; set; }

    /// <summary>Texto que a mesa manda por UDP (porta em <see cref="AppSettingsSnapshot.UdpTriggerPort"/> — UMA porta só, compartilhada por Cartucheira E os dois Decks) pra dar PLAY neste Deck. Vazio = nenhum comando configurado ainda, canal fica sem efeito mesmo com o toggle ligado.</summary>
    public string UdpTriggerPlayCommand { get; set; } = string.Empty;

    /// <summary>Mesma ideia de <see cref="UdpTriggerPlayCommand"/>, pra dar PAUSE neste Deck.</summary>
    public string UdpTriggerPauseCommand { get; set; } = string.Empty;

    /// <summary>
    /// Verdadeiro = este Deck AVISA por UDP (direção contrária de <see cref="ExternalTriggerEnabled"/>,
    /// que RECEBE) uma mesa/console externo quando dá Play e quando termina — Sprint Comando Externo
    /// Deck→Mesa (2026-08-13). Um toggle só liga os dois disparos juntos (Play e Fim), decisão
    /// explícita do usuário — mesmo padrão de "um toggle liga o canal inteiro" já usado em todo
    /// lugar (<see cref="ExternalTriggerEnabled"/>, <see cref="CartridgeSettingsSnapshot.VinhetasExternalTriggerEnabled"/>).
    /// </summary>
    public bool OutboundUdpEnabled { get; set; }

    /// <summary>
    /// IP:porta de destino do aviso — INDEPENDENTE por Deck (decisão explícita do usuário: "IP:porta
    /// por Deck", já que a mesa que ele está desenvolvendo pode crescer e cada Deck pode precisar
    /// apontar pra um lugar diferente), diferente da porta única de ENTRADA
    /// (<see cref="AppSettingsSnapshot.UdpTriggerPort"/>), que é compartilhada por tudo. Vazio =
    /// nenhum destino configurado ainda, canal fica sem efeito mesmo com o toggle ligado.
    /// </summary>
    public string OutboundUdpHost { get; set; } = string.Empty;

    /// <summary>Porta do destino acima — ver <see cref="OutboundUdpHost"/>. Clamp defensivo (1–65535) aplicado na ViewModel, mesmo padrão já usado pelas outras portas UDP deste programa.</summary>
    public int OutboundUdpPort { get; set; } = 9000;

    /// <summary>Texto mandado pro destino acima quando este Deck dá Play. Vazio = nenhum comando configurado ainda, aquele disparo específico fica sem efeito mesmo com o toggle ligado.</summary>
    public string OutboundUdpOnPlayCommand { get; set; } = string.Empty;

    /// <summary>Texto mandado pro destino acima quando este Deck termina — fim natural (sem loop) OU Stop manual, os dois convergem aqui (decisão explícita do usuário: mesmo critério de "terminar" já usado no resto do programa pra retomar a Cartucheira). Vazio = nenhum comando configurado ainda.</summary>
    public string OutboundUdpOnEndCommand { get; set; } = string.Empty;

    /// <summary>
    /// Sprint Áudio-Unificado (2026-09-15): mesma ideia de <see cref="OutboundUdpOnPlayCommand"/>,
    /// só que pro botão Preview deste Deck — usa o MESMO toggle/destino
    /// (<see cref="OutboundUdpEnabled"/>/<see cref="OutboundUdpHost"/>/<see cref="OutboundUdpPort"/>)
    /// já configurado pro Play/Fim, só o texto muda. Motivo de existir: agora que Play e Preview usam
    /// a mesma placa de áudio (ver <c>Core.Deck.AudioDeviceId</c>), a mesa/console externa que o
    /// usuário está desenvolvendo precisa de um jeito de saber QUAL dos dois está tocando, pra decidir
    /// ela mesma a rota física do áudio (ex.: mandar pro ar ou só pro monitor). Vazio = nenhum aviso
    /// configurado ainda.
    /// </summary>
    public string OutboundUdpOnPreviewCommand { get; set; } = string.Empty;

    /// <summary>Texto mandado pro destino acima quando o Preview deste Deck termina — fim natural (sem loop) OU Stop manual, mesmo critério de "terminar" já usado por <see cref="OutboundUdpOnEndCommand"/>. Vazio = nenhum aviso configurado ainda.</summary>
    public string OutboundUdpOnPreviewEndCommand { get; set; } = string.Empty;
}
