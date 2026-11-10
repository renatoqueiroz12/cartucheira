using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace CartucheiraVideo.Engine;

/// <summary>
/// Motor de reprodução de áudio de UMA Cartucheira (Vinhetas OU Trilhas) — Sprint Cartucheira
/// (2026-08-09). Cada Cartucheira (Vinhetas, Trilhas) tem a sua própria instância — tocam de forma
/// totalmente independente, inclusive em dispositivos de saída de áudio diferentes (decisão do
/// usuário na proposta desta sprint).
///
/// Sprint Crossfade v1 (2026-08-11) — REVERTIDA no mesmo dia: chegou a existir aqui um mecanismo de
/// crossfade com dois <c>LibVLCSharp.Shared.MediaPlayer</c> simultâneos (volume ramp via Timer)
/// sobre a mesma LibVLC compartilhada do processo. Testado de verdade pelo usuário e confirmou, na
/// prática, um risco técnico real do LibVLCSharp: depois de um crossfade rodar, o áudio parou de
/// sair (progresso continuava andando, só sem som) nas DUAS Cartucheiras ao mesmo tempo, até fechar
/// e reabrir o programa.
///
/// Sprint Crossfade v2 (2026-08-11, mesmo dia) — causa raiz pesquisada e confirmada antes de tentar
/// de novo: o módulo de saída "mmdevice" do LibVLC (o padrão no Windows) controla volume via
/// <c>ISimpleAudioVolume::SetMasterVolume</c>, chamado com um GUID de sessão FIXO
/// (<c>GUID_VLC_AUD_OUT</c>) — ou seja, TODO <c>MediaPlayer</c> do processo que usa mmdevice entra
/// na MESMA sessão de volume do Windows, então mexer no volume de um mexe em todos. Bug real, aberto
/// e sem correção no rastreador oficial do LibVLCSharp (issues #451 e #623) — não era um erro deste
/// código, era o próprio alicerce (LibVLCSharp + mmdevice) que não sustenta volume por instância no
/// Windows.
///
/// Por isso esta classe passou a usar <b>NAudio</b> em vez de LibVLCSharp — só aqui, só nas duas
/// Cartucheiras. O <see cref="Engine.PlaybackEngine"/> do Deck (Preview/Program, NDI) continua 100%
/// LibVLCSharp, intocado, sem nenhum risco herdado desta troca. Desenho novo:
/// <list type="bullet">
/// <item>Um <see cref="MixingSampleProvider"/> ÚNICO e permanente por Cartucheira, alimentando um
/// único <see cref="WasapiOut"/> sempre tocando (mesmo sem nenhum cartucho ativo, tocando silêncio —
/// <c>ReadFully = true</c> garante isso, ver <see cref="EnsureOutputReady"/>). Cartuchos entram e
/// saem da mistura via <c>AddMixerInput</c>/<c>RemoveMixerInput</c>, sem nunca parar/recriar o
/// dispositivo de saída a cada clique — ponto a favor da prioridade #1 (Latência).</item>
/// <item>Volume de cada cartucho é o <see cref="AudioFileReader.Volume"/> da SUA PRÓPRIA instância
/// — um float puro, em memória gerenciada, sem nenhum estado nativo/de sessão do Windows
/// compartilhado entre cartuchos. Estruturalmente não tem como um cartucho vazar volume pro outro
/// neste desenho — é exatamente a causa raiz do problema da v1 que deixa de existir.</item>
/// <item>Crossfade = dois cartuchos na mistura ao mesmo tempo por um instante, um com
/// <see cref="AudioFileReader.Volume"/> descendo enquanto o outro sobe, via
/// <see cref="System.Threading.Timer"/> (mesma técnica/cadência da v1, <see cref="FadeTickMs"/>),
/// só que agora sobre volumes isolados por cartucho em vez de dois MediaPlayer nativos
/// compartilhando sessão.</item>
/// </list>
///
/// Contrapartida honesta desta troca: LibVLC decodifica praticamente qualquer formato sozinho; o
/// <see cref="AudioFileReader"/> do NAudio decodifica WAV/MP3/AIFF nativamente e qualquer outro
/// formato (WMA, AAC, M4A, FLAC) via Media Foundation do próprio Windows (sem pacote extra) — mas
/// Ogg Vorbis (.ogg) NÃO tem decoder nativo no Media Foundation padrão do Windows, então cartuchos
/// .ogg deixam de tocar (erro honesto via <see cref="ErrorOccurred"/>, nunca um cartucho mudo sem
/// aviso) a menos que o usuário tenha um decoder Ogg instalado no sistema (ex.: LAV Filters). Os
/// arquivos reais já vistos neste projeto são .wav — risco baixo na prática.
///
/// Sprint Deck-pausa-Cartucheira (2026-08-11): <see cref="Pause"/>/<see cref="Resume"/> tiram/devolvem
/// o <c>MixerInput</c> do cartucho em primeiro plano na mistura SEM descartar o
/// <see cref="AudioFileReader"/> (diferente de <see cref="Stop"/>, que sempre descarta) — o desenho
/// de mixer permanente desta classe já deixa isso simples: o <see cref="AudioFileReader.Position"/>
/// só avança quando o mixer lê dele, então tirá-lo da mistura já É pausar de verdade, exatamente de
/// onde parou, sem nenhum estado extra de "posição salva" pra gerenciar.
///
/// Sprint Deck-pausa-Cartucheira v3, fade ajustável (2026-08-11, mesmo dia): <see cref="Pause"/>/
/// <see cref="Resume"/> ganham um parâmetro <c>fadeMs</c> opcional — zero (padrão) continua sendo o
/// corte seco de sempre; maior que zero rampeia <see cref="AudioFileReader.Volume"/> do cartucho em
/// primeiro plano (mesma técnica/cadência do crossfade entre cartuchos, <see cref="FadeTickMs"/>, só
/// que sobre o MESMO cartucho em vez de dois) antes de tirar/devolver o <c>MixerInput</c> da
/// mistura. Estado de "em qual fase do duck estou" agora é um enum (<see cref="DuckPhase"/>) em vez
/// de um bool: um bool não bastava mais porque, com fade, dá pra Pause() chegar no MEIO de um fade
/// de entrada (Resume ainda rampeando) e vice-versa — o enum permite inverter a rampa a partir do
/// volume atual em vez de só ignorar o comando ou pular pro extremo.
/// </summary>
public sealed class AudioEngine : IDisposable
{
    /// <summary>Formato fixo do mixer (48kHz estéreo, float) — todo cartucho é adaptado (mono→estéreo, resample se a taxa do arquivo for diferente) pra este formato antes de entrar na mistura, já que <see cref="MixingSampleProvider"/> soma amostra a amostra e exige que todas as entradas estejam no mesmo formato.</summary>
    private static readonly WaveFormat MixerWaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);

    private const int FadeTickMs = 25;
    private const int ReportIntervalMs = 100;
    private const int OutputLatencyMs = 40;

    /// <summary>
    /// Prefixo que marca <see cref="SetOutputDevice"/> como uma "Saída Livewire" em vez de um
    /// dispositivo Windows de verdade — Sprint Livewire-2 (2026-09-13). Valor completo gravado nos
    /// campos existentes (ex.: <c>CartridgeSettingsSnapshot.VinhetasAudioDeviceId</c>) fica
    /// <c>"livewire:{Id}"</c>, onde <c>Id</c> é o <c>LivewireOutputSettings.Id</c> configurado em
    /// Configurações — escolhido de propósito pra não precisar mudar o tipo (<c>string?</c>) nem o
    /// schema já persistido desses campos. Quando detectado, <see cref="EnsureOutputReady"/> NÃO cria
    /// um <see cref="WasapiOut"/> — em vez disso dispara <see cref="ExternalAudioBlockReady"/> no
    /// ritmo do Livewire (240 quadros/5ms), pra quem estiver ouvindo (normalmente
    /// <c>Presentation.AudioBankViewModel</c>, ligando num <c>Infrastructure.Livewire.LivewireSender</c>
    /// — Engine não pode referenciar Infrastructure diretamente, regra permanente do projeto, mesmo
    /// padrão já usado pro NDI) mandar pela rede.
    /// </summary>
    public const string ExternalOutputPrefix = "livewire:";

    /// <summary>
    /// Quantos quadros (amostras por canal) tem cada bloco entregue via
    /// <see cref="ExternalAudioBlockReady"/> — mesmo tamanho de um pacote RTP/L24 Livewire (240
    /// quadros = 5ms a 48kHz, ver <c>Infrastructure.Livewire.LivewireRtpFraming.FramesPerPacket</c>).
    /// Repetido aqui como constante própria (não referenciado direto do Infrastructure) porque Engine
    /// não pode referenciar Infrastructure — regra permanente do projeto.
    /// </summary>
    public const int ExternalOutputFrameCount = 240;

    private readonly object _gate = new();
    private readonly MixingSampleProvider _mixer;
    private readonly Timer _reportTimer;

    private WasapiOut? _wasapiOut;
    private MMDevice? _currentDevice;
    private string? _outputDeviceId;
    private string? _initializedDeviceId;
    private Thread? _externalOutputThread;
    private CancellationTokenSource? _externalOutputCts;

    private PlayingItem? _active;
    private PlayingItem? _fadingOut;
    private Timer? _fadeTimer;
    private Stopwatch? _fadeStopwatch;
    private float _fadeStartVolume;
    private int _crossfadeMs;

    /// <summary>Fase atual do duck (Pause/Resume) do cartucho em primeiro plano — ver <see cref="Pause"/>/<see cref="Resume"/>/<see cref="OnDuckTick"/>.</summary>
    private enum DuckPhase
    {
        /// <summary>Tocando normalmente, no mixer, volume 1.</summary>
        Resumed,
        /// <summary>Rampeando o volume pra 0 — ainda no mixer (sendo lido/ouvido) até a rampa terminar.</summary>
        FadingOut,
        /// <summary>Fora do mixer, silencioso — <see cref="AudioFileReader.Position"/> parada.</summary>
        Paused,
        /// <summary>De volta no mixer, rampeando o volume de onde estava até 1.</summary>
        FadingIn
    }

    private DuckPhase _duckPhase = DuckPhase.Resumed;
    private Timer? _duckTimer;
    private Stopwatch? _duckStopwatch;
    private float _duckStartVolume;
    private int _duckFadeMs;

    private bool _disposed;

    public AudioEngine()
    {
        _mixer = new MixingSampleProvider(MixerWaveFormat) { ReadFully = true };
        _reportTimer = new Timer(OnReportTick, null, ReportIntervalMs, ReportIntervalMs);
    }

    /// <summary>Disparado quando o cartucho em primeiro plano termina de tocar sozinho (thread de áudio do NAudio — quem ouvir precisa marshalar pra UI thread sozinho, mesmo padrão de sempre desta classe).</summary>
    public event EventHandler? MediaEnded;

    /// <summary>Disparado quando um cartucho não pôde ser aberto/tocado (arquivo corrompido, formato não suportado — ex.: Ogg Vorbis sem decoder no sistema, ou mais de 2 canais).</summary>
    public event EventHandler<string>? ErrorOccurred;

    /// <summary>Disparado a cada <see cref="ReportIntervalMs"/> enquanto algo está tocando, com o tempo decorrido do cartucho em primeiro plano — alimenta a contagem regressiva (<see cref="GetDurationAsync"/> dá a duração total).</summary>
    public event EventHandler<TimeSpan>? PositionChanged;

    /// <summary>
    /// Sprint Livewire-2 (2026-09-13): disparado a cada <see cref="ExternalOutputFrameCount"/> quadros
    /// (5ms) enquanto <see cref="SetOutputDevice"/> aponta pra uma Saída Livewire (<see cref="ExternalOutputPrefix"/>)
    /// — SÓ nesse caso; enquanto o destino for um dispositivo Windows normal (<see cref="WasapiOut"/>)
    /// este evento nunca dispara. Disparado na thread dedicada de ritmo Livewire desta instância (ver
    /// <see cref="ExternalOutputThreadLoop"/>), nunca na UI thread — mesma responsabilidade de sempre
    /// de marshalar pra quem ouvir precisar.
    /// </summary>
    public event EventHandler<ExternalAudioBlockEventArgs>? ExternalAudioBlockReady;

    /// <summary>Verdadeiro enquanto há um cartucho em primeiro plano tocando (não conta o cartucho saindo de um crossfade em andamento).</summary>
    public bool IsPlaying
    {
        get { lock (_gate) { return _active is not null; } }
    }

    /// <summary>
    /// Lista os dispositivos de saída de áudio disponíveis no Windows (WASAPI, via NAudio
    /// <see cref="MMDeviceEnumerator"/>) — mesma ideia/formato da versão LibVLC anterior, agora sem
    /// depender de nenhuma <c>LibVLC</c> pra enumerar. Estático porque não depende de nenhum
    /// <see cref="AudioEngine"/> tocando algo.
    /// </summary>
    public static IReadOnlyList<(string DeviceId, string Description)> EnumerateOutputDevices()
    {
        using var enumerator = new MMDeviceEnumerator();
        var result = new List<(string DeviceId, string Description)>();

        foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
        {
            result.Add((device.ID, device.FriendlyName));
            device.Dispose();
        }

        return result;
    }

    /// <summary>
    /// Lê a duração real de um arquivo de áudio SEM tocar ele — só abre o cabeçalho via
    /// <see cref="AudioFileReader"/> (rápido, não decodifica o arquivo inteiro) numa thread separada.
    /// Retorna nulo em qualquer falha (arquivo corrompido, formato não suportado): nenhum valor
    /// inventado, mesmo padrão honesto de sempre desta classe.
    /// </summary>
    public static Task<TimeSpan?> GetDurationAsync(string filePath, CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            try
            {
                using var reader = new AudioFileReader(filePath);
                return (TimeSpan?)reader.TotalTime;
            }
            catch (Exception)
            {
                return (TimeSpan?)null;
            }
        }, cancellationToken);
    }

    /// <summary>Define o dispositivo de saída de áudio — aplicado no próximo <see cref="Play"/> (ver <see cref="EnsureOutputReady"/>), nunca no meio de uma reprodução em andamento. Nulo = dispositivo padrão do sistema.</summary>
    public void SetOutputDevice(string? deviceId)
    {
        lock (_gate)
        {
            _outputDeviceId = deviceId;
        }
    }

    /// <summary>Duração (ms) do crossfade aplicado no próximo <see cref="Play"/> que encontrar algo já tocando. Zero = corte seco (comportamento de sempre). Sem limite aqui — o clamp 0–3000 já acontece na UI (<c>CartridgeSettingsViewModel</c>).</summary>
    public void SetCrossfadeDuration(int milliseconds)
    {
        lock (_gate)
        {
            _crossfadeMs = Math.Max(0, milliseconds);
        }
    }

    /// <summary>
    /// Toca um cartucho. Se já havia outro tocando em primeiro plano: com crossfade configurado
    /// (<see cref="SetCrossfadeDuration"/>) os dois ficam misturados por essa duração, um saindo
    /// enquanto o outro entra; sem crossfade (0ms, padrão), o anterior é cortado na hora — mesmo
    /// comportamento de sempre. Substitui o antigo par Load()+Play() da versão LibVLC: aqui não dá
    /// pra separar "carregar" de "tocar" porque decidir crossfade-ou-corte-seco depende de já saber
    /// o que estava tocando antes.
    /// </summary>
    public void Play(string filePath, bool loopEnabled)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            throw new ArgumentException("Caminho do arquivo nao pode ser vazio.", nameof(filePath));
        }

        EnsureOutputReady();

        AudioFileReader reader;
        try
        {
            reader = new AudioFileReader(filePath);
        }
        catch (Exception)
        {
            ErrorOccurred?.Invoke(this, "Não foi possível abrir este cartucho (arquivo corrompido ou formato não suportado).");
            return;
        }

        ISampleProvider mixerInput;
        CartridgeSampleProvider cartridgeProvider;
        try
        {
            mixerInput = BuildMixerInput(reader, loopEnabled, out cartridgeProvider);
        }
        catch (Exception)
        {
            reader.Dispose();
            ErrorOccurred?.Invoke(this, "Formato de áudio deste cartucho não é suportado (só mono ou estéreo).");
            return;
        }

        var incoming = new PlayingItem(reader, mixerInput);
        cartridgeProvider.Ended += () => OnCartridgeEnded(incoming);

        lock (_gate)
        {
            CancelFadeImmediately();

            var outgoing = _active;
            var crossfade = outgoing is not null && _crossfadeMs > 0;

            // Sprint fade ajustável: um duck (Pause/Resume) do cartucho ANTERIOR pode estar em
            // andamento (timer + Stopwatch próprios, ver DuckPhase) — cancela sem terminar (o
            // outgoing já vai ser descartado ou crossfadado logo abaixo, de um jeito ou de outro) e
            // reseta a fase pro cartucho NOVO, que sempre começa tocando normalmente, nunca pausado.
            CancelDuckFadeImmediately();

            reader.Volume = crossfade ? 0f : 1f;
            _mixer.AddMixerInput(mixerInput);
            _active = incoming;
            _duckPhase = DuckPhase.Resumed;

            if (outgoing is null)
            {
                return;
            }

            if (!crossfade)
            {
                RemoveAndDispose(outgoing);
                return;
            }

            _fadingOut = outgoing;
            _fadeStartVolume = outgoing.Reader.Volume;
            _fadeStopwatch = Stopwatch.StartNew();
            _fadeTimer = new Timer(OnFadeTick, null, FadeTickMs, FadeTickMs);
        }
    }

    /// <summary>Para o cartucho em primeiro plano na hora — cancela qualquer crossfade em andamento também (o que estava saindo é descartado imediatamente, sem terminar a mistura).</summary>
    public void Stop()
    {
        lock (_gate)
        {
            CancelFadeImmediately();
            CancelDuckFadeImmediately();

            if (_active is not null)
            {
                RemoveAndDispose(_active);
                _active = null;
                _duckPhase = DuckPhase.Resumed;
            }
        }
    }

    /// <summary>
    /// Sprint Deck-pausa-Cartucheira (2026-08-11): pausa o cartucho em primeiro plano SEM descartá-lo
    /// — tira o <c>MixerInput</c> da mistura (WasapiOut continua tocando — <c>ReadFully</c> — só que
    /// sem ler nada deste cartucho) mantendo o <see cref="AudioFileReader"/> vivo, com a
    /// <see cref="AudioFileReader.Position"/> exatamente onde estava, pronta pra <see cref="Resume"/>
    /// continuar do mesmo ponto. Diferente de <see cref="Stop"/> (que descarta o Reader — o próximo
    /// <see cref="Play"/> sempre recomeça do início). Cancela qualquer crossfade entre CARTUCHOS
    /// diferentes em andamento primeiro (mesmo racional de <see cref="Stop"/>: o que estava saindo é
    /// descartado na hora, nunca fica "pausado" junto — só o cartucho em primeiro plano participa
    /// desta pausa).
    ///
    /// <paramref name="fadeMs"/> (Sprint fade ajustável, 2026-08-11): zero (padrão) = corte seco
    /// imediato, mesmo comportamento de sempre. Maior que zero rampeia o volume do cartucho até 0 ao
    /// longo desse tempo (ele continua audível, cada vez mais baixo, até a rampa terminar) antes de
    /// sair da mistura de verdade. Sem efeito se nada estiver tocando ou já estiver pausado/pausando
    /// (idempotente). Chamado no MEIO de um <see cref="Resume"/> com fade ainda em andamento: inverte
    /// a rampa a partir do volume atual (nunca salta de volta pro início) — ver <see cref="DuckPhase"/>.
    /// </summary>
    public void Pause(int fadeMs = 0)
    {
        lock (_gate)
        {
            CancelFadeImmediately();

            if (_active is null || _duckPhase is DuckPhase.Paused or DuckPhase.FadingOut)
            {
                return;
            }

            var startVolume = _active.Reader.Volume;
            CancelDuckFadeImmediately();

            fadeMs = Math.Max(0, fadeMs);
            if (fadeMs <= 0 || startVolume <= 0f)
            {
                FinishDuckOutNoLock();
                return;
            }

            _duckPhase = DuckPhase.FadingOut;
            _duckStartVolume = startVolume;
            _duckFadeMs = fadeMs;
            _duckStopwatch = Stopwatch.StartNew();
            _duckTimer = new Timer(OnDuckTick, null, FadeTickMs, FadeTickMs);
        }
    }

    /// <summary>
    /// Retoma o cartucho pausado (ou sendo pausado) por <see cref="Pause"/> exatamente de onde parou
    /// (mesma <see cref="AudioFileReader.Position"/>, nunca reiniciado). <paramref name="fadeMs"/>
    /// (Sprint fade ajustável, 2026-08-11): zero (padrão) = corte seco imediato, volume direto em 1;
    /// maior que zero devolve o cartucho à mistura já rampeando o volume de onde estiver (0 se já
    /// tinha terminado de pausar, ou o valor atual se chamado no meio de um <see cref="Pause"/> com
    /// fade ainda em andamento — inverte a rampa em vez de saltar) até 1. Sem efeito, idempotente, se
    /// nada estiver tocando ou já estiver tocando/retomando.
    /// </summary>
    public void Resume(int fadeMs = 0)
    {
        lock (_gate)
        {
            if (_active is null || _duckPhase is DuckPhase.Resumed or DuckPhase.FadingIn)
            {
                return;
            }

            var startVolume = _duckPhase == DuckPhase.FadingOut ? _active.Reader.Volume : 0f;

            if (_duckPhase == DuckPhase.Paused)
            {
                // Ainda fora do mixer — devolve pra mistura já em silêncio (0), pra rampa (ou o corte
                // seco abaixo) começar de um jeito seguro, sem pulo de volume audível.
                _active.Reader.Volume = 0f;
                _mixer.AddMixerInput(_active.MixerInput);
            }

            CancelDuckFadeImmediately();

            fadeMs = Math.Max(0, fadeMs);
            if (fadeMs <= 0)
            {
                FinishDuckInNoLock();
                return;
            }

            _duckPhase = DuckPhase.FadingIn;
            _duckStartVolume = startVolume;
            _duckFadeMs = fadeMs;
            _duckStopwatch = Stopwatch.StartNew();
            _duckTimer = new Timer(OnDuckTick, null, FadeTickMs, FadeTickMs);
        }
    }

    private void OnDuckTick(object? state)
    {
        lock (_gate)
        {
            var item = _active;
            var stopwatch = _duckStopwatch;

            if (item is null || stopwatch is null)
            {
                return;
            }

            var fadingOut = _duckPhase == DuckPhase.FadingOut;
            var t = Math.Clamp(stopwatch.Elapsed.TotalMilliseconds / _duckFadeMs, 0.0, 1.0);
            var targetVolume = fadingOut ? 0f : 1f;

            item.Reader.Volume = (float)(_duckStartVolume + (targetVolume - _duckStartVolume) * t);

            if (t >= 1.0)
            {
                var timer = _duckTimer;
                _duckTimer = null;
                _duckStopwatch = null;

                if (fadingOut)
                {
                    FinishDuckOutNoLock();
                }
                else
                {
                    FinishDuckInNoLock();
                }

                timer?.Dispose();
            }
        }
    }

    /// <summary>Chamado só de dentro de um bloco que já segura <see cref="_gate"/>. Tira o cartucho em primeiro plano da mistura de vez (fase <see cref="DuckPhase.Paused"/>) e reseta o volume pra 1 — baseline limpo pro próximo <see cref="Resume"/>/re-entrada na mistura.</summary>
    private void FinishDuckOutNoLock()
    {
        if (_active is not null)
        {
            _mixer.RemoveMixerInput(_active.MixerInput);
            _active.Reader.Volume = 1f;
        }

        _duckPhase = DuckPhase.Paused;
    }

    /// <summary>Chamado só de dentro de um bloco que já segura <see cref="_gate"/>. Cartucho já está na mistura (ver <see cref="Resume"/>) — só garante o volume final em 1 e marca a fase <see cref="DuckPhase.Resumed"/>.</summary>
    private void FinishDuckInNoLock()
    {
        if (_active is not null)
        {
            _active.Reader.Volume = 1f;
        }

        _duckPhase = DuckPhase.Resumed;
    }

    /// <summary>Chamado só de dentro de um bloco que já segura <see cref="_gate"/> — só derruba o timer/Stopwatch do duck em andamento, SEM decidir o estado final (quem chama decide: <see cref="Pause"/>/<see cref="Resume"/> continuam pra <see cref="FinishDuckOutNoLock"/>/<see cref="FinishDuckInNoLock"/> ou pra uma nova rampa; <see cref="Play"/>/<see cref="Stop"/> descartam o cartucho inteiro logo em seguida, o estado do duck deixa de importar).</summary>
    private void CancelDuckFadeImmediately()
    {
        var timer = _duckTimer;
        _duckTimer = null;
        _duckStopwatch = null;
        timer?.Dispose();
    }

    /// <summary>
    /// (Re)cria o <see cref="WasapiOut"/> apontado pro dispositivo configurado (<see cref="SetOutputDevice"/>)
    /// — só faz o trabalho pesado (abrir dispositivo, iniciar o stream) quando ainda não existe
    /// nenhum ou quando o dispositivo configurado mudou desde a última vez; caso contrário retorna
    /// na hora, sem custo, pra não pagar esse preço a cada cartucho disparado (prioridade #1,
    /// Latência). O <see cref="_mixer"/> é permanente e sobrevive à troca de dispositivo — só quem
    /// lê dele (o WasapiOut) é recriado.
    /// </summary>
    private void EnsureOutputReady()
    {
        string? wantedDeviceId;
        lock (_gate)
        {
            var alreadyReady = _initializedDeviceId == _outputDeviceId
                && (_wasapiOut is not null || _externalOutputThread is not null);

            if (alreadyReady)
            {
                return;
            }

            wantedDeviceId = _outputDeviceId;
        }

        // Sprint Livewire-2: valor prefixado ("livewire:{Id}") significa Saída Livewire — não é um
        // dispositivo Windows de verdade, então nunca passa pelo caminho MMDevice/WasapiOut abaixo.
        // Ver comentário de ExternalOutputPrefix.
        if (wantedDeviceId is not null && wantedDeviceId.StartsWith(ExternalOutputPrefix, StringComparison.Ordinal))
        {
            StartExternalOutput(wantedDeviceId);
        }
        else
        {
            StartWasapiOutput(wantedDeviceId);
        }
    }

    /// <summary>Caminho normal (dispositivo Windows de verdade) — comportamento idêntico ao de antes da Sprint Livewire-2, só extraído pra um método próprio pra caber ao lado do novo caminho <see cref="StartExternalOutput"/>.</summary>
    private void StartWasapiOutput(string? wantedDeviceId)
    {
        using var enumerator = new MMDeviceEnumerator();
        MMDevice device;
        try
        {
            device = string.IsNullOrEmpty(wantedDeviceId)
                ? enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia)
                : enumerator.GetDevice(wantedDeviceId);
        }
        catch (Exception)
        {
            // Dispositivo salvo não existe mais (removido/desconectado) — cai pro padrão do sistema,
            // nunca inventa um dispositivo, mesmo padrão honesto de SetOutputDevice de sempre.
            device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        }

        var wasapiOut = new WasapiOut(device, AudioClientShareMode.Shared, true, OutputLatencyMs);
        wasapiOut.Init(_mixer);
        wasapiOut.Play();

        WasapiOut? previousOut;
        MMDevice? previousDevice;
        Thread? previousExternalThread;
        CancellationTokenSource? previousExternalCts;

        lock (_gate)
        {
            previousOut = _wasapiOut;
            previousDevice = _currentDevice;
            previousExternalThread = _externalOutputThread;
            previousExternalCts = _externalOutputCts;

            _wasapiOut = wasapiOut;
            _currentDevice = device;
            _externalOutputThread = null;
            _externalOutputCts = null;
            _initializedDeviceId = wantedDeviceId;
        }

        previousOut?.Stop();
        previousOut?.Dispose();
        previousDevice?.Dispose();
        StopExternalOutputThread(previousExternalThread, previousExternalCts);
    }

    /// <summary>
    /// Sprint Livewire-2: caminho novo pra quando <see cref="SetOutputDevice"/> aponta pra uma Saída
    /// Livewire — NÃO cria <see cref="WasapiOut"/> nenhum. Em vez disso sobe uma thread dedicada
    /// (<see cref="ExternalOutputThreadLoop"/>) que lê o MESMO <see cref="_mixer"/> permanente desta
    /// Cartucheira no ritmo do Livewire (240 quadros/5ms, relógio de parede — mesma técnica de
    /// <c>Engine.NdiAudioCapture.AudioThreadLoop</c>, só sem correção de deriva porque aqui não existe
    /// nenhum vídeo pra sincronizar) e dispara <see cref="ExternalAudioBlockReady"/> a cada bloco.
    /// </summary>
    private void StartExternalOutput(string wantedDeviceId)
    {
        var outputId = wantedDeviceId.Substring(ExternalOutputPrefix.Length);
        var cts = new CancellationTokenSource();
        var thread = new Thread(() => ExternalOutputThreadLoop(outputId, cts.Token))
        {
            IsBackground = true,
            Name = "AudioEngine-Livewire-" + outputId,
        };

        WasapiOut? previousOut;
        MMDevice? previousDevice;
        Thread? previousExternalThread;
        CancellationTokenSource? previousExternalCts;

        lock (_gate)
        {
            previousOut = _wasapiOut;
            previousDevice = _currentDevice;
            previousExternalThread = _externalOutputThread;
            previousExternalCts = _externalOutputCts;

            _wasapiOut = null;
            _currentDevice = null;
            _externalOutputThread = thread;
            _externalOutputCts = cts;
            _initializedDeviceId = wantedDeviceId;
        }

        previousOut?.Stop();
        previousOut?.Dispose();
        previousDevice?.Dispose();
        StopExternalOutputThread(previousExternalThread, previousExternalCts);

        thread.Start();
    }

    /// <summary>Pra sempre — pede cancelamento e espera a thread terminar sozinha (nunca <c>Abort</c>) antes de descartar o <see cref="CancellationTokenSource"/>. Seguro chamar com <paramref name="thread"/> nulo (nenhuma Saída Livewire estava ativa).</summary>
    private static void StopExternalOutputThread(Thread? thread, CancellationTokenSource? cts)
    {
        if (thread is null)
        {
            return;
        }

        cts?.Cancel();
        thread.Join(500);
        cts?.Dispose();
    }

    /// <summary>
    /// Roda inteira numa thread própria (uma por Saída Livewire ativa) até <paramref name="token"/>
    /// ser cancelado. Ritmo por relógio de parede (mesma técnica de
    /// <c>Engine.NdiAudioCapture.AudioThreadLoop</c>): calcula quantos quadros DEVERIAM ter saído até
    /// agora e só lê/dispara mais um bloco quando alcança esse alvo — evita tanto acumular atraso
    /// (nunca dorme além da conta) quanto rodar mais rápido que o relógio real (nunca manda áudio
    /// "adiantado"). <see cref="_mixer"/> tem <c>ReadFully = true</c>, então <c>Read</c> sempre volta
    /// com o bloco cheio (silêncio se nada estiver tocando) — nunca menos que <see cref="ExternalOutputFrameCount"/>.
    /// </summary>
    private void ExternalOutputThreadLoop(string outputId, CancellationToken token)
    {
        var stopwatch = Stopwatch.StartNew();
        long framesDelivered = 0;
        var sampleRate = MixerWaveFormat.SampleRate;
        var interleaved = new float[ExternalOutputFrameCount * 2];

        while (!token.IsCancellationRequested)
        {
            var targetFrames = stopwatch.Elapsed.TotalSeconds * sampleRate;
            if (framesDelivered >= targetFrames)
            {
                Thread.Sleep(1);
                continue;
            }

            _mixer.Read(interleaved, 0, interleaved.Length);

            var left = new float[ExternalOutputFrameCount];
            var right = new float[ExternalOutputFrameCount];
            for (var i = 0; i < ExternalOutputFrameCount; i++)
            {
                left[i] = interleaved[i * 2];
                right[i] = interleaved[i * 2 + 1];
            }

            framesDelivered += ExternalOutputFrameCount;

            if (token.IsCancellationRequested)
            {
                return;
            }

            ExternalAudioBlockReady?.Invoke(this, new ExternalAudioBlockEventArgs(outputId, left, right));
        }
    }

    /// <summary>Adapta um cartucho recém-aberto pro formato fixo do mixer (<see cref="MixerWaveFormat"/>): mono vira estéreo, taxa de amostragem diferente é reamostrada (<see cref="WdlResamplingSampleProvider"/>, resampler 100% gerenciado do próprio NAudio, sem dependência do Windows). Mais de 2 canais não é suportado (nenhum cartucho real deste projeto até agora tem isso) — erro honesto em vez de tentar adivinhar um downmix.</summary>
    private ISampleProvider BuildMixerInput(AudioFileReader reader, bool loop, out CartridgeSampleProvider cartridgeProvider)
    {
        cartridgeProvider = new CartridgeSampleProvider(reader, loop);
        ISampleProvider chain = cartridgeProvider;

        if (chain.WaveFormat.Channels == 1 && MixerWaveFormat.Channels == 2)
        {
            chain = new MonoToStereoSampleProvider(chain);
        }
        else if (chain.WaveFormat.Channels != MixerWaveFormat.Channels)
        {
            throw new NotSupportedException($"Arquivo com {chain.WaveFormat.Channels} canal(is) não suportado (só mono ou estéreo).");
        }

        if (chain.WaveFormat.SampleRate != MixerWaveFormat.SampleRate)
        {
            chain = new WdlResamplingSampleProvider(chain, MixerWaveFormat.SampleRate);
        }

        return chain;
    }

    private void OnFadeTick(object? state)
    {
        lock (_gate)
        {
            var outgoing = _fadingOut;
            var incoming = _active;
            var stopwatch = _fadeStopwatch;

            if (outgoing is null || incoming is null || stopwatch is null)
            {
                return;
            }

            var t = Math.Clamp(stopwatch.Elapsed.TotalMilliseconds / _crossfadeMs, 0.0, 1.0);

            outgoing.Reader.Volume = (float)(_fadeStartVolume * (1.0 - t));
            incoming.Reader.Volume = (float)t;

            if (t >= 1.0)
            {
                FinishFadeNoLock();
            }
        }
    }

    /// <summary>Chamado só de dentro de um bloco que já segura <see cref="_gate"/> (<c>lock</c> do C# é reentrante na mesma thread — seguro chamar aninhado).</summary>
    private void FinishFadeNoLock()
    {
        var timer = _fadeTimer;
        _fadeTimer = null;
        _fadeStopwatch = null;

        var outgoing = _fadingOut;
        _fadingOut = null;

        if (outgoing is not null)
        {
            RemoveAndDispose(outgoing);
        }

        if (_active is not null)
        {
            _active.Reader.Volume = 1f;
        }

        timer?.Dispose();
    }

    /// <summary>Cancela um crossfade em andamento na hora (descarta o cartucho que estava saindo) — chamado no início de todo <see cref="Play"/> novo (pra nunca ter mais de um "saindo" ao mesmo tempo) e por <see cref="Stop"/>.</summary>
    private void CancelFadeImmediately()
    {
        lock (_gate)
        {
            var timer = _fadeTimer;
            _fadeTimer = null;
            _fadeStopwatch = null;

            var outgoing = _fadingOut;
            _fadingOut = null;

            if (outgoing is not null)
            {
                RemoveAndDispose(outgoing);
            }

            timer?.Dispose();
        }
    }

    private void RemoveAndDispose(PlayingItem item)
    {
        _mixer.RemoveMixerInput(item.MixerInput);
        item.Reader.Dispose();
    }

    /// <summary>
    /// Chamado (na thread de áudio do NAudio) quando um cartucho chega ao fim sozinho — nunca para
    /// um em loop (ver <see cref="CartridgeSampleProvider"/>, que reinicia sozinho por dentro em vez
    /// de terminar). Como o mixer usa <c>ReadFully = true</c> (pra sempre tocar silêncio em vez de
    /// "acabar" quando fica vazio), um cartucho terminado NUNCA sai sozinho da mistura — este é o
    /// único lugar que garante a remoção/descarte dele.
    /// </summary>
    private void OnCartridgeEnded(PlayingItem item)
    {
        bool isForeground;

        lock (_gate)
        {
            isForeground = ReferenceEquals(_active, item);

            if (isForeground)
            {
                _active = null;
                CancelDuckFadeImmediately();
                _duckPhase = DuckPhase.Resumed;

                // O cartucho que estava ENTRANDO terminou sozinho antes do crossfade acabar (arquivo
                // mais curto que a duração configurada) — não sobra mais nada pra misturar, então
                // finaliza o fade na hora (descarta o que estava saindo) em vez de deixá-lo preso
                // esperando um próximo tick que nunca mais faria sentido.
                if (_fadingOut is not null)
                {
                    FinishFadeNoLock();
                }
            }
            else if (!ReferenceEquals(_fadingOut, item))
            {
                // Nem o atual, nem quem está saindo agora — só pode ser um item já removido por outro
                // caminho; RemoveAndDispose é idempotente (RemoveMixerInput de algo que já saiu é
                // no-op), mantido aqui só como rede de segurança honesta, nunca deveria disparar.
                RemoveAndDispose(item);
            }
        }

        if (isForeground)
        {
            MediaEnded?.Invoke(this, EventArgs.Empty);
        }
    }

    private void OnReportTick(object? state)
    {
        TimeSpan elapsed;

        lock (_gate)
        {
            if (_active is null)
            {
                return;
            }

            elapsed = _active.Reader.CurrentTime;
        }

        PositionChanged?.Invoke(this, elapsed);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        _reportTimer.Dispose();

        lock (_gate)
        {
            CancelFadeImmediately();
            CancelDuckFadeImmediately();

            if (_active is not null)
            {
                RemoveAndDispose(_active);
                _active = null;
                _duckPhase = DuckPhase.Resumed;
            }
        }

        _wasapiOut?.Stop();
        _wasapiOut?.Dispose();
        _currentDevice?.Dispose();
        StopExternalOutputThread(_externalOutputThread, _externalOutputCts);
    }

    private sealed class PlayingItem
    {
        public PlayingItem(AudioFileReader reader, ISampleProvider mixerInput)
        {
            Reader = reader;
            MixerInput = mixerInput;
        }

        public AudioFileReader Reader { get; }

        public ISampleProvider MixerInput { get; }
    }

    /// <summary>
    /// Envolve o <see cref="AudioFileReader"/> de um cartucho: trata loop (rebobina sozinho por
    /// dentro do <see cref="Read"/> em vez de terminar — gapless de verdade, sem o vaivém de thread
    /// que a versão LibVLC precisava) e detecta o fim natural (dispara <see cref="Ended"/> uma única
    /// vez, na thread de áudio — quem ouve já marshala pra UI thread, ver <see cref="OnCartridgeEnded"/>).
    /// </summary>
    private sealed class CartridgeSampleProvider : ISampleProvider
    {
        private readonly AudioFileReader _reader;
        private readonly bool _loop;
        private bool _endedFired;

        public CartridgeSampleProvider(AudioFileReader reader, bool loop)
        {
            _reader = reader;
            _loop = loop;
        }

        public WaveFormat WaveFormat => _reader.WaveFormat;

        public event Action? Ended;

        public int Read(float[] buffer, int offset, int count)
        {
            int read;
            try
            {
                read = _reader.Read(buffer, offset, count);
            }
            catch (ObjectDisposedException)
            {
                // Corrida rara e inofensiva: o leitor foi descartado por RemoveAndDispose bem no
                // instante em que o mixer ainda tinha um Read() em voo pra ele — trata como fim
                // normal (silêncio), nunca derruba a thread de áudio.
                return 0;
            }

            if (read == 0)
            {
                if (_loop && _reader.Length > 0)
                {
                    _reader.Position = 0;
                    try
                    {
                        read = _reader.Read(buffer, offset, count);
                    }
                    catch (ObjectDisposedException)
                    {
                        return 0;
                    }
                }
                else if (!_endedFired)
                {
                    _endedFired = true;
                    Ended?.Invoke();
                }
            }

            return read;
        }
    }
}

/// <summary>
/// Sprint Livewire-2 (2026-09-13): um bloco de <see cref="AudioEngine.ExternalOutputFrameCount"/>
/// quadros de áudio (48kHz, float -1..1, desintercalado em <see cref="Left"/>/<see cref="Right"/>)
/// prontos pra virar um pacote Livewire — disparado por <see cref="AudioEngine.ExternalAudioBlockReady"/>.
/// Público (não aninhado em <see cref="AudioEngine"/>) porque quem ouve fica em
/// <c>CartucheiraVideo.Presentation</c> (Engine não pode referenciar Infrastructure diretamente pra
/// já entregar isto como um tipo do próprio Livewire — regra permanente do projeto — então
/// Presentation é quem faz a ponte: lê <see cref="OutputId"/>, decide qual
/// <c>Infrastructure.Livewire.LivewireSender</c> corresponde, e chama
/// <c>LivewireSender.SendFrame(Left, Right)</c>).
/// </summary>
public sealed class ExternalAudioBlockEventArgs : EventArgs
{
    public ExternalAudioBlockEventArgs(string outputId, float[] left, float[] right)
    {
        OutputId = outputId;
        Left = left;
        Right = right;
    }

    /// <summary>Id da Saída Livewire (a parte depois do prefixo "livewire:", ver <see cref="AudioEngine.ExternalOutputPrefix"/>) — igual ao <c>LivewireOutputSettings.Id</c> configurado.</summary>
    public string OutputId { get; }

    /// <summary><see cref="AudioEngine.ExternalOutputFrameCount"/> amostras do canal esquerdo, float -1..1.</summary>
    public float[] Left { get; }

    /// <summary><see cref="AudioEngine.ExternalOutputFrameCount"/> amostras do canal direito, float -1..1.</summary>
    public float[] Right { get; }
}
