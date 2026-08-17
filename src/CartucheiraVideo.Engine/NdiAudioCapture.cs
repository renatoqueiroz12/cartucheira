using System.Diagnostics;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace CartucheiraVideo.Engine;

/// <summary>
/// Sprint NDI-audio-NAudio (2026-08-11, mesmo dia da aprovacao do usuario "OKL") — decodificador de
/// audio dedicado pro NDI, via NAudio, substituindo POR COMPLETO o mecanismo anterior (callback de
/// memoria bruta do LibVLC, <c>SetAudioFormat</c>/<c>SetAudioCallbacks</c> num <c>MediaPlayer</c>
/// headless proprio). Motivo da troca: sete crashes ao vivo (<see cref="System.ExecutionEngineException"/>,
/// excecao de estado corrompido do CLR, sem StackTrace/InnerException/Message, nao interceptavel) no
/// mecanismo de callback do LibVLC ao longo de um unico dia de sprint — e mesmo depois de eliminar o
/// crash mais recente (dando ao player de captura de audio uma <c>LibVLC</c> propria, isolada), o
/// audio entregue continuou zerado/corrompido, evidencia de que o problema estava no proprio
/// mecanismo de callback, nao em como este projeto o usava. NAudio ja e usado neste projeto (ver
/// <see cref="AudioEngine"/>, motor das duas Cartucheiras) sem nenhum historico de crash — os padroes
/// desta classe (<see cref="AudioFileReader"/>, cadeia de <see cref="ISampleProvider"/>,
/// <see cref="MonoToStereoSampleProvider"/>, <see cref="WdlResamplingSampleProvider"/>) sao os MESMOS
/// ja validados la, adaptados aqui pro caso de uso de captura-pra-NDI em vez de mixagem-pra-alto-falante.
///
/// Decodifica o arquivo de forma TOTALMENTE INDEPENDENTE do <see cref="MediaPlayer"/> de video do
/// <see cref="PlaybackEngine"/> — nao ha nenhum buffer nativo compartilhado entre os dois; cada um le
/// o mesmo arquivo por um caminho proprio (mesma decisao arquitetural ja usada desde a Sprint NDI
/// pros tres players de video/captura/audio anteriores: decodificacao em dobro/triplo aceita
/// conscientemente, ordem de prioridade Estabilidade > Simplicidade > Baixo consumo de memoria).
///
/// SINCRONISMO A/V — ponto de atencao explicito levantado pelo usuario antes de aprovar esta troca
/// (citando risco real de deriva entre um audio tocado por um clock independente e o video do
/// LibVLC): esta classe usa dois mecanismos, um primario e um secundario:
/// <list type="bullet">
/// <item><b>Primario — cadencia por relogio de parede (<see cref="Stopwatch"/>).</b> Uma thread
/// dedicada (<see cref="AudioThreadLoop"/>) calcula, a cada iteracao, quantas amostras JA DEVERIAM
/// ter sido entregues desde o inicio da reproducao (<c>elapsed * sampleRate</c>) e le/entrega blocos
/// ate alcancar esse alvo — nunca usa <c>Thread.Sleep</c> fixo por bloco (que acumula deriva ao longo
/// de um video longo, erro classico deste tipo de pacing). <see cref="Play"/>/<see cref="Pause"/>
/// apenas ligam/desligam o MESMO <see cref="Stopwatch"/> (<c>Start()</c>/<c>Stop()</c> sao
/// cumulativos — tempo pausado nao conta pro calculo), entao o alvo de amostras congela
/// automaticamente durante uma pausa, sem nenhum estado extra pra gerenciar.</item>
/// <item><b>Secundario — correcao periodica contra a posicao real do video.</b>
/// <see cref="SyncToVideoPosition"/>, chamado pelo <see cref="PlaybackEngine"/> a cada
/// <c>MediaPlayer.TimeChanged</c> (mesma cadencia que ja alimenta a UI), guarda a posicao mais
/// recente reportada pelo LibVLC. A thread de audio compara essa posicao com
/// <see cref="AudioFileReader.CurrentTime"/> a cada ~1s de audio entregue — se a diferenca passar de
/// <see cref="DriftCorrectionThresholdMs"/>, reposiciona o leitor de audio direto pra posicao do
/// video (pequeno "pulo" audivel, aceito conscientemente como rede de seguranca rara, nunca como
/// mecanismo normal de sincronismo — na pratica so deveria disparar se o pacing primario falhar por
/// algum motivo excepcional, ex.: uma pausa longa do GC ou disco lento).</item>
/// </list>
///
/// Falha honesta ao abrir um arquivo (formato nao suportado, corrompido, mais de 2 canais): esta
/// classe simplesmente nao entrega nenhum bloco de audio (equivalente a "Deck sem audio pro NDI") —
/// nunca derruba o Deck inteiro, mesmo padrao ja usado pelo resto do projeto nesta fronteira.
/// </summary>
public sealed class NdiAudioCapture : IDisposable
{
    /// <summary>Formato fixo entregue por <see cref="AudioFrameReady"/> — mesmo formato que a NDI SDK ja consumia do mecanismo anterior (48kHz estereo float32 intercalado), sem negociacao dinamica.</summary>
    private const int AudioSampleRate = 48000;
    private const int AudioChannels = 2;

    /// <summary>Tamanho de bloco entregue por chamada — 10ms a 48kHz (480 amostras/canal). Pequeno de proposito (prioridade #1 do projeto, Latencia): blocos menores custam mais chamadas/s, mas reduzem o atraso entre "amostra decodificada" e "amostra no NDI".</summary>
    private const int BlockSamplesPerChannel = 480;

    /// <summary>Diferenca maxima tolerada entre a posicao do audio e a posicao do video antes da correcao secundaria (ver comentario de classe) entrar em acao.</summary>
    private const int DriftCorrectionThresholdMs = 200;

    /// <summary>Intervalo minimo entre duas checagens de deriva — nao precisa ser a cada bloco (10ms), just o suficiente pra pegar deriva real antes que vire perceptivel.</summary>
    private const int DriftCheckIntervalMs = 1000;

    /// <summary>Falhas seguidas de <c>chain.Read</c> (ver comentario no laco de <see cref="AudioThreadLoop"/> sobre o 8º crash da saga) toleradas antes de desistir e encerrar a thread — cobre um blip passageiro do Media Foundation sem tentar pra sempre num arquivo genuinamente com problema.</summary>
    private const int MaxConsecutiveReadFailures = 10;

    private readonly object _gate = new();
    private readonly Stopwatch _pacingClock = new();

    private AudioFileReader? _reader;
    private ISampleProvider? _chain;
    private Thread? _thread;
    private volatile bool _stopRequested;
    private volatile bool _loopEnabled;
    private bool _disposed;

    /// <summary>Ultima posicao de video conhecida (ticks de <see cref="TimeSpan"/>) reportada via <see cref="SyncToVideoPosition"/> — <c>-1</c> = nenhuma ainda. <see cref="long"/> e naturalmente atomico pra leitura/escrita em runtimes 64-bit (.NET 8/WPF desktop), sem necessidade de lock nem <see cref="System.Threading.Interlocked"/> pra este uso (um produtor, um consumidor, valor sempre consistente mesmo sem sincronizacao explicita).</summary>
    private long _lastVideoPositionTicks = -1;

    /// <summary>
    /// Disparado a cada bloco de amostras decodificado (thread propria desta classe — NAO
    /// marshalado pra UI thread de proposito, mesma decisao ja tomada pro antigo callback do LibVLC e
    /// pro <see cref="PlaybackEngine.FrameReady"/>: quem ouve isto, NdiSender via DeckViewModel, nunca
    /// toca em estado de binding do WPF).
    /// </summary>
    public event EventHandler<EngineAudioFrameReadyEventArgs>? AudioFrameReady;

    /// <summary>
    /// Quando verdadeiro, o fim do arquivo reinicia a decodificacao de audio em vez de simplesmente
    /// parar de entregar blocos — mesmo espirito de <see cref="PlaybackEngine.LoopEnabled"/>, mas
    /// independente dele (esta classe nao sabe nada sobre o estado do video; quem mantem os dois em
    /// sincronia e o <see cref="PlaybackEngine"/>, escrevendo esta propriedade sempre que escreve a
    /// dele).
    /// </summary>
    public bool LoopEnabled
    {
        get => _loopEnabled;
        set => _loopEnabled = value;
    }

    /// <summary>
    /// Abre um arquivo novo pra captura de audio, descartando qualquer decodificacao anterior (mesmo
    /// comportamento honesto de <see cref="PlaybackEngine.Load"/>: sempre aceito, mesmo com algo
    /// tocando). Nao inicia a decodificacao — quem decide iniciar e <see cref="Play"/>.
    /// </summary>
    public void Load(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            throw new ArgumentException("Caminho do arquivo nao pode ser vazio.", nameof(filePath));
        }

        StopInternal();

        lock (_gate)
        {
            var previousReader = _reader;
            _reader = null;
            _chain = null;
            previousReader?.Dispose();

            Volatile.Write(ref _lastVideoPositionTicks, -1);

            AudioFileReader reader;
            try
            {
                reader = new AudioFileReader(filePath);
            }
            catch (Exception)
            {
                // Falha honesta (arquivo corrompido/formato nao suportado) — este Deck simplesmente
                // fica sem audio pro NDI, video continua funcionando normalmente via PlaybackEngine.
                return;
            }

            try
            {
                _chain = BuildChain(reader);
                _reader = reader;
            }
            catch (Exception)
            {
                // Mais de 2 canais, ou qualquer outra falha ao montar a cadeia de adaptacao —
                // mesma falha honesta.
                reader.Dispose();
            }
        }
    }

    /// <summary>Inicia (ou retoma, se pausado) a entrega de audio. Sem efeito se nada foi carregado (ou o Load falhou — ver comentario de <see cref="Load"/>).</summary>
    public void Play()
    {
        lock (_gate)
        {
            if (_reader is null || _chain is null)
            {
                return;
            }

            if (_thread is null)
            {
                _stopRequested = false;
                _pacingClock.Restart();
                _thread = new Thread(AudioThreadLoop) { IsBackground = true, Name = "NdiAudioCapture" };
                _thread.Start();
            }
            else
            {
                // Ja rodando (possivelmente pausado) — Start() num Stopwatch ja rodando e no-op
                // seguro; num pausado, retoma de onde ficou (cumulativo, tempo pausado nao conta).
                _pacingClock.Start();
            }
        }
    }

    /// <summary>Congela a entrega de audio no ponto atual (o <see cref="Stopwatch"/> de cadencia para — ver comentario de classe). Sem efeito se nao estiver tocando.</summary>
    public void Pause()
    {
        lock (_gate)
        {
            _pacingClock.Stop();
        }
    }

    /// <summary>Interrompe a entrega de audio e volta a posicao de leitura pro inicio — mesmo par Stop/Play de sempre: o proximo <see cref="Play"/> recomeca do inicio do arquivo.</summary>
    public void Stop()
    {
        StopInternal();
    }

    /// <summary>Move a posicao de leitura do audio. Sem efeito se nada foi carregado. Silenciosamente recorta pra dentro de [0, duracao] — nunca lanca por causa de um valor fora da faixa.</summary>
    public void Seek(TimeSpan position)
    {
        lock (_gate)
        {
            if (_reader is null)
            {
                return;
            }

            try
            {
                var clamped = position;
                if (clamped < TimeSpan.Zero)
                {
                    clamped = TimeSpan.Zero;
                }
                else if (clamped > _reader.TotalTime)
                {
                    clamped = _reader.TotalTime;
                }

                _reader.CurrentTime = clamped;
            }
            catch (Exception)
            {
                // Fronteira nativa (Media Foundation por baixo de alguns formatos, ex. AAC/WMA) —
                // falha honesta, ignora o seek em vez de derrubar o Deck.
            }
        }
    }

    /// <summary>
    /// Descarrega o arquivo atual (se houver) e para a decodificacao — mesma distincao de
    /// <see cref="PlaybackEngine.Clear"/> vs <see cref="PlaybackEngine.Stop"/>: Clear tira o arquivo
    /// de vez, um proximo <see cref="Play"/> sem <see cref="Load"/> antes nao faz nada.
    /// </summary>
    public void Clear()
    {
        StopInternal();

        lock (_gate)
        {
            _chain = null;
            var reader = _reader;
            _reader = null;
            reader?.Dispose();
        }
    }

    /// <summary>
    /// Chamado pelo <see cref="PlaybackEngine"/> a cada <c>MediaPlayer.TimeChanged</c> do lado do
    /// video — alimenta a correcao secundaria de deriva (ver comentario de classe). Seguro chamar de
    /// qualquer thread (a escrita e de um <see cref="long"/>, atomica em runtimes 64-bit, sem
    /// necessidade de lock).
    /// </summary>
    public void SyncToVideoPosition(TimeSpan videoPosition)
    {
        Volatile.Write(ref _lastVideoPositionTicks, videoPosition.Ticks);
    }

    private void StopInternal()
    {
        Thread? thread;
        lock (_gate)
        {
            _stopRequested = true;
            thread = _thread;
            _thread = null;
        }

        // Fora do lock: a propria AudioThreadLoop precisa do _gate pra ler _reader/_chain a cada
        // iteracao — segurar o lock aqui enquanto espera a thread parar seria um deadlock potencial.
        thread?.Join();

        lock (_gate)
        {
            _pacingClock.Reset();
            try
            {
                if (_reader is not null)
                {
                    _reader.Position = 0;
                }
            }
            catch (Exception)
            {
                // Fronteira nativa — mesma falha honesta de sempre, nunca derruba o Deck por causa
                // de um Stop.
            }
        }
    }

    /// <summary>Adapta o arquivo aberto pro formato fixo desta classe (48kHz estereo) — mesmo padrao ja validado em <see cref="AudioEngine.BuildMixerInput"/>: mono vira estereo, taxa diferente e reamostrada via <see cref="WdlResamplingSampleProvider"/> (resampler 100% gerenciado do proprio NAudio). Mais de 2 canais nao e suportado.</summary>
    private static ISampleProvider BuildChain(AudioFileReader reader)
    {
        ISampleProvider chain = reader;

        if (chain.WaveFormat.Channels == 1)
        {
            chain = new MonoToStereoSampleProvider(chain);
        }
        else if (chain.WaveFormat.Channels != AudioChannels)
        {
            throw new NotSupportedException($"Arquivo com {chain.WaveFormat.Channels} canal(is) nao suportado (so mono ou estereo).");
        }

        if (chain.WaveFormat.SampleRate != AudioSampleRate)
        {
            chain = new WdlResamplingSampleProvider(chain, AudioSampleRate);
        }

        return chain;
    }

    /// <summary>
    /// Corpo da thread dedicada de captura de audio — ver comentario de classe pro desenho completo
    /// do pacing primario (relogio de parede) e da correcao secundaria (deriva contra o video).
    /// </summary>
    private void AudioThreadLoop()
    {
        var buffer = new float[BlockSamplesPerChannel * AudioChannels];
        long samplesDelivered = 0;
        var lastDriftCheck = Stopwatch.StartNew();
        var consecutiveReadFailures = 0;

        while (!_stopRequested)
        {
            var targetSamples = (long)(_pacingClock.Elapsed.TotalSeconds * AudioSampleRate);

            if (samplesDelivered >= targetSamples)
            {
                Thread.Sleep(1);
                continue;
            }

            ISampleProvider? chain;
            AudioFileReader? reader;
            bool loop;

            lock (_gate)
            {
                chain = _chain;
                reader = _reader;
                loop = _loopEnabled;
            }

            if (chain is null || reader is null)
            {
                // Clear()/Load() trocou o arquivo debaixo desta thread (ou Dispose em andamento) —
                // encerra, quem chamou Play() de novo cria uma thread nova.
                return;
            }

            int read;
            try
            {
                read = chain.Read(buffer, 0, buffer.Length);
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (Exception)
            {
                // OITAVO crash da saga do audio NDI (2026-08-11, mesmo dia da migracao pra NAudio) —
                // primeiro DESTA saga que e uma excecao gerenciada de verdade, capturavel
                // (System.Runtime.InteropServices.COMException, 0xC00D36E5 "a operacao no
                // deslocamento atual nao e permitida", pilha real mostrando
                // NAudio.MediaFoundation.IMFSourceReader.SetCurrentPosition) — nao mais o
                // ExecutionEngineException de estado corrompido do CLR das sete tentativas
                // anteriores. Causa: NAudio ADIA o reposicionamento pedido via
                // AudioFileReader.Position/CurrentTime (usado por Seek(), pelo reinicio de loop
                // logo abaixo, e pela correcao de deriva em TryCorrectDrift) pro PRIMEIRO Read()
                // seguinte — se esse SetCurrentPosition adiado falhar (estado interno do Media
                // Foundation nao aceita o reposicionamento pedido, ex.: bem no limite do stream em
                // certos containers/formatos comprimidos), a excecao aparece AQUI, na chamada de
                // Read(), nao na linha que pediu o reposicionamento (essas ja tinham try/catch
                // proprio, mas nao ajudam porque a falha real acontece um passo depois). Falha
                // honesta: conta como um bloco perdido, tenta de novo no proximo ciclo da thread —
                // nunca derruba o Deck por causa disso. Only desiste (ver
                // MaxConsecutiveReadFailures abaixo) se falhar repetidas vezes seguidas, sinal de
                // que o arquivo tem algum problema mais fundamental de reposicionamento neste
                // formato, nao um blip passageiro.
                consecutiveReadFailures++;
                if (consecutiveReadFailures >= MaxConsecutiveReadFailures)
                {
                    return;
                }

                Thread.Sleep(1);
                continue;
            }

            consecutiveReadFailures = 0;

            if (read == 0)
            {
                if (loop && reader.Length > 0)
                {
                    try
                    {
                        reader.Position = 0;
                    }
                    catch (Exception)
                    {
                        // Fronteira nativa (Media Foundation) — mesma falha honesta de sempre: se
                        // nem pedir o reposicionamento pro loop funciona, desiste desta thread (sem
                        // loop possivel), video continua normal sem audio.
                        return;
                    }

                    continue;
                }

                // Fim natural sem loop — mesmo comportamento do antigo _audioCapturePlayer (so
                // tratava o proprio reinicio em loop, nunca disparava MediaEnded: essa e
                // responsabilidade exclusiva do player de video no PlaybackEngine). Encerra a
                // thread; nao ha mais nada pra entregar.
                return;
            }

            samplesDelivered += read / AudioChannels;

            // Correcao secundaria de deriva (ver comentario de classe) — checada por tempo de
            // parede, nao por numero de blocos, pra manter o intervalo real constante independente
            // do tamanho de bloco escolhido.
            if (lastDriftCheck.ElapsedMilliseconds >= DriftCheckIntervalMs)
            {
                lastDriftCheck.Restart();
                TryCorrectDrift(reader);
            }

            var block = new float[read];
            Array.Copy(buffer, block, read);
            AudioFrameReady?.Invoke(this, new EngineAudioFrameReadyEventArgs(block, AudioSampleRate, AudioChannels, read / AudioChannels));
        }
    }

    /// <summary>Compara a posicao atual do leitor de audio com a ultima posicao de video conhecida (<see cref="SyncToVideoPosition"/>) e reposiciona o leitor se a diferenca passar de <see cref="DriftCorrectionThresholdMs"/> — ver comentario de classe pro racional completo.</summary>
    private void TryCorrectDrift(AudioFileReader reader)
    {
        var videoTicks = Volatile.Read(ref _lastVideoPositionTicks);
        if (videoTicks < 0)
        {
            // Nenhuma posicao de video reportada ainda (Play acabou de comecar, ou o Deck nao tem
            // video real por algum motivo excepcional) — nada pra comparar, sem correcao.
            return;
        }

        TimeSpan audioPosition;
        try
        {
            audioPosition = reader.CurrentTime;
        }
        catch (Exception)
        {
            // Fronteira nativa (Media Foundation) — mesma falha honesta de sempre: desiste desta
            // correcao especifica, a proxima checagem (1s depois) tenta de novo.
            return;
        }

        var videoPosition = new TimeSpan(videoTicks);
        var driftMs = Math.Abs((audioPosition - videoPosition).TotalMilliseconds);

        if (driftMs < DriftCorrectionThresholdMs)
        {
            return;
        }

        try
        {
            reader.CurrentTime = videoPosition;
        }
        catch (Exception)
        {
            // Fronteira nativa — falha honesta, so desiste desta correcao especifica, a proxima
            // checagem (1s depois) tenta de novo.
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Clear();
    }
}
