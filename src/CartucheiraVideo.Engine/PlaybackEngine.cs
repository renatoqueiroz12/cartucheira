using System.Runtime.InteropServices;
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
/// (pacing por relogio de parede + correcao periodica contra a posicao real do video, alimentada por
/// <see cref="OnTimeChanged"/> aqui). <see cref="_framePlayer"/> (video-only, <c>:no-audio</c>) e
/// <see cref="_mediaPlayer"/> (preview local) continuam 100% LibVLCSharp, intocados — so o caminho de
/// audio-pro-NDI mudou de mecanismo.
/// </summary>
public sealed class PlaybackEngine : IDisposable
{
    /// <summary>Resolucao/formato fixos do player de captura (decisao da Sprint NDI: MVP sem negociacao dinamica de formato) — BGRA 32bpp, 1 canal de saida NDI por Deck.</summary>
    private const int FrameWidth = 1920;
    private const int FrameHeight = 1080;
    private const int FrameStrideBytes = FrameWidth * 4;
    private const int BytesPerPixel = 4;

    /// <summary>Tempo maximo pro parse de resolucao/orientacao (Sprint Deck-Aspect-Ratio) — parse local so le cabecalho, nunca deveria demorar isso; limite curto pra nunca travar um Load perceptivelmente, mesmo com um arquivo estranho.</summary>
    private const int AspectRatioParseTimeoutMs = 1500;

    private readonly LibVLC _libVlc;
    private readonly MediaPlayer _mediaPlayer;
    private readonly MediaPlayer _framePlayer;
    private readonly IntPtr _frameBuffer;

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

    private Media? _currentMedia;
    private Media? _currentFrameMedia;
    private bool _loopEnabled;
    private bool _disposed;

    /// <summary>Tamanho, dentro do canvas fixo 1920x1080, que preserva a proporcao real do arquivo carregado — recalculado a cada Load (ver <see cref="ConfigureFrameFormat"/>). Comeca no canvas inteiro (16:9), mesmo valor de antes desta sprint, ate o primeiro Load calcular o real.</summary>
    private int _frameInnerWidth = FrameWidth;
    private int _frameInnerHeight = FrameHeight;
    private int _frameOffsetXBytes;
    private int _frameOffsetYBytes;

    public PlaybackEngine(LibVLC libVlc)
    {
        _libVlc = libVlc ?? throw new ArgumentNullException(nameof(libVlc));
        _mediaPlayer = new MediaPlayer(_libVlc);

        _mediaPlayer.TimeChanged += OnTimeChanged;
        _mediaPlayer.EndReached += OnEndReached;
        _mediaPlayer.EncounteredError += OnEncounteredError;
        _mediaPlayer.Playing += (_, _) => StateChanged?.Invoke(this, new EngineStateChangedEventArgs(EnginePlaybackState.Playing));
        _mediaPlayer.Paused += (_, _) => StateChanged?.Invoke(this, new EngineStateChangedEventArgs(EnginePlaybackState.Paused));
        _mediaPlayer.Stopped += (_, _) => StateChanged?.Invoke(this, new EngineStateChangedEventArgs(EnginePlaybackState.Stopped));

        // Player de captura (headless): mesma LibVLC compartilhada, midia propria (ver Load — NAO
        // reusa o mesmo objeto Media do player principal; dois Media separados abrindo o mesmo
        // arquivo e um padrao conhecido/seguro do LibVLC, ao contrario de um Media so preso a dois
        // MediaPlayer ao mesmo tempo, que nao e documentado como seguro em lugar nenhum encontrado
        // na pesquisa desta sprint — preferido o caminho conservador).
        _framePlayer = new MediaPlayer(_libVlc);
        _frameBuffer = Marshal.AllocHGlobal(FrameStrideBytes * FrameHeight);
        FillFrameBufferOpaqueBlack();
        _frameLockCallback = OnFrameLock;
        _frameDisplayCallback = OnFrameDisplay;
        // Formato inicial = canvas inteiro (16:9), mesmo valor de sempre — Load() reconfigura pro
        // tamanho real do arquivo assim que houver um carregado (ver ConfigureFrameFormat).
        _framePlayer.SetVideoFormat("RV32", FrameWidth, FrameHeight, FrameStrideBytes);
        _framePlayer.SetVideoCallbacks(_frameLockCallback, null, _frameDisplayCallback);
        _framePlayer.EndReached += OnFramePlayerEndReached;

        // Sprint NDI-audio-NAudio (2026-08-11) — repassa direto (mesma decisao de latencia ja
        // documentada nos outros eventos desta classe: quem ouve isto nunca toca em estado de
        // binding do WPF, marshalar so acrescentaria atraso sem necessidade).
        _audioCapture.AudioFrameReady += (_, e) => AudioFrameReady?.Invoke(this, e);
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
    /// Disparado a cada quadro que o player de captura termina de decodificar (thread do LibVLC —
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
    /// Associa um arquivo ao player. Sempre aceito, mesmo com uma midia ja tocando (a midia
    /// anterior e descartada). Nao inicia a reproducao — quem decide iniciar e o chamador (<see cref="Play"/>).
    /// </summary>
    public void Load(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            throw new ArgumentException("Caminho do arquivo nao pode ser vazio.", nameof(filePath));
        }

        // Sprint NDI-audio-NAudio (2026-08-11): a hipotese "player de preview monopolizando o audio
        // real do arquivo" (testada com ":no-audio" temporario aqui) deixou de fazer sentido pra
        // testar — o audio pro NDI agora vem de uma decodificacao TOTALMENTE INDEPENDENTE via NAudio
        // (ver _audioCapture.Load abaixo), sem nenhum estado do LibVLC compartilhado entre os dois
        // caminhos. ":no-audio" removido: o preview volta a tocar audio local normalmente (mesmo
        // comportamento de antes da Sprint NDI-audio) — o operador ouve o Deck de novo.
        var previousMedia = _currentMedia;
        _currentMedia = new Media(_libVlc, filePath, FromType.FromPath);
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
        var previousFrameMedia = _currentFrameMedia;
        _currentFrameMedia = new Media(_libVlc, filePath, FromType.FromPath, ":no-audio");
        _framePlayer.Media = _currentFrameMedia;
        previousFrameMedia?.Dispose();

        ConfigureFrameFormat(_currentFrameMedia);

        // Sprint NDI-audio-NAudio (2026-08-11): decodificacao de audio pro NDI, agora via NAudio —
        // terceira leitura independente do mesmo arquivo, so que fora do LibVLC por completo (ver
        // comentario de classe de NdiAudioCapture pro historico/motivo da migracao). NdiAudioCapture
        // ja cuida da propria sincronizacao (para a decodificacao anterior antes de trocar de
        // arquivo) — nao ha corrida de use-after-free possivel aqui, unica classe de bug que
        // derrubou o pipeline anterior sete vezes.
        _audioCapture.Load(filePath);

        StateChanged?.Invoke(this, new EngineStateChangedEventArgs(EnginePlaybackState.Loaded));
    }

    /// <summary>
    /// Sprint Deck-Aspect-Ratio (2026-08-11): calcula o retangulo que preserva a proporcao real do
    /// arquivo dentro do canvas fixo 1920x1080 e reconfigura o player de captura pra esse tamanho —
    /// chamado a cada <see cref="Load"/> (cada arquivo pode ter uma proporcao diferente). Preenche o
    /// buffer inteiro de preto opaco antes (as bordas do letterbox/pillarbox do arquivo novo podem
    /// ser maiores do que as do arquivo anterior — sem isso, sobrariam pixels do quadro anterior nas
    /// bordas).
    /// </summary>
    private void ConfigureFrameFormat(Media frameMedia)
    {
        var (innerWidth, innerHeight) = ComputeLetterboxSize(frameMedia);

        _frameInnerWidth = innerWidth;
        _frameInnerHeight = innerHeight;
        _frameOffsetXBytes = (FrameWidth - innerWidth) / 2 * BytesPerPixel;
        _frameOffsetYBytes = (FrameHeight - innerHeight) / 2 * FrameStrideBytes;

        FillFrameBufferOpaqueBlack();

        _framePlayer.SetVideoFormat("RV32", (uint)innerWidth, (uint)innerHeight, (uint)FrameStrideBytes);
    }

    /// <summary>
    /// Le a resolucao/orientacao real do video (parse local, com timeout curto) e calcula o maior
    /// retangulo innerWidth x innerHeight que cabe dentro do canvas 1920x1080 sem distorcer a
    /// proporcao original do arquivo (mesma conta de "fit"/letterbox de sempre). Falha honesta em
    /// qualquer problema (sem faixa de video, parse expirado/sem sucesso, dimensao invalida): cai
    /// pro canvas inteiro (1920x1080) — exatamente o comportamento de antes desta sprint, sem
    /// regressao nesse caso.
    /// </summary>
    private (int Width, int Height) ComputeLetterboxSize(Media frameMedia)
    {
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

            // Alinhamento par (2px) — mais seguro pra formatos de video/chroma em geral, custo zero.
            innerWidth -= innerWidth % 2;
            innerHeight -= innerHeight % 2;

            return (Math.Clamp(innerWidth, 2, FrameWidth), Math.Clamp(innerHeight, 2, FrameHeight));
        }
        catch (Exception)
        {
            // Fronteira com o LibVLC nativo — qualquer falha aqui cai pro canvas inteiro, nunca
            // trava nem quebra o Load (mesmo racional ja usado em GetDurationAsync/outras
            // fronteiras nativas deste projeto).
            return (FrameWidth, FrameHeight);
        }
    }

    /// <summary>Preenche <see cref="_frameBuffer"/> inteiro de preto OPACO (canal Alpha 255, nao 0/transparente — letterbox de transmissao precisa de barra preta solida, nao transparente). Sem `unsafe`/ponteiros de proposito — só Marshal.Copy, mesmo estilo do resto deste arquivo.</summary>
    private void FillFrameBufferOpaqueBlack()
    {
        var row = new byte[FrameStrideBytes];
        for (var x = 3; x < FrameStrideBytes; x += 4)
        {
            row[x] = 255;
        }

        for (var y = 0; y < FrameHeight; y++)
        {
            Marshal.Copy(row, 0, _frameBuffer + y * FrameStrideBytes, FrameStrideBytes);
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
        _audioCapture.Clear();

        _mediaPlayer.Media = null;
        _framePlayer.Media = null;

        _currentMedia?.Dispose();
        _currentMedia = null;

        _currentFrameMedia?.Dispose();
        _currentFrameMedia = null;

        StateChanged?.Invoke(this, new EngineStateChangedEventArgs(EnginePlaybackState.Idle));
    }

    /// <summary>Inicia (ou retoma, se pausado) a reproducao da midia carregada. Sem efeito se nada foi carregado.</summary>
    public void Play()
    {
        if (_mediaPlayer.Media is null)
        {
            return;
        }

        _mediaPlayer.Play();

        if (_framePlayer.Media is not null)
        {
            _framePlayer.Play();
        }

        _audioCapture.Play();
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

        _mediaPlayer.Play();
    }

    /// <summary>Congela a reproducao no ponto atual. Sem efeito se nao estiver tocando.</summary>
    public void Pause()
    {
        if (_mediaPlayer.IsPlaying)
        {
            _mediaPlayer.Pause();
        }

        if (_framePlayer.IsPlaying)
        {
            _framePlayer.Pause();
        }

        _audioCapture.Pause();
    }

    /// <summary>Interrompe a reproducao — o proximo <see cref="Play"/> recomeca do inicio.</summary>
    public void Stop()
    {
        _mediaPlayer.Stop();
        _framePlayer.Stop();
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

        if (_framePlayer.Media is not null)
        {
            _framePlayer.Time = (long)position.TotalMilliseconds;
        }

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
    /// </summary>
    public void SetAudioOutputDevice(string? deviceId)
    {
        if (string.IsNullOrEmpty(deviceId))
        {
            return;
        }

        _mediaPlayer.SetOutputDevice(deviceId);
    }

    private void OnTimeChanged(object? sender, MediaPlayerTimeChangedEventArgs e)
    {
        var lengthMs = _mediaPlayer.Length;
        var duration = lengthMs > 0 ? TimeSpan.FromMilliseconds(lengthMs) : (TimeSpan?)null;
        PositionChanged?.Invoke(this, new EnginePositionChangedEventArgs(TimeSpan.FromMilliseconds(e.Time), duration));

        // Sprint NDI-audio-NAudio (2026-08-11): alimenta a correcao secundaria de deriva A/V do
        // NdiAudioCapture (ver comentario de classe la) com a posicao de video mais recente — mesma
        // cadencia que ja alimentava PositionChanged, nenhum custo extra de assinatura de evento.
        _audioCapture.SyncToVideoPosition(TimeSpan.FromMilliseconds(e.Time));
    }

    /// <summary>
    /// EndReached e disparado numa thread interna do LibVLC — chamar Play/atribuir Media
    /// diretamente aqui trava o player (limitacao conhecida e documentada do LibVLC/LibVLCSharp).
    /// Por isso o reinicio do loop e despachado pra uma thread separada.
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
    /// </summary>
    private void OnFramePlayerEndReached(object? sender, EventArgs e)
    {
        if (_loopEnabled && _currentFrameMedia is not null)
        {
            Task.Run(() =>
            {
                _framePlayer.Media = _currentFrameMedia;
                _framePlayer.Play();
            });
        }
    }

    /// <summary>
    /// Callback de lock do LibVLC (thread do LibVLC) — chamado antes de decodificar cada quadro no
    /// buffer. Escreve o endereco de escrita em <paramref name="planes"/> (saida) e retorna
    /// IntPtr.Zero como id opaco de "picture" (nao usado por este projeto).
    ///
    /// Sprint Deck-Aspect-Ratio (2026-08-11): o endereco NAO e mais sempre o inicio do
    /// <see cref="_frameBuffer"/> — e o inicio + o deslocamento (<see cref="_frameOffsetXBytes"/>/
    /// <see cref="_frameOffsetYBytes"/>) calculado por <see cref="ConfigureFrameFormat"/>, pra
    /// centralizar o retangulo proporcional dentro do canvas fixo 1920x1080. O LibVLC escreve exatamente
    /// innerWidth x innerHeight pixels a partir desse ponto, respeitando o pitch do canvas inteiro
    /// entre linhas (SetVideoFormat pedido com innerWidth/innerHeight mas pitch=FrameStrideBytes) —
    /// as bordas ao redor (preenchidas de preto por FillFrameBufferOpaqueBlack) nunca sao tocadas
    /// pelo LibVLC, ficam pretas sozinhas.
    /// </summary>
    private IntPtr OnFrameLock(IntPtr opaque, IntPtr planes)
    {
        var target = _frameBuffer + _frameOffsetYBytes + _frameOffsetXBytes;
        Marshal.WriteIntPtr(planes, target);
        return IntPtr.Zero;
    }

    /// <summary>
    /// Callback de display do LibVLC (thread do LibVLC) — chamado quando um quadro decodificado
    /// esta pronto pra ser exibido. E aqui, nao no lock/unlock, que <see cref="FrameReady"/> e
    /// disparado: neste ponto o buffer ja contem um quadro completo e estavel.
    /// </summary>
    private void OnFrameDisplay(IntPtr opaque, IntPtr picture)
    {
        FrameReady?.Invoke(this, new EngineFrameReadyEventArgs(_frameBuffer, FrameWidth, FrameHeight, FrameStrideBytes));
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        _mediaPlayer.TimeChanged -= OnTimeChanged;
        _mediaPlayer.EndReached -= OnEndReached;
        _mediaPlayer.EncounteredError -= OnEncounteredError;
        _framePlayer.EndReached -= OnFramePlayerEndReached;

        _mediaPlayer.Stop();
        _mediaPlayer.Dispose();
        _currentMedia?.Dispose();

        _framePlayer.Stop();
        _framePlayer.Dispose();
        _currentFrameMedia?.Dispose();
        Marshal.FreeHGlobal(_frameBuffer);

        // Sprint NDI-audio-NAudio (2026-08-11): NdiAudioCapture.Dispose() cuida de parar a thread de
        // decodificacao e descartar o AudioFileReader — sem nenhum estado nativo do LibVLC pra
        // ordenar aqui (era o caso do antigo _audioCapturePlayer/_audioLibVlc, removidos).
        _audioCapture.Dispose();
    }
}
