namespace CartucheiraVideo.Configuration;

/// <summary>
/// Qual Deck é a fonte do texto do Teleprompter — Sprint Teleprompter (2026-11-16). Pedido do
/// usuário, depois de pesquisa sobre como teleprompters de voz funcionam de verdade (nenhum produto
/// comercial troca de fonte sozinho no meio da leitura — ver comentário completo em
/// <c>Presentation.ViewModels.TeleprompterViewModel</c>): decisão confirmada
/// com o usuário foi "sempre um Deck fixo" em vez de seguir automaticamente qual Deck está no ar —
/// evita o teleprompter trocar de texto sem querer se os dois Decks tiverem Laudas carregadas ao
/// mesmo tempo (bancada com dois apresentadores, ou um VT no Deck 2 enquanto o apresentador fala no
/// Deck 1). O operador escolhe qual Deck é "o apresentador" aqui, e pode trocar quando a produção
/// mudar.
/// </summary>
public enum TeleprompterSourceDeck
{
    DeckA,
    DeckB
}

/// <summary>
/// Configuração do módulo Teleprompter — Sprint Teleprompter (2026-11-16, pedido do usuário depois
/// de ver o campo TP (teleprompter) de cada Lauda, já existente desde a Sprint 2 do Playlist-
/// Dashboard: "vamos criar uma função para que tenha no menu de configuração a saída de tela de
/// teleprompter [...] cada lauda tem seu teleprompter [...] quando ela estiver carregada no deck, no
/// teleprompter já aparece aquele texto").
///
/// Três responsabilidades, confirmadas com o usuário antes de qualquer código (mesma disciplina de
/// sempre pra pedidos com várias decisões de UX/produto juntas — Convenção #10/#11):
/// 1. <see cref="ScreenOutputDeviceName"/>/<see cref="MirrorMode"/> — pra onde manda o vídeo do
///    teleprompter e se inverte horizontalmente (só faz sentido com vidro/espelho de 45° entre a
///    tela e o apresentador — ver pesquisa citada acima).
/// 2. <see cref="AudioInputDeviceId"/>/<see cref="VoiceTrackingEnabled"/> — de onde vem o áudio do
///    locutor e se o acompanhamento automático por voz está ligado (decisão confirmada: sempre com
///    controle manual disponível por cima, nunca só-voz — ver
///    <c>Presentation.ViewModels.TeleprompterViewModel</c>).
/// 3. <see cref="SourceDeckSetting"/> — qual Deck é "o apresentador" (ver <see cref="TeleprompterSourceDeck"/>).
///
/// Mesmo padrão simples de <c>NdiNetworkSettingsViewModel"</c>/<c>BackgroundArtSettingsViewModel</c>
/// — só o snapshot, sem lista compartilhada com outra seção da tela de Configurações.
/// </summary>
public sealed class TeleprompterSettingsSnapshot
{
    /// <summary>
    /// Identificador estável do monitor Windows (<c>System.Windows.Forms.Screen.DeviceName</c>, ex.:
    /// <c>"\\.\DISPLAY3"</c>) usado como saída do Teleprompter — mesmo formato/fonte de
    /// <see cref="DeckSettingsSnapshot"/>/<c>Core.Deck.ScreenOutputDeviceNames"</c>, mas aqui é
    /// SELEÇÃO ÚNICA (só uma janela de Teleprompter existe, diferente das Telas de Deck, que
    /// aceitam vários monitores ao mesmo tempo). Nulo = nenhuma tela escolhida ainda — o
    /// Teleprompter fica sem nenhuma janela aberta.
    /// </summary>
    public string? ScreenOutputDeviceName { get; set; }

    /// <summary>
    /// "Modo invertido" — espelha o texto horizontalmente, pra quando a tela do Teleprompter fica
    /// atrás de um vidro/espelho de 45° (beam splitter) na frente da câmera: o espelho inverte a
    /// imagem de novo, as duas inversões se cancelam e o texto volta a ficar legível pro
    /// apresentador. Falso (padrão) = leitura direta da tela, sem vidro no meio.
    /// </summary>
    public bool MirrorMode { get; set; }

    /// <summary>
    /// Dispositivo de entrada de áudio (microfone, WASAPI/mmdevice — mesma enumeração de
    /// <c>CartucheiraVideo.Engine.AudioEngine.EnumerateInputDevices</c>, Id completo do Windows)
    /// usado pro acompanhamento automático por voz. Nulo = microfone padrão do sistema.
    /// </summary>
    public string? AudioInputDeviceId { get; set; }

    /// <summary>
    /// Verdadeiro (padrão) = o Teleprompter tenta acompanhar a voz do locutor automaticamente
    /// (reconhecimento de voz local, ver <c>CartucheiraVideo.Engine.TeleprompterVoiceEngine</c>) pra
    /// rolar o texto sozinho. Falso = só rolagem manual (seta/roda do mouse), sem nenhum microfone sendo escutado
    /// — útil se o reconhecimento de voz não estiver disponível nesta máquina (modelo Vosk não
    /// instalado) ou o operador preferir rolar na mão. O controle manual continua disponível nos
    /// dois casos (decisão confirmada com o usuário: nunca só-voz, sem nenhuma rede de segurança).
    /// </summary>
    public bool VoiceTrackingEnabled { get; set; } = true;

    /// <summary>Qual Deck é "o apresentador" — ver <see cref="TeleprompterSourceDeck"/>. Padrão Deck A.</summary>
    public TeleprompterSourceDeck SourceDeckSetting { get; set; } = TeleprompterSourceDeck.DeckA;

    /// <summary>Tamanho da fonte (pontos) do texto na janela de saída — ajustável porque cada apresentador lê a uma distância diferente da tela.</summary>
    public double FontSizePt { get; set; } = 54;
}
