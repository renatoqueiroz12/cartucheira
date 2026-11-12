using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using LibVLCSharp.Shared;

namespace CartucheiraVideo.Engine;

/// <summary>
/// Motor de reproducao de um unico Deck, sobre o LibVLC. Cada Deck (Presentation) tem a sua
/// propria instancia — cada instancia cria seu proprio <see cref="MediaPlayer"/>, mas todas
/// compartilham a mesma instancia de <see cref="LibVLC"/> (custosa de criar, deve existir uma so
/// por processo — quem cria e injeta essa instancia e o composition root da app, <c>App.xaml.cs</c>
/// na Presentation).
///
/// Sem qualquer referencia a WPF/UI: <see cref="MediaPlayer"/> e exposto para quem quiser hospedar
/// a saida de video (ex.: LibVLCSharp.WPF.VideoView na Presentation), mas este projeto nao sabe
/// nem precisa saber como isso e feito.
///
/// Desde a Sprint NDI (2026-08-09), cada instancia tambem cria um SEGUNDO <see cref="MediaPlayer"/>
/// interno (headless, sem VideoView, "player de captura") dedicado a produzir quadros brutos pra
/// <see cref="FrameReady"/> — decisao deliberada de manter DOIS players (um pro VideoView existente,
/// ja validado; outro so pra captura) em vez de reaproveitar o player unico com callbacks de video:
/// trocar o player validado pra modo de callback (em vez de renderizacao por HWND) removeria ele do
/// caminho que o VideoView depende pra desenhar, arriscando regressao na preview que acabou de ser
/// validada. Custo aceito conscientemente: decodificacao em dobro (CPU/memoria) — justificado pela
/// ordem de prioridade do projeto (Estabilidade > Simplicidade > Baixo consumo de memoria).
///
/// Sprint Deck-Aspect-Ratio (2026-08-11): o VideoView da tela (HWND normal) sempre preservou a
/// proporcao do video sozinho — e o player de CAPTURA, alimentado por callback de memoria bruta
/// (SetVideoFormat/SetVideoCallbacks), que nao tinha esse comportamento: pedia sempre o canvas
/// inteiro (1920x1080) e o LibVLC esticava qualquer video fora de 16:9 pra preencher esse retangulo,
/// sem preservar proporcao (o caminho de callback nao faz letterbox sozinho, diferente da saida de
/// janela normal). Corrigido calculando, a cada <see cref="Load"/>, o maior retangulo
/// (<see cref="_frameInnerWidth"/>/<see cref="_frameInnerHeight"/>) que cabe dentro do canvas fixo
/// preservando a proporcao real do arquivo (resolucao/orientacao lidas via
/// <see cref="Media.Parse"/>) — a resolucao de SAIDA continua sempre 1920x1080 (exigencia de quem
/// recebe o NDI), so a imagem escrita dentro dela passa a ser proporcional, centralizada, com as
/// bordas preenchidas de preto opaco (ver <see cref="ConfigureFrameFormat"/>/
/// <see cref="FillFrameBufferOpaqueBlack"/>). Falha honesta em qualquer problema (sem faixa de
/// video, parse expirado) cai pro canvas inteiro — exatamente o comportamento de antes desta sprint,
/// sem regressao.
///
/// Sprint NDI-audio (2026-08-11): o player de captura ganhou tambem <see cref="AudioFrameReady"/> —
/// ate aqui ele so entregava video, o audio do arquivo carregado tocava (sem querer, nunca foi
/// silenciado) pelos alto-falantes padrao da propria maquina, e nunca ia pro NDI, confirmado pelo
/// usuario testando numa segunda maquina (so chegava imagem).
///
/// HISTORICO COMPLETO do mecanismo original (callback de memoria bruta do LibVLC,
/// <c>SetAudioFormat</c>/<c>SetAudioCallbacks</c> num <see cref="MediaPlayer"/> headless dedicado) —
/// mantido aqui pra nao repetir nenhuma tentativa ja descartada numa sessao futura, mesmo depois da
/// migracao pra NAudio (ver abaixo): tres crashes ao vivo em sequencia (AccessViolationException,
/// depois ExecutionEngineException duas vezes seguidas) motivaram uma QUARTA tentativa (player de
/// audio dedicado, separado do <see cref="_framePlayer"/> de video); um QUINTO crash (peak de
/// diagnostico proximo do maximo de um float, evidencia de leitura de memoria invalida) foi root-
/// causado a uma corrida em <c>Load()</c> (trocar/descartar a Media sem garantir que o player tivesse
/// parado primeiro) e corrigido; um SEXTO crash veio de testar remover a opcao <c>:no-video</c> da
/// Media de audio combinado com um destino de video "descartavel" — root-causado a um bug conhecido e
/// nao corrigido do LibVLCSharp nesta versao (GitLab #339, lado video, "vmem cleanup callback
/// regression", corrigido so na 3.6.7); um SETIMO crash aconteceu mesmo depois de isolar o player de
/// audio numa instancia PROPRIA de <c>LibVLC</c> (nao mais compartilhada com preview/captura de
/// video) — o teste seguinte (sustentado, pedido explicitamente pelo usuario antes de aprovar
/// qualquer reescrita maior) confirmou que o crash tinha sumido com essa separacao, mas o audio
/// entregue continuava zerado/corrompido de qualquer forma, evidencia de que o problema estava no
/// proprio MECANISMO de callback do LibVLC (nao em como este projeto o usava).
///
/// Sprint NDI-audio-NAudio (2026-08-11, mesmo dia, aprovada pelo usuario apos ver o resultado do
/// teste acima): TODO o mecanismo de callback do LibVLC pro audio foi removido desta classe — o
/// <c>_audioCapturePlayer</c>/<c>_audioLibVlc</c> headless e o par <c>SetAudioFormat</c>/
/// <c>SetAudioCallbacks</c> deixaram de existir. No lugar, <see cref="_audioCapture"/>
/// (<see cref="NdiAudioCapture"/>, decodificador via NAudio, mesmos padroes ja validados por
/// <see cref="AudioEngine"/>) decodifica o arquivo de forma independente, sincronizado pelos MESMOS
/// comandos de transporte que ja existiam (Play/Pause/Stop/Seek/Loop, ver cada metodo abaixo) — ver
/// comentario de classe de <see cref="NdiAudioCapture"/> pro desenho completo do sincronismo A/V
/// (pacing por relogio de parede + correcao periodica contra a posicao real do video). <see cref="_framePlayer"/>
/// (video-only, <c>:no-audio</c>) e <see cref="_mediaPlayer"/> (preview local) continuam 100%
/// LibVLCSharp, intocados — so o caminho de audio-pro-NDI mudou de mecanismo.
///
/// Sprint NDI-audio-video-clock-lock (2026-09-12): a correcao periodica citada acima passou a se
/// referenciar contra a posicao REAL de <see cref="_framePlayer"/> (o decodificador que gera o video
/// que vai pro NDI) — antes vinha de <see cref="OnTimeChanged"/> (posicao do <see cref="_mediaPlayer"/>,
/// o preview, um decodificador DIFERENTE e mais esparso). Ver comentario de classe de
/// <see cref="NdiAudioCapture"/> pro diagnostico completo.
///
/// Sprint NDI-audio-video-clock-lock-hotfix (2026-09-12, mesmo dia): a sprint acima leu
/// <see cref="_framePlayer"/>.Time de DENTRO de <see cref="OnFrameDisplay"/> (o callback nativo de
/// video do LibVLC) — causou um deadlock real (thread da interface presa em <c>Pause()</c> contra a
/// thread do LibVLC presa nessa leitura), travando a aplicacao inteira ao pausar. Corrigido movendo
/// a leitura pra <see cref="PollFramePlayerPosition"/>, um timer comum rodando fora de qualquer
/// callback do LibVLC — ver comentario dela e o comentario (removido) de <see cref="OnFrameDisplay"/>
/// pro diagnostico completo.
/// </summary>
public sealed class PlaybackEngine : IDisposable
{
    /// <summary>Resolucao/formato fixos do player de captura (decisao da Sprint NDI: MVP sem negociacao dinamica de formato) — UYVY desde a Sprint NDI-video-uyvy (2026-09-12): YCbCr 4:2:2 empacotado, 2 bytes/pixel EM MEDIA (grupo de 4 bytes cobre 2 pixels, ordem U,Y0,V,Y1 — ver NdiInterop/NdiSender), pedido pra fechar o gap de banda de rede com o OBS. Antes disso, BGRX (Sprint NDI-video-bgrx)/BGRA (MVP), 4 bytes/pixel. 1 canal de saida NDI por Deck.</summary>
    private const int FrameWidth = 1920;
    private const int FrameHeight = 1080;

    /// <summary>
    /// Sprint NDI-video-uyvy (2026-09-12): UYVY empacota 2 pixels em 4 bytes (nao 4 bytes POR pixel
    /// como BGRX/BGRA) — a media e 2 bytes/pixel, entao o stride de uma linha inteira e
    /// <c>FrameWidth * 2</c>, nao <c>* 4</c>. <see cref="BytesPerPixel"/> abaixo continua usado so
    /// pro calculo do deslocamento horizontal do letterbox (ver <see cref="ConfigureFrameFormat"/>),
    /// onde "2 bytes por pixel" (a media) e exatamente a conta certa desde que o deslocamento em
    /// PIXELS seja sempre par (ver <see cref="ComputeLetterboxSize"/> — garante innerWidth multiplo
    /// de 4, o que garante o offset em pixels multiplo de 2, nunca cortando um grupo U/Y/V/Y ao
    /// meio).
    /// </summary>
    private const int FrameStrideBytes = FrameWidth * 2;
    private const int BytesPerPixel = 2;

    /// <summary>Tempo maximo pro parse de resolucao/orientacao (Sprint Deck-Aspect-Ratio) — parse local so le cabecalho, nunca deveria demorar isso; limite curto pra nunca travar um Load perceptivelmente, mesmo com um arquivo estranho.</summary>
    private const int AspectRatioParseTimeoutMs = 1500;

    /// <summary>
    /// Sprint NDI-audio-video-clock-lock-hotfix (2026-09-12): intervalo do timer que le
    /// <see cref="_framePlayer"/>.Time FORA de qualquer callback do LibVLC (ver
    /// <see cref="PollFramePlayerPosition"/> pro motivo desta mudanca — corrige um travamento real
    /// introduzido na sprint anterior). Mesmo valor de 50ms que a v1.0.9 ja usava (a cada quadro,
    /// aproximadamente, num video de ~20fps ou mais), preservando a mesma frequencia de correcao —
    /// so muda ONDE a leitura acontece, nunca a frequencia.
    /// </summary>
    private const int VideoPositionPollIntervalMs = 50;

    /// <summary>
    /// Sprint NDI-TryCorrectDrift-Ruido (2026-09-16) — teto de quanto <see cref="PollFramePlayerPosition"/>
    /// pode EXTRAPOLAR a posicao do video pra frente, em ms de tempo de parede, antes de congelar no
    /// ultimo valor real conhecido (ver comentario completo em <see cref="PollFramePlayerPosition"/>).
    /// 2000ms fica bem acima do maior salto real ja medido entre duas leituras de
    /// <see cref="_framePlayer"/>.Time em uso normal (~1300ms, ver diagnostico desta sprint) — nunca
    /// interfere com o funcionamento normal, so existe pra limitar o estrago numa pausa real ou num
    /// travamento genuino do video (onde extrapolar pra sempre seria pior que so congelar).
    /// </summary>
    private const int VideoPositionExtrapolationCapMs = 2000;

    private readonly LibVLC _libVlc;
    private readonly MediaPlayer _mediaPlayer;
    private readonly MediaPlayer _framePlayer;

    /// <summary>
    /// Dois buffers alternados ("ping-pong", Sprint NDI-video-async, 2026-09-11) em vez de um unico
    /// buffer fixo — necessario desde que <see cref="Ndi.NdiSender.SendFrame"/> passou a usar a
    /// variante ASSINCRONA da NDI SDK (ver comentario la e em <c>NdiInterop</c> pro diagnostico
    /// completo do bug corrigido): o buffer que acabou de ser entregue pro NDI pode ainda estar em
    /// transito na rede quando o LibVLC pede o PROXIMO quadro — escrever nele de novo antes da NDI
    /// SDK ter processado o anterior seria uma corrida classica de dado. Alternando entre dois
    /// buffers, o LibVLC sempre escreve no buffer que NAO foi o mais recentemente entregue pro
    /// NdiSender, dando folga de um quadro inteiro pro envio assincrono terminar (e, mesmo que nao
    /// tenha terminado ainda, a propria NDI SDK sincroniza sozinha na chamada seguinte — ver
    /// contrato documentado em NdiInterop). Seguranca desta alternancia depende do contrato de
    /// callback do LibVLC: pra um unico MediaPlayer, <see cref="OnFrameLock"/> do quadro N+1 nunca e
    /// chamado antes de <see cref="OnFrameDisplay"/> do quadro N retornar — entao alternar o indice
    /// dentro de OnFrameDisplay, antes de disparar FrameReady, e seguro sem nenhum lock adicional.
    /// </summary>
    private readonly IntPtr[] _frameBuffers = new IntPtr[2];
    private int _activeFrameBufferIndex;

    /// <summary>
    /// Sprint NDI-Loop-Seamless (2026-09-17): SEGUNDO player de captura de video, gemeo de
    /// <see cref="_framePlayer"/>, usado SO quando <see cref="_seamlessLoopEnabled"/> estiver ligado
    /// (variavel de ambiente <c>CARTUCHEIRA_SEAMLESS_LOOP=1</c>, lida uma unica vez no construtor —
    /// ver comentario completo la). Motivo de existir: eliminar de vez o atraso de ~100-160ms medido
    /// a cada reinicio de loop (ver <c>sprint-2026-09-17-loop-restart-input-repeat-descartado.md</c>
    /// pro raciocinio completo de design e por que a alternativa mais simples — <c>input-repeat</c>
    /// nativo do LibVLC — foi descartada sem escrever codigo).
    ///
    /// IDEIA CENTRAL ("double buffering"): enquanto o slot ATIVO (A=<see cref="_framePlayer"/> ou
    /// B=<see cref="_framePlayerB"/>) esta perto do fim (<see cref="SeamlessLoopLeadTimeMs"/> antes
    /// do fim, ver <see cref="TryArmSeamlessStandby"/>), o OUTRO slot (standby) e recriado do zero e
    /// mandado tocar em PARALELO, do inicio do arquivo — pagando o custo de reabertura ENQUANTO o
    /// slot ativo ainda esta mostrando o final valido da volta anterior, escondendo o atraso em vez
    /// de elimina-lo de verdade (nao tem como eliminar: a v1.0.37 provou que reabrir e necessario
    /// pra este LibVLC conseguir retomar). Quando o slot ativo chega no PROPRIO fim
    /// (<see cref="HandleFrameSlotEndReached"/>), se o standby ja tiver produzido pelo menos um
    /// quadro real (<see cref="_standbyHasFrame"/>), a troca e so uma virada de flag
    /// (<see cref="_seamlessSlotAIsActive"/>) — sem reabrir nada NESSE momento, sem gap. Frames do
    /// slot que nao esta ativo no momento sao decodificados normalmente (o LibVLC precisa disso pra
    /// o standby ficar pronto) mas NUNCA repassados pra <see cref="FrameReady"/> (ver
    /// <see cref="OnFrameDisplay"/>/<see cref="OnFrameDisplayB"/>) — evita mandar dois quadros
    /// concorrentes pro NDI.
    ///
    /// SEGURANCA: cada ponto de incerteza cai pro comportamento EXATO da v1.0.38 (reabrir o mesmo
    /// player no EndReached) em vez de arriscar um estado inconsistente — duracao desconhecida
    /// (<see cref="TryArmSeamlessStandby"/>), standby sem nenhum quadro confirmado ainda
    /// (<see cref="HandleFrameSlotEndReached"/>), ou uma Pause/Seek/Stop manual no meio do
    /// armamento (<see cref="CancelSeamlessStandby"/>, chamada por <see cref="Pause"/>/
    /// <see cref="Seek"/>). <see cref="NdiAudioCapture"/> (o arquivo com o maior historico de
    /// regressoes graves do projeto) NAO e tocado por nenhuma linha desta sprint.
    /// </summary>
    private readonly MediaPlayer _framePlayerB;
    private Media? _currentFrameMediaB;
    private readonly IntPtr[] _frameBuffersB = new IntPtr[2];
    private int _activeFrameBufferIndexB;
    private readonly MediaPlayer.LibVLCVideoLockCb _frameLockCallbackB;
    private readonly MediaPlayer.LibVLCVideoDisplayCb _frameDisplayCallbackB;

    /// <summary>Caminho do ultimo arquivo carregado — guardado so pro modo sem-corte poder recriar a Media do slot em standby quando chegar a vez dele (ver <see cref="ArmStandbySlotA"/>/<see cref="ArmStandbySlotB"/>), sem precisar que o chamador passe o caminho de novo.</summary>
    private string? _currentFilePath;

    /// <summary>
    /// Lida uma UNICA vez, no construtor, da variavel de ambiente <c>CARTUCHEIRA_SEAMLESS_LOOP</c>
    /// ("1" liga, qualquer outro valor ou ausente desliga — o mesmo de sempre, sem regressao).
    /// Deliberadamente SEM setter publico: nunca muda depois de criado o Deck. Escolhido em vez de
    /// uma opcao na tela de proposito, pra esta primeira sprint do mecanismo: liga/desliga sem
    /// reinstalar nada (so mexendo na variavel de ambiente do Windows e reiniciando o programa), sem
    /// arriscar nenhuma mudanca na camada de UI (XAML/bindings) nesta sprint que ja mexe bastante no
    /// motor de reproducao.
    /// </summary>
    private readonly bool _seamlessLoopEnabled;

    /// <summary>
    /// Sprint Áudio-Dispositivo-Diagnóstico-4 (2026-09-19): flag opcional, desligada por padrão (ver
    /// comentário completo no construtor e em <see cref="ReapplyAudioOutputDeviceAsync"/>) — quando
    /// ligada via variável de ambiente, só a primeira tentativa de reaplicar o dispositivo de áudio
    /// acontece, nenhuma das 6 seguintes.
    /// </summary>
    private readonly bool _audioDeviceUmaTentativaEnabled;

    /// <summary>
    /// Sprint Áudio-Dispositivo-Diagnóstico-5 (2026-09-19): flag opcional, desligada por padrão (ver
    /// comentário completo em <see cref="Load"/>) — quando ligada, tenta "semear" o dispositivo de
    /// áudio configurado direto na <see cref="Media"/>, ANTES do Play, via a opção nativa do modulo de
    /// saida de audio do libvlc no Windows (<c>mmdevice-audio-device</c>) — pesquisa externa confirmou
    /// que essa opcao e lida pelo proprio modulo TODA VEZ que a saida de audio abre (nao só uma vez no
    /// arranque do programa), entao aplicá-la à Media antes do Play pode fazer o dispositivo certo já
    /// ser usado desde o primeiro quadro, sem NENHUMA chamada de SetOutputDevice depois — eliminando a
    /// causa raiz (reabrir o fluxo/cortar o áudio) em vez de só encolher a janela onde ela acontece.
    /// Puramente aditivo: o mecanismo de reforço de sempre (7 tentativas) continua rodando do mesmo
    /// jeito por baixo, como rede de segurança, caso a opção não pegue por algum motivo.
    /// </summary>
    private readonly bool _audioDeviceViaOpcaoMediaEnabled;

    /// <summary>
    /// Sprint Áudio-Dispositivo-Diagnóstico-6 (2026-09-19): true quando a ultima chamada de
    /// <see cref="SeedAudioDeviceOptionNaMediaSeAtivo"/> conseguiu de fato acrescentar a opcao na
    /// Media (nao apenas tentar) — junto com <see cref="_audioDeviceSeedDeviceId"/>, usado pelo evento
    /// Playing pra decidir se pula o reforco de 7 tentativas (ver comentario la).
    /// </summary>
    private bool _audioDeviceSeedApplied;

    /// <summary>Dispositivo pro qual a ultima semente (ver <see cref="_audioDeviceSeedApplied"/>) foi aplicada.</summary>
    private string? _audioDeviceSeedDeviceId;

    /// <summary>
    /// Sprint Áudio-Latência-Placa-Local (2026-09-19): flag opcional, desligada por padrão — usuário
    /// mediu (gravação com o audio do NDI num canal e o audio da placa local no outro, comparados no
    /// mesmo arquivo) que a placa local sai com ~300ms de ATRASO em relação ao NDI — o oposto do que
    /// se esperaria (NDI, por passar por rede/encode, "deveria" ser o mais lento). Pesquisa externa na
    /// documentação oficial do libvlc confirma a explicação mais provável: a saída de audio padrão do
    /// Windows (WASAPI em modo COMPARTILHADO, usada pelo <c>_mediaPlayer</c>) tem um buffer de vários
    /// quadros por padrão (tipicamente 100-300ms) antes do audio realmente sair pro hardware — bem
    /// mais que o pipeline dedicado que alimenta o NDI (que usa blocos de ~10ms). Nao e um bug
    /// introduzido por nenhuma sprint anterior, e uma diferenca estrutural de latencia entre os dois
    /// caminhos (documentado no proprio codigo/comentarios como um mesmo tipo de achado ja visto antes
    /// comparando audio/video do NDI, so que ali era ~140ms fixo e resolvido compensando na ferramenta
    /// que recebe). A mesma documentacao aponta uma opcao real da libvlc pra reduzir isso na raiz: modo
    /// EXCLUSIVO do WASAPI (<c>--wasapi-exclusive</c>/<c>:wasapi-exclusive</c>), que contorna o mixer
    /// compartilhado do Windows e fala quase direto com o hardware — latencia bem menor, mas com dois
    /// riscos reais e documentados: (1) pode dar SILENCIO TOTAL se o hardware/driver nao aceitar bem o
    /// formato exigido pelo modo exclusivo; (2) modo exclusivo TRAVA o dispositivo pro uso exclusivo
    /// deste player — se outro Deck (ou outro programa) tentar usar a MESMA placa fisica ao mesmo
    /// tempo, pode falhar. Por isso, desligado por padrao, e só deve ser testado sabendo desse risco.
    /// </summary>
    private readonly bool _audioWasapiExclusiveEnabled;

    /// <summary>
    /// Sprint Áudio-Latência-Placa-Local-Sincronismo (2026-09-19): a v1.0.47 (modo exclusivo do
    /// WASAPI, <see cref="_audioWasapiExclusiveEnabled"/>) tentou atacar o atraso de ~300ms medido
    /// pelo usuário entre o áudio do NDI e o áudio da placa física TENTANDO ACELERAR a placa —
    /// resultado real, confirmado pelo usuário: PIOROU (atraso maior, não menor). Hipótese
    /// abandonada por teste real, não por suposição.
    ///
    /// Esta e uma correção estrutural diferente: em vez de mexer em COMO a placa toca (arriscado,
    /// já provado ruim), atrasa QUANDO o caminho do NDI (<see cref="_framePlayer"/> — vídeo — e
    /// <see cref="_audioCapture"/> — áudio, sempre OS DOIS JUNTOS, preservando a sincronia interna
    /// já existente entre eles) começa a tocar, pra compensar o atraso natural (buffer do modo
    /// compartilhado do Windows) que já existe no caminho da placa local
    /// (<see cref="_mediaPlayer"/>) ANTES do primeiro som de fato sair pelo hardware. Como
    /// <see cref="Play"/> hoje chama <c>_mediaPlayer.Play()</c> e o par NDI praticamente ao mesmo
    /// tempo, e a placa demora ~300ms a mais pra ficar audível depois do comando, o NDI sai "na
    /// frente" — segurando o par NDI por esse mesmo tanto de tempo antes de começar, os dois passam
    /// a ficar audíveis quase juntos.
    ///
    /// Valor em milissegundos lido de <c>CARTUCHEIRA_AUDIO_SYNC_ATRASO_NDI_MS</c> (inteiro, ex.:
    /// "300"). Zero/não definida/inválida = desligado, comportamento idêntico a antes desta sprint
    /// (par NDI começa imediatamente, exatamente como sempre foi). Deliberadamente SEM valor padrão
    /// diferente de zero: o número certo depende da placa/driver de cada máquina, e o usuário já
    /// mediu ~300ms pra placa dele — mas prefere não perder tempo remedindo toda vez, entao o
    /// LEIA-ME desta entrega já traz 300 pronto pra colar.
    ///
    /// Risco desta abordagem, para ser honesto: BEM mais baixo que as tentativas anteriores (v1.0.44
    /// a v1.0.47) porque não muda NADA em como qualquer um dos dois players decodifica ou envia
    /// áudio/vídeo — só atrasa o instante de largada de um dos dois, com um <see cref="Task.Delay"/>
    /// simples. Efeito colateral aceito conscientemente: o feed NDI (vídeo E áudio) passa a começar
    /// visivelmente mais tarde depois do Play (o valor configurado), em vez de imediatamente — troca
    /// deliberada de latência absoluta por sincronismo entre os dois caminhos, que foi exatamente o
    /// que o usuário pediu.
    /// </summary>
    private readonly int _ndiSyncDelayMs;

    /// <summary>
    /// Contador incrementado a cada <see cref="Play"/> e <see cref="Stop"/> — usado só pelo atraso de
    /// <see cref="_ndiSyncDelayMs"/> pra saber, quando o <see cref="Task.Delay"/> terminar, se ainda
    /// faz sentido iniciar o par NDI (mesmo Play que pediu o atraso ainda é o "atual") ou se um Stop
    /// ou um Play novo já aconteceu nesse meio tempo (nesse caso, não faz nada — o Play/Stop mais
    /// recente já cuidou do que precisava). Mesma ideia de sentinela já usada em
    /// <see cref="_pendingAudioDeviceId"/>/<see cref="ReapplyAudioOutputDeviceAsync"/>, adaptada pra
    /// um inteiro simples porque aqui não há um "valor pedido" pra comparar, só uma sequência.
    /// </summary>
    private int _playSequence;

    /// <summary>true = <see cref="_framePlayer"/> (A) e quem entrega quadros pra <see cref="FrameReady"/> agora; false = e <see cref="_framePlayerB"/> (B). So importa enquanto <see cref="_seamlessLoopEnabled"/> estiver ligado — com o modo desligado, e sempre tratado como A (ver <see cref="ActiveFramePlayer"/>/<see cref="IdleFramePlayer"/>).</summary>
    private bool _seamlessSlotAIsActive = true;

    /// <summary>Evita re-armar o standby mais de uma vez por volta (o timer de posicao roda a cada 50ms; a janela de "perto do fim" dura varios desses ticks). Resetado a cada troca de slot e a cada Play/Stop/Load.</summary>
    private bool _seamlessStandbyArmedThisCycle;

    /// <summary>true assim que o slot em standby (o que nao esta ativo agora) confirmar pelo menos UM quadro real decodificado (ver <see cref="OnFrameDisplay"/>/<see cref="OnFrameDisplayB"/>) — condicao pra <see cref="HandleFrameSlotEndReached"/> confiar na troca sem reabrir nada.</summary>
    private volatile bool _standbyHasFrame;

    /// <summary>Quanto antes do fim do slot ativo o standby comeca a ser preparado (ver <see cref="TryArmSeamlessStandby"/>) — folgado o bastante acima dos ~100-160ms de atraso ja medidos pra reabertura (Sprint NDI-Loop-Restart-Diagnostico), sem ser tao cedo a ponto de sobrepor voltas de loop muito curtas.</summary>
    private const int SeamlessLoopLeadTimeMs = 500;

    /// <summary>
    /// Sprint NDI-audio-NAudio (2026-08-11) — decodificador de audio dedicado, via NAudio, que
    /// substituiu o mecanismo de callback do LibVLC (<c>_audioCapturePlayer</c>/<c>_audioLibVlc</c>,
    /// removidos desta classe — ver comentario de classe pro historico completo/motivo). Repassa
    /// <see cref="NdiAudioCapture.AudioFrameReady"/> direto pra <see cref="AudioFrameReady"/> desta
    /// classe (ver construtor) — do ponto de vista de quem consome o evento (DeckViewModel/NdiSender),
    /// nada mudou na API publica, so o mecanismo por baixo.
    /// </summary>
    private readonly NdiAudioCapture _audioCapture = new();

    // Guardados em campos readonly de proposito: se passados so como argumento inline pro
    // SetVideoCallbacks, nada impede o GC de coletar o delegate depois — o LibVLC (nativo) continua
    // chamando o ponteiro de funcao mesmo assim, e isso e um crash silencioso, sem aviso em tempo de
    // compilacao (mesma classe de bug documentada na pesquisa da Sprint NDI-audio).
    private readonly MediaPlayer.LibVLCVideoLockCb _frameLockCallback;
    private readonly MediaPlayer.LibVLCVideoDisplayCb _frameDisplayCallback;

    /// <summary>
    /// Sprint NDI-audio-video-clock-lock-hotfix (2026-09-12): ver <see cref="PollFramePlayerPosition"/>.
    /// Roda na thread do ThreadPool (comportamento padrao do <see cref="System.Threading.Timer"/>),
    /// nunca na thread do LibVLC — e exatamente essa a diferenca que corrige o travamento.
    /// </summary>
    private readonly Timer _videoPositionPollTimer;

    /// <summary>
    /// Sprint NDI-TryCorrectDrift-Ruido (2026-09-16) — ultimo valor BRUTO (nao suavizado) de
    /// <see cref="_framePlayer"/>.Time observado por <see cref="PollFramePlayerPosition"/>, e o
    /// relogio de parede que mede ha quanto tempo esse valor foi observado pela primeira vez. Ver
    /// comentario completo em <see cref="PollFramePlayerPosition"/> pro racional — resumo: o LibVLC
    /// atualiza essa posicao em LOTES (confirmado por log real desta sprint: desvio padrao de 168ms
    /// entre leituras devia ser ~0, o mesmo numero ja documentado desde a Sprint
    /// NDI-audio-video-clock-lock-diag-2 em 2026-09-13), entao alimentar
    /// <see cref="NdiAudioCapture.SyncToVideoPosition"/> com o valor bruto direto faz
    /// <see cref="NdiAudioCapture.TryCorrectDrift"/> reagir ao RUIDO da leitura, nao a deriva real —
    /// confirmado no log de producao do usuario (ate 41% das checagens de uma sessao resultando numa
    /// correcao real, a maioria delas sem nenhuma deriva de verdade por tras). So acessados dentro de
    /// <see cref="PollFramePlayerPosition"/> (sempre o mesmo <see cref="System.Threading.Timer"/>, uma
    /// unica execucao por vez na pratica dado o custo trivial do callback) — mesmo raciocinio de
    /// ausencia de lock ja usado pros outros campos desta classe preenchidos por um unico
    /// timer/callback.
    /// </summary>
    private long _lastRawFramePlayerTimeMs = -1;

    private readonly Stopwatch _framePlayerPositionAnchorClock = new();

    private Media? _currentMedia;
    private Media? _currentFrameMedia;
    private bool _loopEnabled;
    private bool _disposed;

    /// <summary>
    /// Sprint Áudio-Dispositivo-Fix-2 (2026-09-13): ver <see cref="SetAudioOutputDevice"/> e o
    /// handler de <see cref="MediaPlayer.Playing"/> no construtor pro mecanismo completo. Guarda o
    /// ultimo dispositivo pedido pra poder reaplicar toda vez que a reproducao realmente comeca —
    /// inclusive depois de um Stop seguido de Play, caso confirmado pelo usuario em que o PRIMEIRO
    /// Play respeitava a placa configurada mas um Play seguinte (depois de Stop) voltava pra placa
    /// padrao do Windows.
    /// </summary>
    private string? _pendingAudioDeviceId;

    /// <summary>Tamanho, dentro do canvas fixo 1920x1080, que preserva a proporcao real do arquivo carregado — recalculado a cada Load (ver <see cref="ConfigureFrameFormat"/>). Comeca no canvas inteiro (16:9), mesmo valor de antes desta sprint, ate o primeiro Load calcular o real.</summary>
    private int _frameInnerWidth = FrameWidth;
    private int _frameInnerHeight = FrameHeight;
    private int _frameOffsetXBytes;
    private int _frameOffsetYBytes;

    /// <summary>
    /// Sprint NDI-Frame-Rate-Real (2026-09-16): frame rate REAL do arquivo carregado, lido do mesmo
    /// parse/faixa de video ja usado por <see cref="ComputeLetterboxSize"/> pra resolucao/orientacao
    /// (ver <see cref="VideoFrameRate"/> e o diagnostico completo em
    /// <c>sprint-2026-09-16-ndi-sync-diag-vmix-resultado.md</c>). Antes desta sprint,
    /// <see cref="Ndi.NdiSender.SendFrame"/> declarava 30000/1000 (30,000fps exatos) pra QUALQUER
    /// arquivo, sempre — um metadado NDI errado pra qualquer coisa que nao seja exatamente 30fps
    /// (a maioria do conteudo de transmissao real e 29,97/59,94fps, e o teste de sincronismo do
    /// usuario usava um arquivo de 60fps de verdade). Comeca em 30000/1000 (o mesmo valor fixo de
    /// antes) — so muda se o parse conseguir ler um frame_rate_num/frame_rate_den validos (>0) da
    /// faixa de video; se nao conseguir (arquivo estranho, timeout, sem faixa de video), cai pro
    /// mesmo valor fixo de sempre, sem regressao nesse caso.
    /// </summary>
    private int _videoFrameRateNum = 30000;
    private int _videoFrameRateDen = 1000;

    public PlaybackEngine(LibVLC libVlc)
    {
        _libVlc = libVlc ?? throw new ArgumentNullException(nameof(libVlc));
        _mediaPlayer = new MediaPlayer(_libVlc);

        // Sprint Áudio-Dispositivo-Diagnóstico-2 (2026-09-19): log "canário", incondicional, disparado
        // toda vez que um PlaybackEngine e construido (um por Deck, no arranque do programa). Motivo:
        // o primeiro teste do log de SetAudioOutputDevice (v1.0.41) nao gerou NENHUM arquivo, e nao deu
        // pra confirmar com o usuario se foi porque o build novo nao chegou a rodar de verdade, ou
        // porque o dispositivo pedido chegou vazio/nulo (nenhum dos dois caminhos de log da v1.0.41
        // roda nesse caso). Esta linha sozinha resolve a duvida "o build novo esta rodando?": se ela
        // aparecer no log assim que o programa abre (antes de qualquer Play), confirma que o codigo
        // novo esta ativo — se nem isso aparecer, o problema e o build/deploy, nao o mecanismo de log.
        LogAudioDeviceDiagnostic("ENGINE-INICIALIZADO (PlaybackEngine construido)");

        _mediaPlayer.TimeChanged += OnTimeChanged;
        _mediaPlayer.EndReached += OnEndReached;
        _mediaPlayer.EncounteredError += OnEncounteredError;
        _mediaPlayer.Playing += (_, _) =>
        {
            StateChanged?.Invoke(this, new EngineStateChangedEventArgs(EnginePlaybackState.Playing));

            // Sprint Áudio-Dispositivo-Fix-2 (2026-09-13): reaplica o dispositivo de audio pedido
            // TODA VEZ que o LibVLC confirma que a reproducao realmente comecou — ver comentario de
            // _pendingAudioDeviceId e de SetAudioOutputDevice pro diagnostico completo. Cobre tanto o
            // primeiro Play quanto qualquer Play seguinte depois de um Stop (o caso que a v1.0.14,
            // que so invertia a ordem das chamadas, nao cobria).
            //
            // Sprint Áudio-Dispositivo-Fix-3 (2026-09-13, mesmo dia): a v1.0.15 chamava
            // SetOutputDevice AQUI DENTRO de forma sincrona — usuario reportou trava total da
            // aplicacao logo apos o Play ("nao da comando"). Causa: Playing e disparado numa thread
            // INTERNA do LibVLC (mesma classe de problema ja documentada e corrigida em
            // OnFrameDisplay/OnEndReached desta classe — ver comentarios la) — chamar de volta na API
            // do LibVLC (SetOutputDevice, que por baixo chama libvlc_audio_output_device_set)
            // SINCRONAMENTE de dentro desse callback disputa o mesmo lock interno do player contra
            // qualquer outra thread (ex.: a UI, tentando Pause/Stop) que esteja mexendo no player ao
            // mesmo tempo — deadlock. Corrigido despachando pra o ThreadPool, exatamente como
            // OnEndReached ja faz pelo mesmo motivo.
            //
            // Sprint Áudio-Dispositivo-Fix-4 (2026-09-13, mesmo dia): tirar a trava (Fix-3) resolveu
            // o travamento, mas o usuario reportou que a placa configurada voltou a nao ser respeitada
            // (caiu de novo na padrao do Windows). Pesquisa externa (issue #1119 do vlcj — outro
            // binding .NET/Java sobre o MESMO libvlc nativo, github.com/caprica/vlcj/issues/1119)
            // confirma um comportamento documentado por outros desenvolvedores enfrentando o MESMO
            // problema: no Windows, o subsistema de audio do libvlc pode levar ATE ALGUNS SEGUNDOS
            // pra "assentar" depois que a reproducao comeca — um SetOutputDevice chamado antes disso
            // (mesmo que so por poucos milissegundos, como o Task.Run da Fix-3 fazia) e silenciosamente
            // ignorado, e o audio fica preso no dispositivo padrao ate essa janela passar. Corrigido
            // reaplicando o dispositivo VARIAS VEZES ao longo de ~5 segundos (ver
            // ReapplyAudioOutputDeviceAsync), sempre fora da thread do LibVLC — cobre tanto um caso
            // rapido (se acontecer) quanto a janela lenta documentada externamente. Chamar
            // SetOutputDevice repetidas vezes e seguro (documentacao oficial da libvlc), entao o custo
            // desta redundancia e desprezivel.
            var pendingDeviceId = _pendingAudioDeviceId;
            if (pendingDeviceId is not null)
            {
                // Sprint Áudio-Dispositivo-Fix-5 (2026-09-18, log adicionado 2026-09-19): registra o
                // instante exato do evento Playing — ver comentario completo de LogAudioDeviceDiagnostic.
                LogAudioDeviceDiagnostic($"PLAYING device={pendingDeviceId}");

                // Sprint Áudio-Dispositivo-Diagnóstico-6 (2026-09-19): a v1.0.45 semeava a opcao na
                // Media mas MANTINHA o reforco de 7 tentativas rodando por baixo de qualquer jeito —
                // usuario confirmou "exatamente o mesmo comportamento", o que faz sentido AGORA que
                // percebo o problema: mesmo se a opcao tivesse funcionado perfeitamente, as 7 chamadas
                // de SetOutputDevice que vem DEPOIS continuariam reabrindo o fluxo e cortando o audio
                // do mesmo jeito — a semente nunca tinha chance real de mostrar diferenca nenhuma. Ver
                // comentario completo em ReapplyAudioOutputDeviceAsync sobre o teste corrigido aqui:
                // só pula o reforco quando a MESMA sessao de Play atual confirmou ter semeado a opcao
                // pro MESMO deviceId pendente agora — qualquer duvida (nada semeado, ou semeado pra um
                // device diferente do pedido agora) cai no reforco de sempre, sem risco extra.
                if (_audioDeviceViaOpcaoMediaEnabled
                    && _audioDeviceSeedApplied
                    && string.Equals(_audioDeviceSeedDeviceId, pendingDeviceId, StringComparison.Ordinal))
                {
                    LogAudioDeviceDiagnostic($"REAPPLY-PULADO-OPCAO-MEDIA-JA-SEMEOU device={pendingDeviceId}");
                }
                else
                {
                    _ = ReapplyAudioOutputDeviceAsync(pendingDeviceId);
                }
            }
        };
        _mediaPlayer.Paused += (_, _) => StateChanged?.Invoke(this, new EngineStateChangedEventArgs(EnginePlaybackState.Paused));
        _mediaPlayer.Stopped += (_, _) => StateChanged?.Invoke(this, new EngineStateChangedEventArgs(EnginePlaybackState.Stopped));

        // Player de captura (headless): mesma LibVLC compartilhada, midia propria (ver Load — NAO
        // reusa o mesmo objeto Media do player principal; dois Media separados abrindo o mesmo
        // arquivo e um padrao conhecido/seguro do LibVLC, ao contrario de um Media so preso a dois
        // MediaPlayer ao mesmo tempo, que nao e documentado como seguro em lugar nenhum encontrado
        // na pesquisa desta sprint — preferido o caminho conservador).
        _framePlayer = new MediaPlayer(_libVlc);
        _frameBuffers[0] = Marshal.AllocHGlobal(FrameStrideBytes * FrameHeight);
        _frameBuffers[1] = Marshal.AllocHGlobal(FrameStrideBytes * FrameHeight);
        FillFrameBufferOpaqueBlack(_frameBuffers);
        _frameLockCallback = OnFrameLock;
        _frameDisplayCallback = OnFrameDisplay;
        // Formato inicial = canvas inteiro (16:9), mesmo valor de sempre — Load() reconfigura pro
        // tamanho real do arquivo assim que houver um carregado (ver ConfigureFrameFormat).
        // Sprint NDI-video-uyvy (2026-09-12): "RV32" (BGRX/BGRA) trocado por "UYVY" — LibVLC aceita
        // essa string de chroma diretamente em SetVideoFormat (mesmo mecanismo generico ja usado pra
        // RV32, so muda a string pedida).
        _framePlayer.SetVideoFormat("UYVY", FrameWidth, FrameHeight, FrameStrideBytes);
        _framePlayer.SetVideoCallbacks(_frameLockCallback, null, _frameDisplayCallback);
        _framePlayer.EndReached += OnFramePlayerEndReached;

        // Sprint NDI-Loop-Seamless (2026-09-17): segundo player de captura, gemeo do acima — ver
        // comentario completo de classe de _framePlayerB. Sempre criado (custo de uma instancia
        // MediaPlayer extra parada, desprezivel — mesmo racional ja aceito pro par
        // _mediaPlayer/_framePlayer original), mas so chega a receber uma Media/tocar de verdade se
        // _seamlessLoopEnabled estiver ligado (ver ArmStandbySlotA/ArmStandbySlotB) — com a variavel
        // de ambiente ausente (padrao), fica parado a vida inteira, sem nenhum efeito.
        _framePlayerB = new MediaPlayer(_libVlc);
        _frameBuffersB[0] = Marshal.AllocHGlobal(FrameStrideBytes * FrameHeight);
        _frameBuffersB[1] = Marshal.AllocHGlobal(FrameStrideBytes * FrameHeight);
        FillFrameBufferOpaqueBlack(_frameBuffersB);
        _frameLockCallbackB = OnFrameLockB;
        _frameDisplayCallbackB = OnFrameDisplayB;
        _framePlayerB.SetVideoFormat("UYVY", FrameWidth, FrameHeight, FrameStrideBytes);
        _framePlayerB.SetVideoCallbacks(_frameLockCallbackB, null, _frameDisplayCallbackB);
        _framePlayerB.EndReached += OnFramePlayerBEndReached;

        // Sprint NDI-Loop-Seamless (2026-09-17): lida uma unica vez aqui — ver comentario completo
        // do campo _seamlessLoopEnabled pro motivo de ser so leitura de variavel de ambiente nesta
        // primeira sprint, sem opcao na tela.
        _seamlessLoopEnabled = string.Equals(
            Environment.GetEnvironmentVariable("CARTUCHEIRA_SEAMLESS_LOOP"),
            "1",
            StringComparison.Ordinal);

        // Sprint Áudio-Dispositivo-Diagnóstico-4 (2026-09-19): o log real confirmou que a correção de
        // tempo da Diagnostico-3 funcionou (última tentativa disparando aos ~6s, não mais ~15s), mas o
        // usuário segue ouvindo picotada durante boa parte desses ~6 segundos — ou seja, mesmo com o
        // tempo certo, as 7 chamadas de SetOutputDevice continuam causando cortes audíveis (a causa
        // raiz documentada desde a Áudio-Livewire-Duplo-Fix-2 nunca foi eliminada, só a duração da
        // janela). A comparação com um player externo (que aplica a placa uma única vez e nunca
        // engasga) sugere que, pelo menos neste hardware, UMA chamada já basta — mas reduzir tentativas
        // já causou silêncio total numa placa diferente no passado (Fix-2, revertida). Em vez de
        // arriscar mudar o padrão de novo, esta flag (desligada por padrão, igual à
        // CARTUCHEIRA_SEAMLESS_LOOP) permite TESTAR a hipótese isoladamente: com ela ligada, só a
        // PRIMEIRA tentativa (100ms) acontece, nenhuma das outras 6. Sem ela, nada muda — continuam as
        // 7 de sempre. Ver comentário completo em ReapplyAudioOutputDeviceAsync.
        _audioDeviceUmaTentativaEnabled = string.Equals(
            Environment.GetEnvironmentVariable("CARTUCHEIRA_AUDIO_DEVICE_UMA_TENTATIVA"),
            "1",
            StringComparison.Ordinal);

        if (_audioDeviceUmaTentativaEnabled)
        {
            LogAudioDeviceDiagnostic("MODO-UMA-TENTATIVA-ATIVO (CARTUCHEIRA_AUDIO_DEVICE_UMA_TENTATIVA=1)");
        }

        // Sprint Áudio-Dispositivo-Diagnóstico-5 (2026-09-19): mesma ideia de flag opcional acima, ver
        // comentario completo em Load().
        _audioDeviceViaOpcaoMediaEnabled = string.Equals(
            Environment.GetEnvironmentVariable("CARTUCHEIRA_AUDIO_DEVICE_VIA_OPCAO_MEDIA"),
            "1",
            StringComparison.Ordinal);

        if (_audioDeviceViaOpcaoMediaEnabled)
        {
            LogAudioDeviceDiagnostic("MODO-OPCAO-MEDIA-ATIVO (CARTUCHEIRA_AUDIO_DEVICE_VIA_OPCAO_MEDIA=1)");
        }

        // Sprint Áudio-Latência-Placa-Local (2026-09-19): ver comentario completo em
        // _audioWasapiExclusiveEnabled.
        _audioWasapiExclusiveEnabled = string.Equals(
            Environment.GetEnvironmentVariable("CARTUCHEIRA_AUDIO_WASAPI_EXCLUSIVE"),
            "1",
            StringComparison.Ordinal);

        if (_audioWasapiExclusiveEnabled)
        {
            LogAudioDeviceDiagnostic("MODO-WASAPI-EXCLUSIVO-ATIVO (CARTUCHEIRA_AUDIO_WASAPI_EXCLUSIVE=1)");
        }

        // Sprint Áudio-Latência-Placa-Local-Sincronismo (2026-09-19): ver comentario completo em
        // _ndiSyncDelayMs. Zero/nao definida/nao numerica/negativa = desligado (mesmo comportamento
        // de sempre).
        var ndiSyncDelayRaw = Environment.GetEnvironmentVariable("CARTUCHEIRA_AUDIO_SYNC_ATRASO_NDI_MS");
        _ndiSyncDelayMs = int.TryParse(ndiSyncDelayRaw, out var parsedNdiSyncDelayMs) && parsedNdiSyncDelayMs > 0
            ? parsedNdiSyncDelayMs
            : 0;

        if (_ndiSyncDelayMs > 0)
        {
            LogAudioDeviceDiagnostic($"MODO-SINCRONISMO-NDI-ATIVO atrasoMs={_ndiSyncDelayMs} (CARTUCHEIRA_AUDIO_SYNC_ATRASO_NDI_MS={ndiSyncDelayRaw})");
        }

        // Sprint NDI-audio-NAudio (2026-08-11) — repassa direto (mesma decisao de latencia ja
        // documentada nos outros eventos desta classe: quem ouve isto nunca toca em estado de
        // binding do WPF, marshalar so acrescentaria atraso sem necessidade).
        _audioCapture.AudioFrameReady += (_, e) => AudioFrameReady?.Invoke(this, e);

        // Sprint NDI-audio-video-clock-lock-hotfix (2026-09-12): ver comentario de
        // PollFramePlayerPosition. Comeca a rodar imediatamente e pra vida inteira desta instancia
        // (nao so durante Play) — leitura de posicao antes de qualquer Load simplesmente retorna -1
        // (contrato do LibVLC ja tratado la), custo de uma property read a cada 50ms e desprezivel,
        // e assim nao precisa de nenhum hook extra em Play/Pause/Stop pra iniciar/parar o timer
        // (prioridade Simplicidade — menos estado pra ficar consistente).
        _videoPositionPollTimer = new Timer(PollFramePlayerPosition, null, VideoPositionPollIntervalMs, VideoPositionPollIntervalMs);
    }

    /// <summary>Disparado em toda transicao de estado real reportada pelo LibVLC (thread do LibVLC — quem ouvir precisa marshalar pra UI thread sozinho).</summary>
    public event EventHandler<EngineStateChangedEventArgs>? StateChanged;

    /// <summary>Disparado periodicamente durante a reproducao (thread do LibVLC).</summary>
    public event EventHandler<EnginePositionChangedEventArgs>? PositionChanged;

    /// <summary>Disparado quando o arquivo termina SEM loop ativo (thread do LibVLC).</summary>
    public event EventHandler? MediaEnded;

    /// <summary>Disparado quando o LibVLC reporta um erro de reproducao (thread do LibVLC).</summary>
    public event EventHandler<string>? ErrorOccurred;

    /// <summary>
    /// Disparado a cada quadro que o player de captura termina de decodificar (thread do LibVLC).
    /// NAO marshalado pra UI thread de proposito: quem ouve isto (NdiSender, via DeckViewModel)
    /// nunca toca em estado ligado a binding do WPF, e marshalar aqui so acrescentaria latencia
    /// sem necessidade, contra a prioridade #1 do projeto (Latencia). Ver o aviso de lifetime do
    /// buffer em <see cref="EngineFrameReadyEventArgs"/>.
    /// </summary>
    public event EventHandler<EngineFrameReadyEventArgs>? FrameReady;

    /// <summary>
    /// Disparado a cada bloco de amostras que <see cref="NdiAudioCapture"/> decodifica (Sprint
    /// NDI-audio-NAudio, 2026-08-11: thread dedicada desta classe, nao mais a thread do LibVLC — mesma
    /// decisao de NAO marshalar pra UI thread de <see cref="FrameReady"/>, pelo mesmo motivo: quem
    /// ouve isto (NdiSender, via DeckViewModel) nunca toca em estado de binding do WPF).
    /// <see cref="EngineAudioFrameReadyEventArgs.Data"/> e um array gerenciado proprio deste bloco —
    /// ver comentario da classe do EventArgs.
    /// </summary>
    public event EventHandler<EngineAudioFrameReadyEventArgs>? AudioFrameReady;

    /// <summary>
    /// Instancia real do LibVLCSharp.Shared.MediaPlayer, para quem for hospedar o render de video
    /// (ex.: VideoView do LibVLCSharp.WPF, na Presentation).
    /// </summary>
    public MediaPlayer MediaPlayer => _mediaPlayer;

    /// <summary>
    /// Sprint NDI-Frame-Rate-Real (2026-09-16): frame rate REAL (numerador/denominador) do arquivo
    /// atualmente carregado, detectado no ultimo <see cref="Load"/> — ver comentario completo do
    /// campo <see cref="_videoFrameRateNum"/>. Quem le isto (<c>DeckViewModel.Load</c>) repassa pro
    /// <see cref="Ndi.NdiSender.SetFrameRate"/> do Deck, pra o metadado que vai pro NDI bater com o
    /// video de verdade.
    /// </summary>
    public (int Num, int Den) VideoFrameRate => (_videoFrameRateNum, _videoFrameRateDen);

    /// <summary>
    /// Quando verdadeiro, o fim do arquivo reinicia a reproducao em vez de disparar
    /// <see cref="MediaEnded"/>. Pode ser alterado a qualquer momento, inclusive durante a
    /// reproducao — passa a valer a partir do proximo fim de arquivo.
    /// </summary>
    public bool LoopEnabled
    {
        get => _loopEnabled;
        set
        {
            _loopEnabled = value;
            // Mantem NdiAudioCapture em sincronia — ele nao sabe nada sobre o estado do video, quem
            // garante que os dois concordam sobre loop e esta classe (ver comentario de classe de
            // NdiAudioCapture).
            _audioCapture.LoopEnabled = value;
        }
    }

    public bool IsPlaying => _mediaPlayer.IsPlaying;

    /// <summary>
    /// Associa um arquivo ao player. Quase sempre aceito, mesmo com uma midia ja tocando (a midia
    /// anterior e descartada) — EXCETO quando <see cref="TryValidateMediaIsPlayable"/> identifica o
    /// arquivo como corrompido (ver comentario completo la pro incidente real que motivou esta
    /// recusa, Fix v1.0.61/2026-11-12): nesse caso este metodo nao toca em NADA do estado existente
    /// (midia anterior, se houver, continua carregada exatamente como estava) e retorna
    /// <c>false</c> depois de disparar <see cref="ErrorOccurred"/>. Nao inicia a reproducao mesmo
    /// quando aceito — quem decide iniciar e o chamador (<see cref="Play"/>).
    /// </summary>
    /// <returns><c>true</c> se o arquivo foi aceito e carregado nos players; <c>false</c> se foi recusado por estar corrompido (ver <see cref="ErrorOccurred"/> pro motivo exato).</returns>
    public bool Load(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            throw new ArgumentException("Caminho do arquivo nao pode ser vazio.", nameof(filePath));
        }

        // Fix v1.0.61 (2026-11-12): incidente real — usuario carregou um .mov com o cabecalho do
        // container corrompido ("PARADISE - DISNEY_OK.mov", confirmado por ele mesmo que nem o
        // Windows Media Player abre o mesmo arquivo) e o processo inteiro fechava com acesso
        // invalido nativo (0xc0000005/ntdll.dll — diagnostico completo, incluindo o log nativo do
        // libvlc que confirmou a causa, em sprint-2026-11-11-investigacao-crash-nativo-play.md e
        // sprint-2026-11-12-protecao-arquivo-corrompido.md). Esta checagem roda ANTES de tocar em
        // qualquer estado dos players — ver comentario completo de
        // <see cref="TryValidateMediaIsPlayable"/> pro racional.
        if (!TryValidateMediaIsPlayable(filePath, out var invalidReason))
        {
            ErrorOccurred?.Invoke(this, $"Arquivo corrompido ou em formato não reconhecido: \"{Path.GetFileName(filePath)}\" ({invalidReason}). Não foi carregado — nada do que já estava tocando foi afetado.");
            return false;
        }

        // Sprint NDI-audio-NAudio (2026-08-11): a hipotese "player de preview monopolizando o audio
        // real do arquivo" (testada com ":no-audio" temporario aqui) deixou de fazer sentido pra
        // testar — o audio pro NDI agora vem de uma decodificacao TOTALMENTE INDEPENDENTE via NAudio
        // (ver _audioCapture.Load abaixo), sem nenhum estado do LibVLC compartilhado entre os dois
        // caminhos. ":no-audio" removido: o preview volta a tocar audio local normalmente (mesmo
        // comportamento de antes da Sprint NDI-audio) — o operador ouve o Deck de novo.
        var previousMedia = _currentMedia;
        _currentMedia = new Media(_libVlc, filePath, FromType.FromPath);

        // Sprint Áudio-Dispositivo-Diagnóstico-5 (2026-09-19): "semeia" o dispositivo de audio
        // configurado (se ja soubermos qual e, de um Play anterior nesta mesma sessao) direto na Media,
        // ANTES do Play — ver comentario completo em _audioDeviceViaOpcaoMediaEnabled. So faz sentido
        // se _pendingAudioDeviceId ja tiver um valor (nada a semear no primeiro Play da sessao, antes
        // de qualquer SetAudioOutputDevice ter sido chamado — esse continua caindo no mecanismo de
        // reforco de sempre, sem regressao nenhuma).
        SeedAudioDeviceOptionNaMediaSeAtivo(_currentMedia);

        // Sprint Áudio-Latência-Placa-Local (2026-09-19): ver comentario completo em
        // _audioWasapiExclusiveEnabled — ataca a CAUSA do atraso (buffer do modo compartilhado do
        // Windows), nao so compensa o sintoma numa ferramenta rio abaixo. So se aplica ao player
        // LOCAL (_mediaPlayer, o que sai pela placa fisica) — nunca ao _framePlayer/_audioCapture
        // (caminho do NDI, que ja e o lado RAPIDO e nao precisa de nenhuma mudanca).
        if (_audioWasapiExclusiveEnabled)
        {
            try
            {
                _currentMedia.AddOption(":wasapi-exclusive");
                LogAudioDeviceDiagnostic("WASAPI-EXCLUSIVO-APLICADO-NA-MEDIA");
            }
            catch (Exception ex)
            {
                LogAudioDeviceDiagnostic($"WASAPI-EXCLUSIVO-ERRO ex={ex.Message}");
                // Ignorado de proposito — na pior hipotese, o audio local sai no modo compartilhado
                // de sempre (comportamento pre-existente), nunca pior que isso.
            }
        }

        _mediaPlayer.Media = _currentMedia;
        previousMedia?.Dispose();

        // Media PROPRIO pro player de captura de VIDEO (nao o mesmo objeto do player principal — ver
        // comentario do construtor). Mesmo arquivo, segunda decodificacao independente.
        //
        // CORRECAO (achado revendo a regressao "audio tocando na placa padrao em vez da
        // configurada", mantida apos a migracao pra NAudio): este player so decodifica video pra NDI
        // (SetVideoFormat/SetVideoCallbacks, ver construtor) — sem ":no-audio", o LibVLC decodificava
        // a trilha de audio do arquivo TAMBEM e mandava pro dispositivo de audio PADRAO do Windows
        // por conta propria, um consumidor de audio indesejado e nunca configuravel. ":no-audio" aqui
        // e a correcao definitiva: este player nunca precisou de audio nenhum, so decodifica video.
        // Fix v1.0.60 (2026-11-11): achado revendo este metodo investigando o crash de acesso
        // invalido (0xc0000005/ntdll.dll) relatado ao dar Play — este Stop() aqui estava FALTANDO,
        // assimetrico com o _framePlayerB.Stop() alguns campos abaixo (que SEMPRE e chamado antes de
        // trocar a Media do slot B). Sem ele, se este metodo for chamado com o slot A ainda tocando
        // de verdade (ex.: operador troca o arquivo do mesmo Deck sem parar primeiro, ou chama Load
        // de novo rapido), a troca de _framePlayer.Media + ConfigureFrameFormat (que reescreve
        // _frameOffsetXBytes/_frameOffsetYBytes/chama SetVideoFormat com as dimensoes do arquivo
        // NOVO) acontece na thread da interface ENQUANTO a thread interna do LibVLC ainda pode estar
        // dentro de OnFrameLock/OnFrameDisplay lendo esses mesmos campos pro arquivo ANTIGO — uma
        // corrida de dados classica entre threads, e exatamente a classe de bug (estado nativo
        // compartilhado sem sincronizacao) que ja causou os sete crashes historicos do audio NDI
        // documentados em NdiSender. Stop() aqui forca o LibVLC a terminar de usar o formato/buffers
        // antigos antes de qualquer campo novo ser escrito. NAO comprovado ainda como a causa do
        // crash relatado (o usuario descreveu um Play isolado, nao uma troca de arquivo em
        // sequencia) — corrigido de qualquer forma por ser um defeito real e de baixo risco, mas a
        // investigacao do crash continua: ver tambem o log nativo do LibVLC adicionado nesta mesma
        // versao (App.xaml.cs) pra capturar a causa raiz de verdade na proxima reproducao.
        _framePlayer.Stop();

        var previousFrameMedia = _currentFrameMedia;
        _currentFrameMedia = new Media(_libVlc, filePath, FromType.FromPath, ":no-audio");
        _framePlayer.Media = _currentFrameMedia;
        previousFrameMedia?.Dispose();

        ConfigureFrameFormat(_currentFrameMedia);

        // Sprint NDI-TryCorrectDrift-Ruido (2026-09-16): reseta a ancora de suavizacao de posicao de
        // video (ver comentario de PollFramePlayerPosition) — um Load() novo troca o arquivo debaixo
        // do _framePlayer, e a proxima leitura de .Time vai refletir o arquivo NOVO (tipicamente
        // perto de 0); sem este reset, a ancora antiga (do arquivo anterior, possivelmente um valor
        // bem maior) ficaria "vazando" tempo extrapolado por ate VideoPositionExtrapolationCapMs
        // antes de se corrigir sozinha na proxima leitura real.
        _lastRawFramePlayerTimeMs = -1;
        _framePlayerPositionAnchorClock.Reset();

        // Sprint NDI-Loop-Seamless (2026-09-17): um Load() novo troca o arquivo debaixo dos DOIS
        // slots — descarta qualquer standby (B) que estivesse armado/tocando pro arquivo ANTERIOR, e
        // volta o estado do mecanismo pro ponto de partida (A ativo, nada armado). Guarda o caminho
        // pra ArmStandbySlotA/ArmStandbySlotB poderem recriar a Media do slot em standby mais tarde,
        // sem o chamador precisar passar o caminho nenhuma outra vez.
        _currentFilePath = filePath;
        _framePlayerB.Stop();
        _framePlayerB.Media = null;
        _currentFrameMediaB?.Dispose();
        _currentFrameMediaB = null;
        _seamlessSlotAIsActive = true;
        _seamlessStandbyArmedThisCycle = false;
        _standbyHasFrame = false;

        // Sprint NDI-audio-NAudio (2026-08-11): decodificacao de audio pro NDI, agora via NAudio —
        // terceira leitura independente do mesmo arquivo, so que fora do LibVLC por completo (ver
        // comentario de classe de NdiAudioCapture pro historico/motivo da migracao). NdiAudioCapture
        // ja cuida da propria sincronizacao (para a decodificacao anterior antes de trocar de
        // arquivo) — nao ha corrida de use-after-free possivel aqui, unica classe de bug que
        // derrubou o pipeline anterior sete vezes.
        _audioCapture.Load(filePath);

        StateChanged?.Invoke(this, new EngineStateChangedEventArgs(EnginePlaybackState.Loaded));
        return true;
    }

    /// <summary>
    /// Fix v1.0.61 (2026-11-12): ver comentario completo de <see cref="Load"/> pro incidente real
    /// que motivou esta checagem (arquivo .mov corrompido derrubando o processo inteiro com acesso
    /// invalido nativo — 0xc0000005/ntdll.dll — confirmado via log nativo do libvlc, ver
    /// sprint-2026-11-12-protecao-arquivo-corrompido.md no projeto).
    ///
    /// O libvlc, ao abrir um arquivo, e DELIBERADAMENTE permissivo: se o demuxer "certo" pro
    /// formato (aqui, "mp4", pro container ISO-BMFF usado por .mov/.mp4) descarta o arquivo por
    /// inconsistencia, o libvlc cai numa cadeia de FALLBACK tentando outros demuxers completamente
    /// diferentes (no incidente real: ts, mod, avcodec, e por fim "ps" — Program Stream do MPEG,
    /// formato sem nenhuma relacao com .mov) — ele foi desenhado pra tentar tocar QUALQUER coisa,
    /// nunca pra validar. O demuxer errado aceitando o arquivo mesmo incompativel decodifica LIXO
    /// como se fosse video valido ("garbage at input, trying to resync..." repetido centenas de
    /// vezes no log nativo do incidente real) — e esse lixo, passando pelo pipeline de callback RAW
    /// de video deste Engine (<see cref="OnFrameLock"/>/<see cref="OnFrameDisplay"/>, que escreve
    /// direto em buffers nativos de tamanho fixo sem nenhuma validacao de que o decodificador
    /// produziu algo que faz sentido), e o candidato mais provavel pra causar a corrupcao de
    /// memoria real observada.
    ///
    /// Em vez de tentar sobreviver a corrupcao depois dela acontecer (impossivel pra uma violacao
    /// de acesso nativa — ver comentario de classe de <c>CrashLogger</c>, Infrastructure), este
    /// metodo faz uma analise RASA e BARATA (so le os cabecalhos dos boxes de nivel superior do
    /// container ISO-BMFF — nunca o conteudo real de video/audio) ANTES de qualquer midia ser
    /// entregue pro libvlc pra decodificar de verdade. Um arquivo com a classe exata de corrupcao
    /// de cabecalho confirmada no incidente real (um box que declara tamanho 0 — convencao
    /// ISO-BMFF pra "estende at o fim do arquivo" — sem realmente ser o ultimo box de verdade) e
    /// recusado imediatamente, com um aviso honesto pro operador, SEM jamais engajar o pipeline
    /// nativo de callback de video.
    ///
    /// Deliberadamente CONSERVADOR, de proposito — so recusa quando encontra uma inconsistencia
    /// estrutural CLARA e inequivoca:
    /// - Qualquer arquivo que nao comece com um box ISO-BMFF reconhecido (outro container
    ///   qualquer — MKV, AVI, WebM, etc.) passa direto SEM checagem nenhuma: esta validacao e
    ///   estritamente ADICIONAL ao comportamento ja permissivo do libvlc, nunca mais restritiva
    ///   pra formatos que sempre funcionaram.
    /// - Tamanho de box "1" (escape ISO-BMFF pra tamanho de 64 bits, arquivo &gt; 4GB) e qualquer
    ///   erro de I/O (arquivo bloqueado por outro processo, permissao, etc.) tambem deixam passar
    ///   sem opiniao — nunca bloqueia um Load por um motivo que nao seja a corrupcao de cabecalho
    ///   especifica que este metodo sabe detectar com confianca.
    /// </summary>
    private static bool TryValidateMediaIsPlayable(string filePath, out string? invalidReason)
    {
        invalidReason = null;

        try
        {
            using var stream = File.OpenRead(filePath);
            var fileLength = stream.Length;

            // Precisa de pelo menos um cabecalho de box completo (4 bytes de tamanho + 4 de
            // fourcc) pra tentar qualquer analise — arquivo menor que isso nao e um ISO-BMFF
            // reconhecivel por esta checagem; deixa passar sem opiniao (ver nota de
            // conservadorismo no comentario de classe).
            if (fileLength < 8)
            {
                return true;
            }

            Span<byte> header = stackalloc byte[8];
            long offset = 0;

            while (offset + 8 <= fileLength)
            {
                stream.Position = offset;
                var read = stream.Read(header);
                if (read < 8)
                {
                    break;
                }

                // ISO-BMFF: tamanho do box em big-endian (ordem de bytes de rede) nos 4 primeiros
                // bytes — nunca a ordem nativa da maquina (little-endian em x86/x64). Cada termo
                // ja convertido pra `long` ANTES do shift, pra nunca arriscar o overflow de int32
                // que `byte << 24` sofreria pra valores de byte >= 128 (shift em aritmetica de
                // int, nao de long, se nao fosse convertido primeiro).
                long boxSize = ((long)header[0] << 24) | ((long)header[1] << 16) | ((long)header[2] << 8) | header[3];
                var fourCc = Encoding.ASCII.GetString(header.Slice(4, 4));

                var isKnownTopLevelBox = fourCc is "ftyp" or "moov" or "mdat" or "free" or "skip" or "wide" or "pnot" or "uuid" or "moof" or "mfra";
                if (!isKnownTopLevelBox)
                {
                    // Primeiro box nao reconhecido — ou nao e um arquivo ISO-BMFF de verdade
                    // (outro container, sem relacao com este formato), ou chegamos ao fim da
                    // cadeia de boxes de nivel superior que esta checagem sabe nomear. Nos dois
                    // casos, deixa passar sem opiniao daqui em diante (conservador de proposito).
                    return true;
                }

                // Tamanho 0 so e valido pro box que REALMENTE e o ultimo do arquivo (convencao
                // oficial ISO-BMFF pra "estende at o fim do arquivo") — um box de tamanho 0 que
                // NAO esta no fim real do arquivo e estruturalmente impossivel (ninguem sabe onde
                // ele termina e o proximo comeca) e e EXATAMENTE a assinatura confirmada pelo log
                // nativo do libvlc no incidente real que motivou esta correcao.
                if (boxSize == 0)
                {
                    var isLastBoxInFile = offset + 8 >= fileLength;
                    if (!isLastBoxInFile)
                    {
                        invalidReason = $"box \"{fourCc}\" no deslocamento {offset} declara tamanho 0 sem ser o último bloco do arquivo (cabeçalho corrompido/truncado)";
                        return false;
                    }

                    break;
                }

                // Escape ISO-BMFF pra "tamanho real nos proximos 8 bytes" (box maior que 4GB) —
                // nao suportado por esta checagem rasa; deixa passar sem opiniao em vez de
                // arriscar um falso positivo.
                if (boxSize == 1)
                {
                    return true;
                }

                // Tamanho menor que o proprio cabecalho (8 bytes) e impossivel pra qualquer box
                // real — mesma familia de corrupcao de cabecalho do caso de tamanho 0.
                if (boxSize < 8)
                {
                    invalidReason = $"box \"{fourCc}\" no deslocamento {offset} declara tamanho {boxSize}, menor que o próprio cabeçalho (cabeçalho corrompido)";
                    return false;
                }

                // Box declara se estender alem do fim real do arquivo — mesma classe de
                // corrupcao (tamanho inconsistente com o arquivo de verdade), mesmo nao sendo zero.
                if (offset + boxSize > fileLength)
                {
                    invalidReason = $"box \"{fourCc}\" no deslocamento {offset} declara tamanho {boxSize}, que ultrapassa o tamanho real do arquivo ({fileLength} bytes)";
                    return false;
                }

                offset += boxSize;
            }

            // Fim da leitura (ou bloco final incompleto, comum e inofensivo) sem encontrar
            // nenhuma inconsistencia — sem motivo pra recusar.
            return true;
        }
        catch (Exception)
        {
            // Falha honesta (arquivo bloqueado por outro processo, permissao, I/O, etc.) — nunca
            // bloqueia um Load por um motivo nao relacionado a corrupcao real do conteudo; deixa o
            // libvlc tentar normalmente, exatamente o comportamento de antes desta sprint pra
            // qualquer coisa que esta checagem nao saiba avaliar.
            return true;
        }
    }

    /// <summary>
    /// Sprint Deck-Aspect-Ratio (2026-08-11): calcula o retangulo que preserva a proporcao real do
    /// arquivo dentro do canvas fixo 1920x1080 e reconfigura o player de captura pra esse tamanho —
    /// chamado a cada <see cref="Load"/> (cada arquivo pode ter uma proporcao diferente). Preenche o
    /// buffer inteiro de preto opaco antes (as bordas do letterbox/pillarbox do arquivo novo podem
    /// ser maiores do que as do arquivo anterior — sem isso, sobrariam pixels do quadro anterior nas
    /// bordas).
    ///
    /// Sprint NDI-video-uyvy (2026-09-12): <c>_frameOffsetXBytes</c> precisa cair sempre num limite
    /// de grupo UYVY (2 pixels/4 bytes) — <see cref="ComputeLetterboxSize"/> agora garante innerWidth
    /// multiplo de 4 (nao so par), o que garante <c>(FrameWidth - innerWidth) / 2</c> (o deslocamento
    /// em PIXELS) sempre par, e portanto o deslocamento em BYTES (<c>* BytesPerPixel</c>, que agora e
    /// 2) sempre multiplo de 4 — nunca corta um grupo U,Y0,V,Y1 ao meio. O deslocamento vertical
    /// (<c>_frameOffsetYBytes</c>) nao tem essa restricao (UYVY nao faz subamostragem vertical,
    /// cada linha e independente) — continua so multiplicando pelo stride da linha inteira.
    /// </summary>
    private void ConfigureFrameFormat(Media frameMedia)
    {
        var (innerWidth, innerHeight) = ComputeLetterboxSize(frameMedia);

        _frameInnerWidth = innerWidth;
        _frameInnerHeight = innerHeight;
        _frameOffsetXBytes = (FrameWidth - innerWidth) / 2 * BytesPerPixel;
        _frameOffsetYBytes = (FrameHeight - innerHeight) / 2 * FrameStrideBytes;

        FillFrameBufferOpaqueBlack(_frameBuffers);
        FillFrameBufferOpaqueBlack(_frameBuffersB);

        _framePlayer.SetVideoFormat("UYVY", (uint)innerWidth, (uint)innerHeight, (uint)FrameStrideBytes);

        // Sprint NDI-Loop-Seamless (2026-09-17): mesma geometria (o slot B sempre decodifica o MESMO
        // arquivo que o A, so em standby) — configurado aqui mesmo com o modo desligado, custo
        // desprezivel (so registra o formato pro player, nao decodifica nada sozinho).
        _framePlayerB.SetVideoFormat("UYVY", (uint)innerWidth, (uint)innerHeight, (uint)FrameStrideBytes);
    }

    /// <summary>
    /// Le a resolucao/orientacao real do video (parse local, com timeout curto) e calcula o maior
    /// retangulo innerWidth x innerHeight que cabe dentro do canvas 1920x1080 sem distorcer a
    /// proporcao original do arquivo (mesma conta de "fit"/letterbox de sempre). Falha honesta em
    /// qualquer problema (sem faixa de video, parse expirado/sem sucesso, dimensao invalida): cai
    /// pro canvas inteiro (1920x1080) — exatamente o comportamento de antes desta sprint, sem
    /// regressao nesse caso.
    ///
    /// Sprint NDI-video-uyvy (2026-09-12): innerWidth agora arredondado pra multiplo de 4 (nao mais
    /// so par) — UYVY empacota 2 pixels por grupo de 4 bytes, e como <c>FrameWidth</c> (1920) ja e
    /// multiplo de 4, innerWidth multiplo de 4 garante que <c>(FrameWidth - innerWidth) / 2</c> (o
    /// deslocamento horizontal do letterbox, em pixels) seja sempre par — condicao necessaria pra
    /// <see cref="ConfigureFrameFormat"/> nunca cortar um grupo U,Y0,V,Y1 ao meio. innerHeight
    /// continua so par (UYVY nao subamostra verticalmente, sem essa restricao extra).
    /// </summary>
    private (int Width, int Height) ComputeLetterboxSize(Media frameMedia)
    {
        // Sprint NDI-Frame-Rate-Real (2026-09-16): reseta pro fallback fixo de sempre (30000/1000)
        // ANTES de tentar detectar o real — qualquer saida antecipada abaixo (parse falhou, sem
        // faixa de video, dimensao invalida, excecao) deixa o valor no mesmo fallback de sempre, sem
        // regressao. So e sobrescrito mais abaixo, se a faixa de video tiver um frame rate valido.
        _videoFrameRateNum = 30000;
        _videoFrameRateDen = 1000;

        try
        {
            var status = frameMedia.Parse(MediaParseOptions.ParseLocal, AspectRatioParseTimeoutMs)
                .GetAwaiter().GetResult();

            if (status != MediaParsedStatus.Done)
            {
                return (FrameWidth, FrameHeight);
            }

            MediaTrack videoTrack = default;
            var found = false;
            foreach (var track in frameMedia.Tracks)
            {
                if (track.TrackType == TrackType.Video)
                {
                    videoTrack = track;
                    found = true;
                    break;
                }
            }

            if (!found)
            {
                return (FrameWidth, FrameHeight);
            }

            double nativeWidth = videoTrack.Data.Video.Width;
            double nativeHeight = videoTrack.Data.Video.Height;

            // Sprint NDI-Frame-Rate-Real (2026-09-16): mesma faixa de video ja lida acima pra
            // resolucao — FrameRateNum/FrameRateDen e o par que o proprio libvlc usa (video_track_t
            // nativo, espelhado 1:1 pelo LibVLCSharp). So sobrescreve o fallback se os dois vierem
            // validos (>0) — um arquivo estranho que reporte 0/0 (frame rate variavel/desconhecido
            // pro parser local) fica no fallback fixo, mesmo comportamento de sempre.
            var trackFrameRateNum = videoTrack.Data.Video.FrameRateNum;
            var trackFrameRateDen = videoTrack.Data.Video.FrameRateDen;
            if (trackFrameRateNum > 0 && trackFrameRateDen > 0)
            {
                _videoFrameRateNum = (int)trackFrameRateNum;
                _videoFrameRateDen = (int)trackFrameRateDen;
            }

            // Orientacao de rotacao (video vertical gravado por celular, por ex.) troca largura por
            // altura pra efeito de PROPORCAO DE EXIBICAO — pesquisa desta sprint confirmou que
            // Width/Height do LibVLCSharp sao sempre as dimensoes de DECODIFICACAO (antes de
            // rotacionar), nao as de exibicao final.
            var rotated = videoTrack.Data.Video.Orientation is VideoOrientation.LeftTop
                or VideoOrientation.LeftBottom or VideoOrientation.RightTop or VideoOrientation.RightBottom;
            if (rotated)
            {
                (nativeWidth, nativeHeight) = (nativeHeight, nativeWidth);
            }

            if (nativeWidth <= 0 || nativeHeight <= 0)
            {
                return (FrameWidth, FrameHeight);
            }

            var sourceAspect = nativeWidth / nativeHeight;
            var canvasAspect = (double)FrameWidth / FrameHeight;

            int innerWidth, innerHeight;
            if (sourceAspect > canvasAspect)
            {
                innerWidth = FrameWidth;
                innerHeight = (int)Math.Round(FrameWidth / sourceAspect);
            }
            else
            {
                innerHeight = FrameHeight;
                innerWidth = (int)Math.Round(FrameHeight * sourceAspect);
            }

            // Sprint NDI-video-uyvy (2026-09-12): innerWidth alinhado a 4px (era 2px) — ver
            // comentario do metodo acima pro motivo (grupo UYVY de 2 pixels/4 bytes). innerHeight
            // continua so em 2px, sem essa restricao extra.
            innerWidth -= innerWidth % 4;
            innerHeight -= innerHeight % 2;

            return (Math.Clamp(innerWidth, 4, FrameWidth), Math.Clamp(innerHeight, 2, FrameHeight));
        }
        catch (Exception)
        {
            // Fronteira com o LibVLC nativo — qualquer falha aqui cai pro canvas inteiro, nunca
            // trava nem quebra o Load (mesmo racional ja usado em GetDurationAsync/outras
            // fronteiras nativas deste projeto).
            return (FrameWidth, FrameHeight);
        }
    }

    /// <summary>
    /// Preenche OS DOIS <see cref="_frameBuffers"/> inteiros de preto (letterbox de transmissao
    /// precisa de barra preta solida). Sem `unsafe`/ponteiros de proposito — só Marshal.Copy, mesmo
    /// estilo do resto deste arquivo. Preenche os dois buffers sempre (nao so o ativo no momento):
    /// chamado a cada <see cref="Load"/> via <see cref="ConfigureFrameFormat"/>, e o buffer que esta
    /// INATIVO agora pode virar o ativo no proximo quadro, antes do LibVLC ter escrito nele de novo
    /// — precisa comecar limpo tambem.
    ///
    /// Sprint NDI-video-uyvy (2026-09-12): antes (BGRX) o preto era RGB=0 com o 4º byte (alpha/X) em
    /// 255. UYVY nao tem canal alfa — preto "opaco" so existe no sentido de ser um preto valido de
    /// transmissao, nao transparencia (UYVY nunca teve transparencia pra comecar, BGRX so mantinha
    /// isso por historico de vir do BGRA). Preto em faixa limitada/broadcast (mesma faixa que a NDI
    /// SDK espera por padrao pra YCbCr): Y=16, U=128, V=128 (confirmado via VideoLAN wiki, wiki.
    /// videolan.org/YUV) — o padrao de 4 bytes que se repete ao longo de toda a linha, cobrindo 2
    /// pixels por grupo, e [U=128, Y=16, V=128, Y=16].
    ///
    /// Sprint NDI-Loop-Seamless (2026-09-17): passou a receber o array de buffers como parametro
    /// (era sempre <see cref="_frameBuffers"/> direto) pra poder preencher tambem
    /// <see cref="_frameBuffersB"/>, do segundo player — mesma logica, so parametrizada.
    /// </summary>
    private void FillFrameBufferOpaqueBlack(IntPtr[] buffers)
    {
        var row = new byte[FrameStrideBytes];
        for (var x = 0; x + 3 < FrameStrideBytes; x += 4)
        {
            row[x] = 128;     // U
            row[x + 1] = 16;  // Y0
            row[x + 2] = 128; // V
            row[x + 3] = 16;  // Y1
        }

        foreach (var buffer in buffers)
        {
            for (var y = 0; y < FrameHeight; y++)
            {
                Marshal.Copy(row, 0, buffer + y * FrameStrideBytes, FrameStrideBytes);
            }
        }
    }

    /// <summary>
    /// Descarrega a midia atual (se houver) e para os dois players — diferente de <see cref="Stop"/>,
    /// que MANTEM a midia carregada pra um proximo Play recomecar do inicio. Clear tira a midia de
    /// vez, o Deck volta ao estado vazio (sprint dos ajustes de Deck, 2026-08-09 — botao Clear).
    /// Sem efeito nenhum, sem excecao, se ja estiver vazio (Load nunca foi chamado, ou Clear ja foi
    /// chamado antes).
    /// </summary>
    public void Clear()
    {
        _mediaPlayer.Stop();
        _framePlayer.Stop();
        _framePlayerB.Stop();
        _audioCapture.Clear();

        _mediaPlayer.Media = null;
        _framePlayer.Media = null;
        _framePlayerB.Media = null;

        _currentMedia?.Dispose();
        _currentMedia = null;

        _currentFrameMedia?.Dispose();
        _currentFrameMedia = null;

        _currentFrameMediaB?.Dispose();
        _currentFrameMediaB = null;

        _currentFilePath = null;
        _seamlessSlotAIsActive = true;
        _seamlessStandbyArmedThisCycle = false;
        _standbyHasFrame = false;

        StateChanged?.Invoke(this, new EngineStateChangedEventArgs(EnginePlaybackState.Idle));
    }

    /// <summary>Inicia (ou retoma, se pausado) a reproducao da midia carregada. Sem efeito se nada foi carregado.</summary>
    public void Play()
    {
        if (_mediaPlayer.Media is null)
        {
            return;
        }

        var playSequence = ++_playSequence;

        _mediaPlayer.Play();

        // Sprint NDI-Loop-Seamless (2026-09-17): um Play "de verdade" (comando do operador) sempre
        // comeca do slot A, do jeito de sempre — reseta o estado do mecanismo antes de tocar, pra
        // nunca herdar uma troca de slot de uma reproducao anterior (ex.: Stop no meio de B ativo,
        // Play de novo tem que voltar a comecar do A, exatamente como sempre foi sem este mecanismo).
        _seamlessSlotAIsActive = true;
        _seamlessStandbyArmedThisCycle = false;
        _standbyHasFrame = false;

        // Sprint Áudio-Latência-Placa-Local-Sincronismo (2026-09-19): ver comentario completo em
        // _ndiSyncDelayMs. Desligado (0, o padrao) = inicia o par NDI imediatamente, exatamente como
        // sempre foi antes desta sprint.
        if (_ndiSyncDelayMs > 0)
        {
            LogAudioDeviceDiagnostic($"SINCRONISMO-NDI-ATRASANDO-INICIO sequencia={playSequence} atrasoMs={_ndiSyncDelayMs}");
            _ = StartNdiPairAfterSyncDelayAsync(playSequence);
        }
        else
        {
            StartNdiPairNow();
        }
    }

    /// <summary>Inicia o par NDI (video + audio, sempre os dois juntos) imediatamente — o mesmo par de chamadas que <see cref="Play"/> sempre fez, agora isolado num metodo pra poder ser chamado tanto na hora quanto depois do atraso de <see cref="_ndiSyncDelayMs"/> (ver <see cref="StartNdiPairAfterSyncDelayAsync"/>).</summary>
    private void StartNdiPairNow()
    {
        if (_framePlayer.Media is not null)
        {
            _framePlayer.Play();
        }

        _audioCapture.Play();
    }

    /// <summary>
    /// Sprint Áudio-Latência-Placa-Local-Sincronismo (2026-09-19): espera <see cref="_ndiSyncDelayMs"/>
    /// e so entao inicia o par NDI — ver comentario completo em <see cref="_ndiSyncDelayMs"/> pro
    /// racional. Confere <paramref name="playSequence"/> contra <see cref="_playSequence"/> depois da
    /// espera: se um Stop ou um Play novo aconteceu nesse meio tempo, o valor mudou e este metodo nao
    /// faz nada — o Play/Stop mais recente ja e responsavel pelo proprio par NDI (mesma logica de
    /// sentinela ja usada em <see cref="ReapplyAudioOutputDeviceAsync"/> pra <see cref="_pendingAudioDeviceId"/>).
    /// </summary>
    private async Task StartNdiPairAfterSyncDelayAsync(int playSequence)
    {
        try
        {
            await Task.Delay(_ndiSyncDelayMs).ConfigureAwait(false);
        }
        catch (Exception)
        {
            return;
        }

        if (_disposed || _playSequence != playSequence)
        {
            LogAudioDeviceDiagnostic($"SINCRONISMO-NDI-CANCELADO sequenciaPedida={playSequence} sequenciaAtual={_playSequence} disposed={_disposed}");
            return;
        }

        StartNdiPairNow();
        LogAudioDeviceDiagnostic($"SINCRONISMO-NDI-INICIADO sequencia={playSequence} atrasoMs={_ndiSyncDelayMs}");
    }

    /// <summary>
    /// Sprint Preview-Isolado (2026-08-15): toca a midia carregada SO no player de tela
    /// (<see cref="_mediaPlayer"/>) — deliberadamente NAO inicia o player de captura de video pro NDI
    /// (<see cref="_framePlayer"/>) nem o decodificador de audio pro NDI (<see cref="_audioCapture"/>).
    /// Usado exclusivamente pelo botao Preview de cada Deck: o operador confere um arquivo carregado
    /// sem que nada saia da maquina. Se o canal NDI deste Deck estiver "ao vivo" em algum switcher a
    /// jusante no momento, ele simplesmente continua mostrando o ultimo quadro real que tinha
    /// enviado antes (congelado) ate o proximo <see cref="Play"/> de verdade — efeito colateral
    /// aceito conscientemente (documentado/combinado com o usuario), preferivel a vazar o preview
    /// pro NDI. Sem efeito se nada foi carregado.
    /// </summary>
    public void PlayPreviewOnly()
    {
        if (_mediaPlayer.Media is null)
        {
            return;
        }

        // Sprint Áudio-Latência-Placa-Local-Sincronismo (2026-09-19): incrementa a mesma sequencia
        // de Play/Stop pra invalidar um inicio adiado do par NDI que porventura estivesse pendente
        // (ver StartNdiPairAfterSyncDelayAsync) — Preview nunca deve deixar o NDI comecar sozinho
        // depois, isso quebraria a garantia ja documentada desta funcao (nada sai pro NDI).
        _ = ++_playSequence;

        _mediaPlayer.Play();
    }

    /// <summary>
    /// Sprint NDI-Loop-Seamless (2026-09-17): qual dos dois players de captura esta entregando
    /// quadros pra <see cref="FrameReady"/> agora — <see cref="_framePlayer"/> (A) sempre, se
    /// <see cref="_seamlessLoopEnabled"/> estiver desligado (mesmo comportamento de sempre); com o
    /// modo ligado, o que <see cref="_seamlessSlotAIsActive"/> apontar. Usado por
    /// <see cref="Pause"/>/<see cref="Seek"/> pra nunca operar no player errado (ver comentario de
    /// classe de <see cref="_framePlayerB"/> pro motivo disso ser necessario).
    /// </summary>
    private MediaPlayer ActiveFramePlayer => (!_seamlessLoopEnabled || _seamlessSlotAIsActive) ? _framePlayer : _framePlayerB;

    /// <summary>O outro player de captura, o que nao esta ativo agora — ver <see cref="ActiveFramePlayer"/>.</summary>
    private MediaPlayer IdleFramePlayer => (!_seamlessLoopEnabled || _seamlessSlotAIsActive) ? _framePlayerB : _framePlayer;

    /// <summary>
    /// Sprint NDI-Loop-Seamless (2026-09-17): cancela um standby em preparo (se houver) — chamado por
    /// <see cref="Pause"/>/<see cref="Seek"/>, uma intervencao manual do operador no meio de uma
    /// reproducao continua. Um standby em preparo estava avancando sozinho, a partir do proprio 0,
    /// sem nenhuma relacao com uma pausa ou um novo ponto pedido por Seek — nao tem como "adiantar"
    /// ele pro lugar certo de forma segura, entao a escolha mais simples e honesta e descartar: a
    /// PROXIMA volta de loop cai no fallback de reabertura normal (mesmo custo de sempre, so uma
    /// vez), e o modo sem-corte volta a armar um novo standby sozinho a partir dai, sem intervencao
    /// nenhuma.
    /// </summary>
    private void CancelSeamlessStandby()
    {
        if (!_seamlessStandbyArmedThisCycle)
        {
            return;
        }

        IdleFramePlayer.Stop();
        _seamlessStandbyArmedThisCycle = false;
        _standbyHasFrame = false;
    }

    /// <summary>Congela a reproducao no ponto atual. Sem efeito se nao estiver tocando.</summary>
    public void Pause()
    {
        if (_mediaPlayer.IsPlaying)
        {
            _mediaPlayer.Pause();
        }

        var activeFramePlayer = ActiveFramePlayer;
        if (activeFramePlayer.IsPlaying)
        {
            activeFramePlayer.Pause();
        }

        CancelSeamlessStandby();

        _audioCapture.Pause();
    }

    /// <summary>Interrompe a reproducao — o proximo <see cref="Play"/> recomeca do inicio.</summary>
    public void Stop()
    {
        // Sprint Áudio-Latência-Placa-Local-Sincronismo (2026-09-19): invalida qualquer inicio
        // adiado do par NDI que estivesse pendente (ver StartNdiPairAfterSyncDelayAsync) — um Stop
        // no meio da espera do atraso nao pode deixar o par NDI comecar sozinho depois.
        _ = ++_playSequence;

        _mediaPlayer.Stop();
        _framePlayer.Stop();

        // Sprint NDI-Loop-Seamless (2026-09-17): descarta qualquer standby (B) que estivesse
        // armado/tocando — o proximo Play() sempre recomeca do slot A do zero (ver comentario de
        // Play()), entao nao ha motivo pra manter a Media de B viva entre um Stop e o Play seguinte.
        _framePlayerB.Stop();
        _framePlayerB.Media = null;
        _currentFrameMediaB?.Dispose();
        _currentFrameMediaB = null;
        _seamlessSlotAIsActive = true;
        _seamlessStandbyArmedThisCycle = false;
        _standbyHasFrame = false;

        _audioCapture.Stop();
    }

    /// <summary>Move a posicao de reproducao. Sem efeito se nada foi carregado ou a midia nao for buscavel.</summary>
    public void Seek(TimeSpan position)
    {
        if (_mediaPlayer.Media is null)
        {
            return;
        }

        _mediaPlayer.Time = (long)position.TotalMilliseconds;

        var activeFramePlayer = ActiveFramePlayer;
        if (activeFramePlayer.Media is not null)
        {
            activeFramePlayer.Time = (long)position.TotalMilliseconds;
        }

        CancelSeamlessStandby();

        _audioCapture.Seek(position);
    }

    /// <summary>
    /// Sprint Áudio-Dispositivo-Deck (2026-08-11): direciona o áudio LOCAL do player de tela (o que
    /// o operador ouve monitorando este Deck) pra um dispositivo específico do Windows — nulo/vazio
    /// não faz nada (LibVLC já usa o dispositivo padrão do sistema sozinho enquanto isto nunca for
    /// chamado, mesmo comportamento de sempre). Só afeta <see cref="_mediaPlayer"/> (o player de
    /// tela): o áudio pro NDI (<see cref="NdiAudioCapture"/>, Sprint NDI-audio-NAudio) decodifica o
    /// arquivo de forma inteiramente independente, via NAudio — não toca em nenhum dispositivo de
    /// saída do Windows, então isto nunca afeta o que chega no NDI remoto.
    ///
    /// Seguro chamar a qualquer momento, inclusive durante reprodução — <c>MediaPlayer.SetOutputDevice</c>
    /// muda o dispositivo imediatamente (documentação oficial da libvlc, confirmada nesta sprint).
    ///
    /// LIMITAÇÃO HONESTA: a documentação oficial da libvlc não garante um jeito de reverter pro
    /// dispositivo padrão do sistema depois de escolher um específico (só descreve como trocar PRA
    /// um dispositivo, nunca como "desfazer") — por isso, depois de escolher um dispositivo
    /// específico uma vez, voltar pra "Padrão do sistema" na tela de Configurações só tem efeito de
    /// verdade reiniciando o programa. Documentado aqui em vez de arriscar um comportamento de
    /// "reset" não confirmado contra a API real.
    ///
    /// Sprint Áudio-Dispositivo-Fix (2026-09-13): tentativa inicial foi so trocar a ORDEM de quem
    /// chama isto (DeckViewModel passou a chamar depois de Play(), nao antes) — hipotese de que o
    /// LibVLC so cria seu objeto interno de saida de audio ("aout") quando a reproducao comeca, entao
    /// chamar antes disso arriscava a escolha ser perdida. Funcionou no PRIMEIRO Play, mas o usuario
    /// confirmou que um Play SEGUINTE (depois de um Stop) voltava a cair no dispositivo padrao —
    /// prova de que o aout tambem e destruido/recriado no Stop, e a mera troca de ordem so cobria o
    /// primeiro ciclo por sorte de tempo (nao uma garantia real).
    ///
    /// Sprint Áudio-Dispositivo-Fix-2 (2026-09-13, mesmo dia): corrigido de forma robusta guardando o
    /// pedido em <see cref="_pendingAudioDeviceId"/> e reaplicando a cada evento
    /// <see cref="MediaPlayer.Playing"/> (ver construtor) — ou seja, a cada vez que o LibVLC CONFIRMA
    /// que a reproducao comecou de verdade, nao importa se e o primeiro Play da instancia ou um Play
    /// depois de um Stop. Continua tambem aplicando imediatamente aqui quando ja estiver tocando
    /// (troca de dispositivo em Configuracoes durante a reproducao, comportamento de sempre).
    ///
    /// Sprint Áudio-Dispositivo-Fix-3 (2026-09-13, mesmo dia): a chamada dentro do evento Playing
    /// precisou sair da thread do LibVLC (Task.Run) — chama-la sincronamente ali deadlockava a
    /// aplicacao inteira. Ver comentario do evento no construtor.
    ///
    /// Sprint Áudio-Dispositivo-Fix-4 (2026-09-13, mesmo dia): a chamada dentro do evento Playing
    /// passou de uma tentativa unica pra <see cref="ReapplyAudioOutputDeviceAsync"/> (varias
    /// tentativas ao longo de ~5s) — pesquisa externa confirmou que o libvlc no Windows pode demorar
    /// ate alguns segundos pra o dispositivo de audio "pegar" depois do Play. Ver comentario do
    /// evento Playing no construtor pro diagnostico completo.
    /// </summary>
    public void SetAudioOutputDevice(string? deviceId)
    {
        // Sprint Áudio-Dispositivo-Diagnóstico-2 (2026-09-19): log incondicional, ANTES do guard de
        // nulo/vazio abaixo — a v1.0.41 só logava depois desse guard, então um deviceId nulo/vazio
        // (Deck sem placa configurada, ou testado com "Padrão do sistema") não deixava rastro nenhum
        // no log. Esta linha cobre esse caso também.
        LogAudioDeviceDiagnostic($"SETAUDIOOUTPUTDEVICE-CHAMADO device={deviceId ?? "(nulo/vazio)"}");

        if (string.IsNullOrEmpty(deviceId))
        {
            return;
        }

        _pendingAudioDeviceId = deviceId;

        if (_mediaPlayer.IsPlaying)
        {
            LogAudioDeviceDiagnostic($"SET-IMEDIATO device={deviceId} (ja estava tocando)");
            _mediaPlayer.SetOutputDevice(deviceId);
        }
        else
        {
            LogAudioDeviceDiagnostic($"SET-PENDENTE device={deviceId} (ainda nao tocando, aplica no evento Playing)");
        }
    }

    /// <summary>
    /// Sprint Áudio-Dispositivo-Diagnóstico-5 (2026-09-19): se <see cref="_audioDeviceViaOpcaoMediaEnabled"/>
    /// estiver ligada e já soubermos qual dispositivo o Deck quer (<see cref="_pendingAudioDeviceId"/>
    /// de um Play anterior nesta sessao), acrescenta a opcao <c>mmdevice-audio-device</c> direto na
    /// <see cref="Media"/> — lida pelo proprio modulo de saida de audio do libvlc no Windows toda vez
    /// que ele abre (pesquisa externa, codigo-fonte do modulo mmdevice do VLC), entao se isto realmente
    /// funcionar o dispositivo certo passa a ser usado desde o PRIMEIRO quadro de audio, sem nenhuma
    /// chamada de SetOutputDevice depois do Play. Nunca lanca excecao — na pior hipotese (opcao
    /// ignorada, ou formato de id incompativel) o comportamento cai exatamente no mecanismo de reforco
    /// de sempre (inalterado, continua rodando por baixo de qualquer forma).
    /// </summary>
    private void SeedAudioDeviceOptionNaMediaSeAtivo(Media media)
    {
        // Sprint Áudio-Dispositivo-Diagnóstico-6: qualquer caminho de saida daqui pra frente que NAO
        // seja "aplicado com sucesso pro deviceId atual" precisa limpar o estado anterior — senao um
        // Load() sem dispositivo pendente (ex.: reiniciar o programa) poderia herdar por engano o
        // _audioDeviceSeedApplied=true de um arquivo anterior e pular o reforco indevidamente.
        _audioDeviceSeedApplied = false;
        _audioDeviceSeedDeviceId = null;

        if (!_audioDeviceViaOpcaoMediaEnabled)
        {
            return;
        }

        var deviceId = _pendingAudioDeviceId;
        if (string.IsNullOrEmpty(deviceId))
        {
            LogAudioDeviceDiagnostic("OPCAO-MEDIA-PULADA (nenhum dispositivo pendente conhecido ainda nesta sessao)");
            return;
        }

        try
        {
            media.AddOption($":mmdevice-audio-device={deviceId}");
            _audioDeviceSeedApplied = true;
            _audioDeviceSeedDeviceId = deviceId;
            LogAudioDeviceDiagnostic($"OPCAO-MEDIA-APLICADA device={deviceId}");
        }
        catch (Exception ex)
        {
            LogAudioDeviceDiagnostic($"OPCAO-MEDIA-ERRO device={deviceId} ex={ex.Message}");
            // Ignorado de proposito — isto e so uma tentativa extra; o mecanismo de reforco de sempre
            // continua sendo a rede de seguranca real.
        }
    }

    /// <summary>
    /// Sprint Áudio-Dispositivo-Diagnostico (2026-09-19): log temporario de investigacao, na MESMA
    /// pasta e no MESMO padrao de <see cref="NdiAudioCapture.LogDiagnostic"/> (arquivo separado, so
    /// pra nao misturar com o log de audio-buffer do NDI). Objetivo: descobrir se as picotadas de
    /// ~2-3s relatadas com a placa Realtek onboard sao causadas pelas proprias chamadas repetidas de
    /// <c>SetOutputDevice</c> deste Engine (hipotese atual, ver comentario de
    /// <see cref="ReapplyAudioOutputDeviceAsync"/>) — e nao por um "assentamento" generico do
    /// Windows/driver, hipotese da Fix-5 (v1.0.40) que o proprio usuario invalidou na pratica (mais
    /// tentativas nao mudou nada, e o mesmo video/mesma placa fora do Cartucheira toca limpo). Um log
    /// de diagnostico nunca pode derrubar a reproducao de audio de verdade — por isso o try/catch
    /// silencioso, igual ao de NdiAudioCapture.
    /// </summary>
    private static void LogAudioDeviceDiagnostic(string message)
    {
        try
        {
            var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CartucheiraVideo");
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, "audio-dispositivo-diagnostico.log");
            var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}{Environment.NewLine}";
            File.AppendAllText(path, line);
        }
        catch (Exception)
        {
            // Falha honesta — diagnostico nunca deve afetar a reproducao real.
        }
    }

    /// <summary>
    /// Sprint Áudio-Livewire-Duplo (2026-09-15): liga/desliga o mudo do áudio LOCAL deste
    /// <see cref="_mediaPlayer"/> (o que sai pelos alto-falantes/placa da própria máquina, monitor
    /// de tela) — usado por <see cref="DeckViewModel.SetAudioOutputDevice"/> quando o Deck está
    /// configurado pra mandar o Play por uma Saída Livewire nativa em vez de uma placa Windows.
    ///
    /// CAUSA RAIZ do bug reportado ("com placas diferentes configuradas, sai nas duas ao mesmo
    /// tempo"): uma Saída Livewire nativa (prefixo <c>"livewire:"</c>) NUNCA chega até este Engine —
    /// <see cref="DeckViewModel.SetAudioOutputDevice"/> intercepta esse caso e so abre/atualiza o
    /// <see cref="LivewireSender"/> correspondente, de propósito (ver comentário lá: mandar um id de
    /// dispositivo Livewire pro LibVLC seria um comportamento não documentado/arriscado). Sem efeito
    /// colateral aparente à primeira vista — MAS isso significa que <see cref="_mediaPlayer"/> nunca
    /// fica sabendo que devia ficar quieto: ele continua tocando, sem nenhum SetOutputDevice novo,
    /// na placa Windows que já estava configurada antes (ou a padrão do sistema, se nunca configurou
    /// nenhuma) — ENQUANTO, ao mesmo tempo, o áudio de verdade (via <see cref="_audioCapture"/>,
    /// decodificador NAudio 100% independente) sai pela Livewire. Resultado: o operador ouve a MESMA
    /// reprodução duas vezes ao mesmo tempo, pelos alto-falantes locais E pela mesa/console via rede
    /// — nenhuma das duas "placas" está de fato errada, o problema é as DUAS tocarem juntas quando só
    /// uma deveria (a Livewire, que é a que o operador escolheu de verdade pro Play).
    ///
    /// Mutar aqui É seguro e não afeta o áudio de verdade: <see cref="_audioCapture"/> decodifica o
    /// arquivo de forma inteiramente independente do volume/mudo deste <see cref="_mediaPlayer"/> (ver
    /// comentário de classe) — só o monitor local fica quieto, o que vai pra Livewire (ou pro NDI)
    /// nunca passa por aqui.
    /// </summary>
    public void SetLocalAudioMuted(bool muted)
    {
        _mediaPlayer.Mute = muted;
    }

    /// <summary>
    /// Sprint Áudio-Dispositivo-Fix-4 (2026-09-13): reaplica <paramref name="deviceId"/> varias vezes
    /// ao longo de ~5 segundos depois que <see cref="MediaPlayer.Playing"/> dispara (ver construtor) —
    /// nunca bloqueando nenhuma thread (<see cref="Task.Delay(int)"/>, nao <c>Thread.Sleep</c>).
    /// Necessario porque o subsistema de audio do libvlc no Windows pode levar ate alguns segundos
    /// pra "assentar" depois que a reproducao comeca (evidencia externa: issue #1119 do vlcj, outro
    /// binding sobre o MESMO libvlc nativo, relatando o mesmo sintoma — device ignorado se escolhido
    /// nos primeiros instantes, "funciona se escolhido alguns segundos depois de iniciar"). Chamar
    /// SetOutputDevice repetidas vezes e documentado como seguro pela libvlc, entao o custo desta
    /// redundancia e desprezivel. Para de tentar cedo se o pedido mudou (usuario trocou de novo) ou
    /// se a reproducao parou nesse meio tempo — nunca aplica um valor desatualizado.
    ///
    /// Sprint Áudio-Livewire-Duplo-Fix-2 (2026-09-15) — TENTATIVA REVERTIDA: reduzir de 5 tentativas
    /// (100/300/700/1500/2500ms) pra 2 (150/2000ms) diminuiu o engasgo do Preview (causa raiz real:
    /// cada chamada de SetOutputDevice numa reproducao ja tocando reabre o fluxo de audio e causa um
    /// corte audivel), MAS o usuario reportou que o Preview passou a nao tocar NENHUM audio — a placa
    /// configurada (uma placa Windows real) aparentemente so "pegava" de verdade numa das tentativas
    /// REMOVIDAS (700/1500/2500ms), que aqui nunca tinha sido isolada/confirmada individualmente antes
    /// de cortar. Reduzir o numero de chamadas sem confirmar QUAL tentativa era a que realmente
    /// aplicava o dispositivo pra esta placa especifica se mostrou arriscado demais pra um ambiente de
    /// transmissao ao vivo — revertido pras 5 tentativas originais (prioridade Estabilidade > engasgo
    /// perceptivel, ate se achar uma correcao que resolva os dois ao mesmo tempo sem risco de silencio
    /// total).
    ///
    /// Sprint Áudio-Dispositivo-Fix-5 (2026-09-18): usuario reportou, numa placa Windows real
    /// (Realtek onboard, confirmada via <c>Get-CimInstance Win32_SoundDevice</c>), ~2-3 segundos de
    /// silencio total no monitor local antes de estabilizar sozinho — bem na borda da janela de
    /// ~2,5s que as 5 tentativas originais cobrem (a ultima e aos 2500ms; se a placa "assenta" logo
    /// depois disso, nenhuma tentativa pega o momento certo). Acrescentadas DUAS tentativas a mais,
    /// mais tarde (4000ms/6000ms) — sO ACRESCENTADAS, nenhuma das 5 originais foi tocada/removida,
    /// exatamente a licao da Fix-2 acima (reduzir tentativas e o que causou a regressao grave;
    /// adicionar mais tentativas mais tarde nao muda nenhum comportamento ja existente, so aumenta a
    /// chance de acertar o momento em maquinas/placas mais lentas pra assentar).
    ///
    /// Sprint Áudio-Dispositivo-Diagnostico-3 (2026-09-19) — BUG REAL ENCONTRADO nos logs da
    /// Diagnostico/Diagnostico-2: <c>delaysMs</c> SEMPRE foi documentado (nos comentarios acima, desde
    /// a Fix-4) como "tentativa aos Xms depois do evento Playing" — mas a implementacao com
    /// <c>await Task.Delay(delayMs)</c> DENTRO de um <c>foreach</c> sequencial soma os atrasos, nao os
    /// mede a partir do Playing. O log real confirmou: com <c>{100,300,700,1500,2500,4000,6000}</c>, as
    /// tentativas de fato disparam em 100/400/1100/2600/5100/9100/15100ms depois do Playing — a ULTIMA
    /// tentativa (v1.0.40) acontecia mais de 15 SEGUNDOS depois do Play, nao 6s como todo o raciocinio
    /// da Fix-5 assumiu. Isso muda a analise: a v1.0.40 nao "nao fez diferenca" por a placa nao
    /// precisar de mais tempo — ela ficou reabrindo o fluxo de audio (cada SetOutputDevice numa
    /// reproducao ja tocando causa um corte, ver Fix-2 acima) por TRES VEZES MAIS TEMPO do que o
    /// pretendido, o que e consistente com o usuario relatar que a placa Realtek simplesmente NUNCA
    /// emitiu audio num dos testes (log mostrou o proprio <c>MediaPlayer.IsPlaying</c> virando falso no
    /// meio da sequencia de reforco, antes de completar).
    ///
    /// CORRIGIDO aqui: cada elemento de <c>delaysMs</c> agora e tratado como o instante ABSOLUTO
    /// (desde o Playing) em que aquela tentativa deve disparar, esperando so a DIFERENCA pro anterior
    /// — exatamente o que os comentarios desde 2026-09-13 sempre disseram que o codigo fazia. NENHUM
    /// valor da lista foi alterado e NENHUMA tentativa foi removida ou acrescentada (continuam sendo
    /// exatamente as mesmas 7, nos mesmos instantes-alvo já documentados) — isto e uma correcao de bug
    /// de calculo de tempo, nao mais um ajuste de quantidade/intervalo. Efeito esperado: a ultima
    /// tentativa passa a disparar aos 6s reais (nao 15,1s), reduzindo o tempo total em que o Cartucheira
    /// fica reabrindo o fluxo de audio da placa. Log de cada tentativa agora tambem registra o tempo
    /// decorrido desde o Playing, pra confirmar isso no proximo teste.
    ///
    /// Sprint Áudio-Dispositivo-Diagnóstico-4 (2026-09-19): mesmo com a correção de tempo acima, o
    /// usuário confirmou (log real) que a sequência de reforço terminava certinho aos ~6s, mas AINDA
    /// picotava durante boa parte dessa janela — ou seja, a causa raiz (cada SetOutputDevice numa
    /// reprodução já tocando reabre o fluxo e corta o áudio, ver Fix-2 acima) continua batendo, só que
    /// numa janela menor. Um player externo, que aplica a placa uma única vez, nunca engasga nesse
    /// hardware — sugerindo que talvez nem precisasse de mais de uma tentativa aqui. Testar isso direto
    /// (reduzir de verdade) é arriscado (a Fix-2 já mostrou que ISSO pode causar silêncio total numa
    /// placa diferente) — por isso vira uma opção opcional, desligada por padrão
    /// (<see cref="_audioDeviceUmaTentativaEnabled"/>), só pra confirmar a hipótese com dado real antes
    /// de considerar mudar o padrão pra todo mundo.
    /// </summary>
    private async Task ReapplyAudioOutputDeviceAsync(string deviceId)
    {
        // Instantes-alvo, em ms DESDE O EVENTO Playing (nao intervalos entre tentativas — ver bug
        // documentado acima). Mesmos 7 valores de sempre, so a forma de esperar ate cada um mudou.
        // Sprint Áudio-Dispositivo-Diagnóstico-4: com _audioDeviceUmaTentativaEnabled ligada (opcional,
        // desligada por padrao), so a primeira tentativa (100ms) roda - nenhuma das outras 6.
        int[] targetOffsetsMs = _audioDeviceUmaTentativaEnabled
            ? new[] { 100 }
            : new[] { 100, 300, 700, 1500, 2500, 4000, 6000 };
        var startedAt = DateTime.UtcNow;

        LogAudioDeviceDiagnostic($"REAPPLY-INICIO device={deviceId} tentativas={targetOffsetsMs.Length}");

        var previousOffsetMs = 0;
        foreach (var targetOffsetMs in targetOffsetsMs)
        {
            var waitMs = targetOffsetMs - previousOffsetMs;
            previousOffsetMs = targetOffsetMs;

            if (waitMs > 0)
            {
                await Task.Delay(waitMs).ConfigureAwait(false);
            }

            var elapsedMs = (int)(DateTime.UtcNow - startedAt).TotalMilliseconds;

            if (_disposed || _pendingAudioDeviceId != deviceId || !_mediaPlayer.IsPlaying)
            {
                LogAudioDeviceDiagnostic($"REAPPLY-CANCELADO device={deviceId} alvoMs={targetOffsetMs} decorridoMs={elapsedMs} disposed={_disposed} pendingMudou={_pendingAudioDeviceId != deviceId} isPlaying={_mediaPlayer.IsPlaying}");
                return;
            }

            try
            {
                LogAudioDeviceDiagnostic($"REAPPLY-TENTATIVA device={deviceId} alvoMs={targetOffsetMs} decorridoMs={elapsedMs}");
                _mediaPlayer.SetOutputDevice(deviceId);
                LogAudioDeviceDiagnostic($"REAPPLY-OK device={deviceId} alvoMs={targetOffsetMs} decorridoMs={elapsedMs}");
            }
            catch (Exception ex)
            {
                LogAudioDeviceDiagnostic($"REAPPLY-ERRO device={deviceId} alvoMs={targetOffsetMs} decorridoMs={elapsedMs} ex={ex.Message}");
                // Ignorado de proposito — isto e so uma tentativa de reforco; nunca deve derrubar a
                // reproducao real por causa de uma falha aqui.
            }
        }

        LogAudioDeviceDiagnostic($"REAPPLY-FIM device={deviceId} (todas as {targetOffsetsMs.Length} tentativas concluidas)");
    }

    private void OnTimeChanged(object? sender, MediaPlayerTimeChangedEventArgs e)
    {
        var lengthMs = _mediaPlayer.Length;
        var duration = lengthMs > 0 ? TimeSpan.FromMilliseconds(lengthMs) : (TimeSpan?)null;
        PositionChanged?.Invoke(this, new EnginePositionChangedEventArgs(TimeSpan.FromMilliseconds(e.Time), duration));

        // Sprint NDI-audio-video-clock-lock (2026-09-12): ate esta sprint, alimentava a correcao de
        // deriva do NdiAudioCapture com ESTA posicao (a do player de PREVIEW, _mediaPlayer) — mas
        // _mediaPlayer e um decodificador TOTALMENTE INDEPENDENTE do que realmente decodifica o video
        // que vai pro NDI (_framePlayer). A correcao de deriva nunca mirou no alvo certo. Agora quem
        // alimenta SyncToVideoPosition e OnFrameDisplay (a cada quadro REAL enviado pro NDI, nao mais
        // so quando o preview reporta TimeChanged) — ver comentario la e o comentario de classe de
        // NdiAudioCapture pro diagnostico completo desta sprint. PositionChanged (usado so pra UI —
        // barra de progresso/tempo do Deck) continua vindo do preview normalmente, sem nenhuma mudanca:
        // é exatamente o que a interface deve mostrar (o que o operador ve na tela).
    }

    /// <summary>
    /// EndReached e disparado numa thread interna do LibVLC — chamar Play/atribuir Media
    /// diretamente aqui trava o player (limitacao conhecida e documentada do LibVLC/LibVLCSharp).
    /// Por isso o reinicio do loop e despachado pra uma thread separada.
    ///
    /// Sprint NDI-Loop-Restart-Rapido (v1.0.37, 2026-09-17) — REVERTIDA na v1.0.38: chegou a trocar
    /// isto por Play() direto sem reatribuir Media (pra evitar reabrir o arquivo a cada loop, ver
    /// sprint-2026-09-17-loop-restart-diagnostico.md/-rapido.md), mas o usuario confirmou que o loop
    /// PAROU DE FUNCIONAR de verdade com essa mudanca (nao apenas mais lento) — ver
    /// sprint-2026-09-17-loop-restart-revert.md. Confirma que reatribuir Media e NECESSARIO pra este
    /// LibVLCSharp/LibVLC (3.9.0/3.0.20) conseguir retomar depois do estado Ended; Play() sozinho nao
    /// basta. Voltado ao comportamento original, conhecido e estavel.
    /// </summary>
    private void OnEndReached(object? sender, EventArgs e)
    {
        if (_loopEnabled && _currentMedia is not null)
        {
            Task.Run(() =>
            {
                _mediaPlayer.Media = _currentMedia;
                _mediaPlayer.Play();
            });
            return;
        }

        StateChanged?.Invoke(this, new EngineStateChangedEventArgs(EnginePlaybackState.Ended));
        MediaEnded?.Invoke(this, EventArgs.Empty);
    }

    private void OnEncounteredError(object? sender, EventArgs e)
    {
        StateChanged?.Invoke(this, new EngineStateChangedEventArgs(EnginePlaybackState.Error));
        ErrorOccurred?.Invoke(this, "O LibVLC reportou um erro ao reproduzir esta midia.");
    }

    /// <summary>
    /// Fim de arquivo do player de CAPTURA — deliberadamente NAO dispara StateChanged/MediaEnded
    /// (o player principal, via <see cref="OnEndReached"/>, ja e a unica fonte de verdade do estado
    /// do Deck). Este handler so cuida do proprio reinicio em loop deste player, pro sinal NDI
    /// continuar em loop junto com a preview local.
    ///
    /// Sprint NDI-Loop-Restart-Rapido (v1.0.37, 2026-09-17) — REVERTIDA na v1.0.38: mesma troca
    /// tentada em <see cref="OnEndReached"/> (Play() direto sem reatribuir Media, pra evitar reabrir
    /// o arquivo a cada loop). O usuario confirmou que o loop PAROU DE FUNCIONAR com essa mudanca —
    /// ver sprint-2026-09-17-loop-restart-revert.md. Voltado a reatribuir Media antes de Play(),
    /// unico jeito confirmado de funcionar nesta versao do LibVLCSharp/LibVLC.
    ///
    /// Sprint NDI-Loop-Seamless (2026-09-17): o atraso de ~100-160ms por loop (causa da rajada
    /// residual do TryCorrectDrift, ver diagnostico e confirmacao por log dessa mesma data) agora TEM
    /// uma solucao real, opcional (<see cref="_seamlessLoopEnabled"/>, desligada por padrao) — ver
    /// <see cref="HandleFrameSlotEndReached"/> pro mecanismo completo. Com o modo desligado (padrao),
    /// este handler continua fazendo EXATAMENTE o que fazia antes desta sprint.
    /// </summary>
    private void OnFramePlayerEndReached(object? sender, EventArgs e)
    {
        HandleFrameSlotEndReached(isSlotA: true);
    }

    /// <summary>
    /// Sprint NDI-Loop-Seamless (2026-09-17): fim de arquivo do SEGUNDO player de captura
    /// (<see cref="_framePlayerB"/>) — so chega a disparar de verdade quando <see cref="_seamlessLoopEnabled"/>
    /// estiver ligado (com o modo desligado, B nunca recebe Media/nunca toca, ver
    /// <see cref="ArmStandbySlotB"/>). Mesmo tratamento simetrico de <see cref="OnFramePlayerEndReached"/>,
    /// so que do ponto de vista do slot B — ver <see cref="HandleFrameSlotEndReached"/>.
    /// </summary>
    private void OnFramePlayerBEndReached(object? sender, EventArgs e)
    {
        HandleFrameSlotEndReached(isSlotA: false);
    }

    /// <summary>
    /// Sprint NDI-Loop-Seamless (2026-09-17): logica compartilhada de fim-de-arquivo pros dois slots
    /// de captura — ver comentario completo de classe de <see cref="_framePlayerB"/> pro desenho
    /// inteiro do mecanismo. Chamado direto de dentro do evento EndReached do LibVLC (thread interna
    /// do LibVLC) — qualquer chamada de volta pra API do LibVLC daqui (Play/atribuir Media) PRECISA
    /// continuar despachada pra outra thread via Task.Run (ver <see cref="RestartSlotByReopening"/>),
    /// exatamente pelo mesmo motivo documentado ha muito tempo nesta classe; so mexer em campos
    /// gerenciados proprios (os bools de estado do modo sem-corte) e seguro fazer direto aqui.
    /// </summary>
    private void HandleFrameSlotEndReached(bool isSlotA)
    {
        if (!_loopEnabled)
        {
            return;
        }

        var thisSlotWasActive = isSlotA == _seamlessSlotAIsActive;
        if (!thisSlotWasActive)
        {
            // O slot que terminou nao era o ativo agora — nao deveria acontecer em uso normal (um
            // standby so toca por ate SeamlessLoopLeadTimeMs antes de ser promovido, bem menos que
            // uma volta inteira), mas cobrir por seguranca: sem nenhuma acao, ele so fica parado ate
            // a proxima vez que for armado como standby de novo.
            return;
        }

        if (!_seamlessLoopEnabled)
        {
            // Comportamento EXATO da v1.0.38 (revert), inalterado.
            RestartSlotByReopening(isSlotA);
            return;
        }

        var standbyReady = _seamlessStandbyArmedThisCycle && _standbyHasFrame;
        if (standbyReady)
        {
            // Handoff sem reabrir nada agora: o outro slot ja esta tocando de verdade ha ate
            // SeamlessLoopLeadTimeMs, com pelo menos um quadro real confirmado — vira o ativo.
            _seamlessSlotAIsActive = !isSlotA;
            _seamlessStandbyArmedThisCycle = false;
            _standbyHasFrame = false;
        }
        else
        {
            // Standby nao ficou pronto a tempo (duracao ainda desconhecida, primeira volta desde o
            // Load, ou uma Pause/Seek cancelou o armamento no meio do caminho) — cai no MESMO
            // fallback de sempre so nesta volta; a proxima volta tenta armar o standby de novo
            // normalmente, sem intervencao nenhuma.
            RestartSlotByReopening(isSlotA);
        }
    }

    /// <summary>Reabre o mesmo player (A ou B) do jeito antigo, ja comprovado — unico caminho usado quando o modo sem-corte esta desligado, e o fallback de seguranca quando ligado mas o standby nao ficou pronto a tempo.</summary>
    private void RestartSlotByReopening(bool isSlotA)
    {
        if (isSlotA)
        {
            if (_currentFrameMedia is null)
            {
                return;
            }

            Task.Run(() =>
            {
                _framePlayer.Media = _currentFrameMedia;
                _framePlayer.Play();
            });
        }
        else
        {
            if (_currentFrameMediaB is null)
            {
                return;
            }

            Task.Run(() =>
            {
                _framePlayerB.Media = _currentFrameMediaB;
                _framePlayerB.Play();
            });
        }
    }

    /// <summary>
    /// Sprint NDI-Loop-Seamless (2026-09-17): chamado por <see cref="PollFramePlayerPosition"/> (timer
    /// do ThreadPool, NUNCA a thread do LibVLC — seguro chamar Play/atribuir Media direto daqui, sem
    /// precisar de Task.Run) quando o slot ativo estiver a menos de <see cref="SeamlessLoopLeadTimeMs"/>
    /// do proprio fim. Duracao desconhecida (arquivo estranho, ou o parse local nao terminou a tempo)
    /// significa sem gatilho seguro — sem armar nada, cai no fallback de reabertura de sempre nesta
    /// volta, tentando de novo na proxima (comportamento honesto, nunca arrisca um estado
    /// inconsistente por causa de um valor que nao da pra confiar).
    /// </summary>
    private void TryArmSeamlessStandby(long activeTimeMs)
    {
        var activeMedia = _seamlessSlotAIsActive ? _currentFrameMedia : _currentFrameMediaB;
        var durationMs = activeMedia?.Duration ?? -1;
        if (durationMs <= 0)
        {
            return;
        }

        var remainingMs = durationMs - activeTimeMs;
        if (remainingMs > SeamlessLoopLeadTimeMs || remainingMs < 0)
        {
            return;
        }

        _seamlessStandbyArmedThisCycle = true;
        _standbyHasFrame = false;

        if (_seamlessSlotAIsActive)
        {
            ArmStandbySlotB();
        }
        else
        {
            ArmStandbySlotA();
        }
    }

    /// <summary>Recria a Media do slot A do zero e manda tocar em paralelo, como standby — ver <see cref="TryArmSeamlessStandby"/>. Usado quando B esta ativo e A e quem precisa ser preparado pra proxima troca.</summary>
    private void ArmStandbySlotA()
    {
        if (_currentFilePath is null)
        {
            _seamlessStandbyArmedThisCycle = false;
            return;
        }

        try
        {
            var previous = _currentFrameMedia;
            _currentFrameMedia = new Media(_libVlc, _currentFilePath, FromType.FromPath, ":no-audio");
            _framePlayer.Media = _currentFrameMedia;
            _framePlayer.Play();
            previous?.Dispose();
        }
        catch (Exception)
        {
            // Falha ao armar o standby (arquivo removido/trocado no meio do caminho, etc.) — sem
            // standby pronto, o proximo EndReached do slot ativo cai no fallback de reabertura
            // normal, mesmo comportamento de sempre, sem regressao.
            _seamlessStandbyArmedThisCycle = false;
        }
    }

    /// <summary>Espelho de <see cref="ArmStandbySlotA"/> pro slot B — ver comentario la.</summary>
    private void ArmStandbySlotB()
    {
        if (_currentFilePath is null)
        {
            _seamlessStandbyArmedThisCycle = false;
            return;
        }

        try
        {
            var previous = _currentFrameMediaB;
            _currentFrameMediaB = new Media(_libVlc, _currentFilePath, FromType.FromPath, ":no-audio");
            _framePlayerB.Media = _currentFrameMediaB;
            _framePlayerB.Play();
            previous?.Dispose();
        }
        catch (Exception)
        {
            _seamlessStandbyArmedThisCycle = false;
        }
    }

    /// <summary>
    /// Callback de lock do LibVLC (thread do LibVLC) — chamado antes de decodificar cada quadro no
    /// buffer. Escreve o endereco de escrita em <paramref name="planes"/> (saida) e retorna
    /// IntPtr.Zero como id opaco de "picture" (nao usado por este projeto).
    ///
    /// Sprint Deck-Aspect-Ratio (2026-08-11): o endereco NAO e mais sempre o inicio do buffer — e o
    /// inicio + o deslocamento (<see cref="_frameOffsetXBytes"/>/<see cref="_frameOffsetYBytes"/>)
    /// calculado por <see cref="ConfigureFrameFormat"/>, pra centralizar o retangulo proporcional
    /// dentro do canvas fixo 1920x1080. O LibVLC escreve exatamente innerWidth x innerHeight pixels a
    /// partir desse ponto, respeitando o pitch do canvas inteiro entre linhas (SetVideoFormat pedido
    /// com innerWidth/innerHeight mas pitch=FrameStrideBytes) — as bordas ao redor (preenchidas de
    /// preto por FillFrameBufferOpaqueBlack) nunca sao tocadas pelo LibVLC, ficam pretas sozinhas.
    ///
    /// Sprint NDI-video-async (2026-09-11): escreve sempre no buffer ATIVO
    /// (<see cref="_frameBuffers"/>[<see cref="_activeFrameBufferIndex"/>]) — o indice so muda dentro
    /// de <see cref="OnFrameDisplay"/>, entao este metodo sempre ve o mesmo indice que estava ativo
    /// desde o ultimo display, nunca o buffer que acabou de ser entregue pro NdiSender.
    /// </summary>
    private IntPtr OnFrameLock(IntPtr opaque, IntPtr planes)
    {
        var target = _frameBuffers[_activeFrameBufferIndex] + _frameOffsetYBytes + _frameOffsetXBytes;
        Marshal.WriteIntPtr(planes, target);
        return IntPtr.Zero;
    }

    /// <summary>Espelho de <see cref="OnFrameLock"/> pro segundo player de captura (<see cref="_framePlayerB"/>) — mesma geometria compartilhada (<see cref="_frameOffsetXBytes"/>/<see cref="_frameOffsetYBytes"/>, o arquivo e sempre o mesmo), buffers proprios (<see cref="_frameBuffersB"/>).</summary>
    private IntPtr OnFrameLockB(IntPtr opaque, IntPtr planes)
    {
        var target = _frameBuffersB[_activeFrameBufferIndexB] + _frameOffsetYBytes + _frameOffsetXBytes;
        Marshal.WriteIntPtr(planes, target);
        return IntPtr.Zero;
    }

    /// <summary>
    /// Callback de display do LibVLC (thread do LibVLC) — chamado quando um quadro decodificado
    /// esta pronto pra ser exibido. E aqui, nao no lock/unlock, que <see cref="FrameReady"/> e
    /// disparado: neste ponto o buffer ja contem um quadro completo e estavel.
    ///
    /// Sprint NDI-video-async (2026-09-11): captura o buffer que ACABOU de ser preenchido, alterna
    /// <see cref="_activeFrameBufferIndex"/> pro OUTRO buffer ANTES de disparar <see cref="FrameReady"/>
    /// (pra quando o proximo <see cref="OnFrameLock"/> chegar — o que so pode acontecer depois deste
    /// metodo retornar, ver contrato de callback do LibVLC documentado em <see cref="_frameBuffers"/>
    /// — ele ja escreva no buffer OUTRO, nunca no que acabou de ser entregue pro NdiSender), e so
    /// entao dispara o evento com o buffer capturado (o que acabou de ser preenchido, agora
    /// "inativo" do ponto de vista do proximo lock).
    ///
    /// Sprint NDI-audio-video-clock-lock (2026-09-12): esta sprint colocou aqui uma leitura de
    /// <see cref="_framePlayer"/>.Time pra alimentar <see cref="NdiAudioCapture.SyncToVideoPosition"/>
    /// com a posicao REAL do decodificador que vai pro NDI (em vez do preview, que era o bug real
    /// encontrado naquela sprint — ver comentario de classe de <see cref="NdiAudioCapture"/>). ESSA
    /// LEITURA FOI REMOVIDA DAQUI na Sprint NDI-audio-video-clock-lock-hotfix (2026-09-12, mesmo dia):
    /// causava travamento real da aplicacao inteira. <c>MediaPlayer.Time</c> nao e uma simples leitura
    /// de campo — e uma chamada de volta pro LibVLC nativo (<c>libvlc_media_player_get_time</c>), que
    /// disputa um lock interno do player com outras operacoes de controle (entre elas
    /// <c>MediaPlayer.Pause()</c>). Chamar essa funcao de DENTRO do proprio callback de video (esta
    /// funcao, chamada pela thread interna do LibVLC) e um padrao arriscado o bastante pra causar
    /// deadlock: a thread da interface presa dentro de <c>_framePlayer.Pause()</c> esperando o player
    /// entrar em pausa, e a thread do LibVLC presa aqui dentro esperando o mesmo lock pra responder
    /// <c>.Time</c> — nenhuma das duas libera. Como <c>Pause()</c> e chamado direto na thread da
    /// interface, o efeito observado foi a aplicacao inteira travar e parar de responder a qualquer
    /// comando ao pausar. A leitura de posicao agora e feita por <see cref="PollFramePlayerPosition"/>,
    /// numa thread comum do ThreadPool, nunca de dentro deste callback — mesmo ganho da sprint anterior
    /// (referencia certa, leitura frequente), sem o risco.
    ///
    /// Sprint NDI-Loop-Seamless (2026-09-17): com o modo sem-corte DESLIGADO (padrao), o "if" abaixo
    /// e sempre verdadeiro — FrameReady dispara incondicionalmente, byte a byte o mesmo
    /// comportamento de antes desta sprint. Com o modo LIGADO, so dispara quando este slot (A) for o
    /// ativo agora; quando A estiver em standby (B ativo), o quadro e descartado (nunca mandado pro
    /// NDI) — so serve pra confirmar, via <see cref="_standbyHasFrame"/>, que A ja esta decodificando
    /// de verdade (condicao que <see cref="HandleFrameSlotEndReached"/> exige antes de confiar numa
    /// troca sem reabrir nada).
    /// </summary>
    private void OnFrameDisplay(IntPtr opaque, IntPtr picture)
    {
        var completedBuffer = _frameBuffers[_activeFrameBufferIndex];
        _activeFrameBufferIndex = 1 - _activeFrameBufferIndex;

        if (!_seamlessLoopEnabled || _seamlessSlotAIsActive)
        {
            FrameReady?.Invoke(this, new EngineFrameReadyEventArgs(completedBuffer, FrameWidth, FrameHeight, FrameStrideBytes));
        }
        else
        {
            _standbyHasFrame = true;
        }
    }

    /// <summary>Espelho de <see cref="OnFrameDisplay"/> pro slot B — so dispara <see cref="FrameReady"/> quando B for o ativo (so possivel com o modo sem-corte ligado; desligado, B nunca chega a tocar, este callback nunca dispara de verdade).</summary>
    private void OnFrameDisplayB(IntPtr opaque, IntPtr picture)
    {
        var completedBuffer = _frameBuffersB[_activeFrameBufferIndexB];
        _activeFrameBufferIndexB = 1 - _activeFrameBufferIndexB;

        if (_seamlessLoopEnabled && !_seamlessSlotAIsActive)
        {
            FrameReady?.Invoke(this, new EngineFrameReadyEventArgs(completedBuffer, FrameWidth, FrameHeight, FrameStrideBytes));
        }
        else
        {
            _standbyHasFrame = true;
        }
    }

    /// <summary>
    /// Sprint NDI-audio-video-clock-lock-hotfix (2026-09-12): callback do <see cref="_videoPositionPollTimer"/>
    /// (thread do ThreadPool, NUNCA a thread do LibVLC) — le <see cref="_framePlayer"/>.Time e
    /// repassa pra <see cref="NdiAudioCapture.SyncToVideoPosition"/>. Ver comentario removido de
    /// <see cref="OnFrameDisplay"/> pro historico completo do bug que esta mudanca corrige: a mesma
    /// leitura, feita de dentro do callback nativo de video, causava um deadlock real entre a thread
    /// da interface (presa em <c>Pause()</c>) e a thread do LibVLC (presa em <c>.Time</c>). Fora de
    /// qualquer callback do LibVLC, <c>.Time</c> volta a ser uma chamada de controle comum, sem esse
    /// risco. <c>MediaPlayer.Time</c> e <c>-1</c> quando a posicao ainda nao e conhecida (nenhuma
    /// midia carregada, ou ainda carregando) — contrato do LibVLC, tratado abaixo exatamente como
    /// antes. Qualquer excecao aqui (ex.: player sendo descartado neste exato instante, corrida com
    /// <see cref="Dispose"/>) e engolida de proposito: mesma postura honesta de sempre nesta classe,
    /// nunca derrubar o Deck por causa de uma leitura de posicao que falhou.
    ///
    /// SUAVIZACAO (Sprint NDI-TryCorrectDrift-Ruido, 2026-09-16) — o usuario reportou (com log real
    /// do app confirmando) rajadas de microcortes de audio a cada ~1s durante testes longos, mesmo
    /// depois da Sprint NDI-Frame-Rate-Real ter eliminado a deriva suave original. Investigacao:
    /// <c>_framePlayer.Time</c> NAO avanca de forma linear entre leituras — o LibVLC atualiza sua
    /// posicao interna em LOTES (ja documentado desde 2026-09-13, agora confirmado de novo com
    /// numeros de producao: desvio padrao de 168ms entre leituras consecutivas, distribuicao bimodal
    /// entre ~750ms e ~1250ms de salto por checagem de ~1s, em vez de ~1000ms constante). Alimentar
    /// <see cref="NdiAudioCapture.SyncToVideoPosition"/> com esse valor bruto faz
    /// <see cref="NdiAudioCapture.TryCorrectDrift"/> reagir ao RUIDO da leitura como se fosse deriva
    /// real — confirmado no log do usuario (ate 41% das checagens de uma sessao resultando numa
    /// correcao real). Correcao: em vez de repassar o valor bruto direto, esta funcao mantem uma
    /// ANCORA (<see cref="_lastRawFramePlayerTimeMs"/> + <see cref="_framePlayerPositionAnchorClock"/>)
    /// que so avanca quando o LibVLC reporta um valor realmente novo, e EXTRAPOLA a posicao atual
    /// somando o tempo de parede decorrido desde essa ancora — mesmo principio ja usado pelo pacing
    /// de audio desta classe (<see cref="NdiAudioCapture"/>, relogio de parede em vez de confiar em
    /// callbacks irregulares). <see cref="VideoPositionExtrapolationCapMs"/> limita ate onde essa
    /// extrapolacao pode ir sem uma leitura real nova — protege contra uma pausa de verdade ou um
    /// travamento genuino do video (onde extrapolar indefinidamente seria pior que so congelar no
    /// ultimo valor real conhecido). NENHUMA mudanca em <see cref="NdiAudioCapture.TryCorrectDrift"/>
    /// nesta sprint — o limiar/passo/frequencia da correcao continuam exatamente como estavam, so a
    /// MEDIDA de posicao de video que ela recebe ficou mais confiavel.
    /// </summary>
    private void PollFramePlayerPosition(object? state)
    {
        if (_disposed)
        {
            return;
        }

        // Sprint NDI-Loop-Seamless (2026-09-17): le do slot ATIVO agora (A ou B) — com o modo
        // desligado (padrao), ActiveFramePlayer sempre resolve pra _framePlayer, IDENTICO ao que esta
        // linha fazia antes desta sprint.
        var activeFramePlayer = ActiveFramePlayer;

        long framePlayerTimeMs;
        try
        {
            framePlayerTimeMs = activeFramePlayer.Time;
        }
        catch (Exception)
        {
            return;
        }

        if (framePlayerTimeMs < 0)
        {
            return;
        }

        if (framePlayerTimeMs != _lastRawFramePlayerTimeMs)
        {
            // Leitura realmente nova do LibVLC (avancou, ou voltou — seek/loop/reinicio) — vira a
            // nova ancora "verdadeira", zerando o relogio de extrapolacao. Cobre seek/loop/pausa sem
            // nenhum hook extra: qualquer mudanca real de posicao (inclusive pra tras) e adotada na
            // proxima checagem, no maximo VideoPositionPollIntervalMs (50ms) depois de acontecer. Uma
            // troca de slot (modo sem-corte) tambem cai exatamente neste caso — a leitura do slot
            // recem-promovido chega "nova" pro anchor, sem tratamento especial nenhum.
            _lastRawFramePlayerTimeMs = framePlayerTimeMs;
            _framePlayerPositionAnchorClock.Restart();
        }

        var elapsedSinceAnchorMs = Math.Min(_framePlayerPositionAnchorClock.ElapsedMilliseconds, VideoPositionExtrapolationCapMs);
        var smoothedPositionMs = _lastRawFramePlayerTimeMs + elapsedSinceAnchorMs;

        _audioCapture.SyncToVideoPosition(TimeSpan.FromMilliseconds(smoothedPositionMs));

        if (_seamlessLoopEnabled && _loopEnabled && !_seamlessStandbyArmedThisCycle)
        {
            TryArmSeamlessStandby(framePlayerTimeMs);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // Sprint NDI-audio-video-clock-lock-hotfix (2026-09-12): para o timer ANTES de descartar
        // _framePlayer — _disposed ja e true a essa altura (checado no topo de PollFramePlayerPosition),
        // e o try/catch la dentro cobre qualquer callback que ja estava em voo quando Dispose chegou
        // aqui. Timer.Dispose() so garante que nenhuma NOVA execucao comeca; nao espera a atual
        // terminar — aceitavel aqui pois essa execucao, se existir, ve _disposed==true ou pega a
        // excecao e retorna sem tocar em mais nada.
        _videoPositionPollTimer.Dispose();

        _mediaPlayer.TimeChanged -= OnTimeChanged;
        _mediaPlayer.EndReached -= OnEndReached;
        _mediaPlayer.EncounteredError -= OnEncounteredError;
        _framePlayer.EndReached -= OnFramePlayerEndReached;
        _framePlayerB.EndReached -= OnFramePlayerBEndReached;

        _mediaPlayer.Stop();
        _mediaPlayer.Dispose();
        _currentMedia?.Dispose();

        _framePlayer.Stop();
        _framePlayer.Dispose();
        _currentFrameMedia?.Dispose();
        Marshal.FreeHGlobal(_frameBuffers[0]);
        Marshal.FreeHGlobal(_frameBuffers[1]);

        // Sprint NDI-Loop-Seamless (2026-09-17): descarte simetrico do segundo player/buffers —
        // seguro mesmo se o modo sem-corte nunca chegou a ligar (B nunca recebeu Media, Stop/Dispose
        // num player parado nao tem efeito nenhum, Marshal.FreeHGlobal libera memoria alocada no
        // construtor de qualquer forma, usada ou nao).
        _framePlayerB.Stop();
        _framePlayerB.Dispose();
        _currentFrameMediaB?.Dispose();
        Marshal.FreeHGlobal(_frameBuffersB[0]);
        Marshal.FreeHGlobal(_frameBuffersB[1]);

        // Sprint NDI-audio-NAudio (2026-08-11): NdiAudioCapture.Dispose() cuida de parar a thread de
        // decodificacao e descartar o AudioFileReader — sem nenhum estado nativo do LibVLC pra
        // ordenar aqui (era o caso do antigo _audioCapturePlayer/_audioLibVlc, removidos).
        _audioCapture.Dispose();
    }
}
