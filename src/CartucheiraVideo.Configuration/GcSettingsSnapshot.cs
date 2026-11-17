namespace CartucheiraVideo.Configuration;

/// <summary>
/// Posição/tamanho aproximados de UM campo do GC dentro do "mockup configurável" do monitoramento
/// remoto — Sprint GC (2026-11-17). Pesquisei (ver comentário de classe de <c>GcSettingsSnapshot</c>)
/// se o vMix oferecia uma imagem/preview ao vivo do próprio Título pela rede, pra espelhar de
/// verdade (igual o espelho de texto do Teleprompter, que reaproveita o estado real da janela): NÃO
/// oferece — a única função relacionada (<c>SnapshotInput</c>) grava um arquivo na máquina do vMix,
/// não manda nada pela API HTTP. Perguntei ao usuário como seguir e ele confirmou "Mockup
/// configurável": o operador desenha aqui, uma vez, em que posição aproximada (num retângulo de
/// referência 1920x1080, mesma lógica de referência já usada pelo espelho de fonte do Teleprompter
/// — ver <c>TeleprompterSettingsSnapshot.FontSizePt</c>/app.js <c>TP_MIRROR_REFERENCE_WIDTH_PX</c>)
/// cada campo nomeado do Título real do vMix aparece na tela — a dashboard então desenha um
/// retângulo com o VALOR atual de cada campo nessa posição, ao vivo, em vez de mostrar uma imagem
/// de verdade (que nunca existiu desta vez, diferente do Teleprompter).
/// </summary>
public sealed class GcFieldLayout
{
    /// <summary>Nome do campo — precisa casar com um <c>SelectedName</c> real do Título configurado no vMix (mesmo nome usado em <c>Core.PlaylistLauda.GcFields</c>/<c>PlaylistProgramRecord.DefaultGcFields</c>).</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Posição X (pixels) num retângulo de referência de 1920 de largura.</summary>
    public double X { get; set; }

    /// <summary>Posição Y (pixels) num retângulo de referência de 1080 de altura.</summary>
    public double Y { get; set; }

    public double Width { get; set; } = 500;

    public double Height { get; set; } = 70;

    public double FontSizePt { get; set; } = 28;
}

/// <summary>
/// Configuração do módulo GC (Gerador de Caracteres) — Sprint GC (2026-11-17, pedido do usuário
/// depois de testar o Controle-Remoto-TP: "precisamos criar o modulo GC, o sistema cartucheira vai
/// enviar para o VMIX O GC automaticamente, assim que o tp ou vt for ativado [...] temos que ter um
/// controle parecido ao tp para ser controlado qd necessario [...] tenho o monitoramento do que
/// esta no ar, parecido com o tp").
///
/// Decisões confirmadas com o usuário antes de qualquer código (mesma disciplina de sempre —
/// Convenção #10/#12/#13):
/// 1. Laudas COM VT: o GC dispara ao DAR PLAY do VT (mesmo instante do corte pro vMix, ver
///    <c>DeckViewModel.TriggerVMixOnPlay</c>) e volta pro GC padrão ao Stop/fim natural (mesmo
///    critério de "terminou" de <c>DeckViewModel.TriggerVMixOnEnd</c>) — ver
///    <c>Presentation.ViewModels.GcViewModel.NotifyDeckPlay</c>/<c>NotifyDeckEnded</c>.
/// 2. Laudas SEM VT (Entrevista/Nota/Passagem): o GC dispara quando o TELEPROMPTER mostra o texto
///    dela — por isso <see cref="SourceDeckSetting"/> existe aqui TAMBÉM (separado do
///    <c>TeleprompterSettingsSnapshot.SourceDeckSetting</c>, mas pensado pra apontar pro mesmo Deck
///    na prática) — ver <c>GcViewModel.NotifyDeckLoaded</c>.
/// 3. Um único Título de vMix pro sistema todo (não por Programa) — só o CONTEÚDO dos campos varia
///    por Lauda/Programa (<c>Core.PlaylistLauda.GcFields</c>/<see cref="PlaylistProgramRecord"/>
///    <c>.DefaultGcFields</c>), nunca o Título em si — por isso os campos de conexão vMix abaixo são
///    só um conjunto, DELIBERADAMENTE separado da aba vMix de cada Deck (que aponta pro Input de
///    VÍDEO daquele Deck, um conceito diferente).
/// 4. Monitoramento remoto = mockup configurável (<see cref="FieldLayout"/>), não uma imagem real do
///    vMix (tecnicamente impossível pela API HTTP, ver comentário de <see cref="GcFieldLayout"/>).
/// </summary>
public sealed class GcSettingsSnapshot
{
    /// <summary>Liga/desliga o módulo GC inteiro — falso (padrão) não manda nenhum comando pro vMix, mesmo com Laudas preenchendo GcFields (precisa ser ligado explicitamente em Configurações → GC, depois de preencher Host/Título).</summary>
    public bool Enabled { get; set; }

    /// <summary>IP/hostname do vMix que recebe o GC — mesmo formato de <c>DeckSettingsViewModel.VMixHost</c>, mas um campo PRÓPRIO (ver decisão #3 no comentário de classe): o GC pode estar no mesmo vMix de um dos Decks ou não, a Cartucheira nunca assume.</summary>
    public string? VMixHost { get; set; }

    /// <summary>Porta da API HTTP do vMix — mesmo padrão/fallback 8088 de <c>DeckSettingsViewModel</c>/<c>VMixClient</c>.</summary>
    public string VMixPort { get; set; } = "8088";

    /// <summary>Número ou nome (cadastrado no vMix) do Input de Título usado pro GC.</summary>
    public string? TitleInput { get; set; }

    /// <summary>Canal de Overlay do vMix (1 a 8, ver <c>VMixClient.SetOverlayVisibleAsync</c>) onde o Título do GC entra/sai — padrão 1 (o mais comum pra lower third).</summary>
    public int OverlayChannel { get; set; } = 1;

    /// <summary>Qual Deck é "o apresentador" pro disparo automático de GC de Laudas SEM VT — ver decisão #2 no comentário de classe. Mesmo enum de <c>TeleprompterSettingsSnapshot.SourceDeckSetting</c>, mas um valor PRÓPRIO (o operador escolhe o mesmo Deck nos dois lugares na prática, mas são configurações independentes).</summary>
    public TeleprompterSourceDeck SourceDeckSetting { get; set; } = TeleprompterSourceDeck.DeckA;

    /// <summary>Layout do mockup configurável do monitoramento remoto — ver <see cref="GcFieldLayout"/>. Vazio = monitoramento cai pra uma lista simples nome/valor (sem posição nenhuma desenhada).</summary>
    public List<GcFieldLayout> FieldLayout { get; set; } = new();
}
