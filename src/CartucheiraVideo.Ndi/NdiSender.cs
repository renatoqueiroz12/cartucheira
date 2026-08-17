using System.Runtime.InteropServices;

namespace CartucheiraVideo.Ndi;

/// <summary>
/// Emissor NDI de um unico Deck — "sempre quente" (arquitetura, pesquisa-ndi-switching.md: fonte
/// NDI criada uma vez e mantida viva pela vida do Deck, nao recriada a cada Play). Escopo original
/// (MVP, Sprint NDI): so video, formato fixo 1920x1080 BGRA — quem decide QUANDO enviar frames (so
/// durante Playing, arquitetura ainda nao cobre Cue/Stopped idle-frame) e o chamador (DeckViewModel),
/// nao esta classe. Sprint NDI-audio (2026-08-11): ganhou <see cref="SendAudioFrame"/>, mesma
/// filosofia (o chamador decide quando chamar, esta classe so encaminha pra NDI SDK).
///
/// <see cref="SendFrame"/>/<see cref="SendAudioFrame"/> usam as variantes NAO-async da NDI SDK
/// (NDIlib_send_send_video_v2/NDIlib_send_send_audio_v2), que copiam o buffer internamente antes de
/// retornar — por isso e seguro o chamador reusar/descartar o buffer de origem (ex.: o buffer fixo
/// de video do PlaybackEngine, ou o buffer de audio gerenciado pelo proprio LibVLC) imediatamente
/// apos a chamada retornar, sem nenhuma sincronizacao adicional.
///
/// HISTORICO COMPLETO do audio NDI (documentado aqui pra nao repetir nenhuma tentativa ja
/// descartada numa sessao futura — ver tambem o doc do projeto, sprint 2026-08-11):
/// 1ª tentativa: funcao utilitaria <c>NDIlib_util_send_send_audio_interleaved_32f</c>, buffer
///    intercalado direto — <see cref="System.AccessViolationException"/> ao vivo.
/// 2ª tentativa: trocada pela funcao "nativa" <c>NDIlib_send_send_audio_v2</c> (PLANAR, a mesma que
///    exemplos oficiais/wrappers C#/.NET reais de producao usam, confirmado contra
///    <c>vvvv/VL.IO.NDI</c>), com desintercalacao manual aqui — <see cref="System.ExecutionEngineException"/>
///    ao vivo, idêntico.
/// 3ª tentativa: adicionado lock contra corrida de threads nos buffers nativos reusados entre
///    chamadas — MESMO crash ao vivo, identico, o lock nao resolveu.
/// Decisao apos a 3ª: desligar o pipeline inteiro na raiz (<c>PlaybackEngine</c>, comentando
///    SetAudioFormat/SetAudioCallbacks) — estado estavel restaurado e validado pelo usuario.
/// 4ª tentativa (2026-08-11): pesquisa contra o codigo-fonte real do LibVLCSharp
///    (videolan/libvlcsharp, branch 3.x) descartou a hipotese de um bug conhecido de cleanup
///    callback (GitLab #339) — confirmado que esse bug so afeta quem usa
///    <c>SetAudioFormatCallbacks</c> (negociacao dinamica), nao <c>SetAudioFormat</c> fixo (o que
///    este projeto usa). A mudanca real desta tentativa: (a) <see cref="SendAudioFrame"/> agora
///    aloca/libera um buffer NATIVO NOVO a cada chamada, em vez de reusar/crescer um buffer entre
///    chamadas — elimina de vez a classe de bug "memoria nativa compartilhada entre chamadas" que o
///    lock da 3ª tentativa tentava mitigar sem sucesso (o lock foi removido — sem estado nativo
///    compartilhado entre chamadas, nao ha mais corrida pra proteger, OU ASSIM SE PENSAVA — ver
///    correcao abaixo); (b) a captura de audio, no <c>PlaybackEngine</c>, passou a usar um
///    <c>MediaPlayer</c> headless PROPRIO, dedicado so a audio; (c) <see cref="AudioDiagnosticLog"/>
///    novo — gravacao sincrona em disco a cada bloco.
/// CORRECAO pos-6º-crash (2026-08-11, mesmo dia): o lock removido na 4ª tentativa voltou. O
///    raciocinio da 4ª tentativa ("sem buffer NATIVO compartilhado entre chamadas, nao ha mais
///    corrida pra proteger") deixou passar uma coisa: <c>_interleavedScratch</c>/<c>_planarScratch</c>
///    (os arrays GERENCIADOS reusados, ver campos abaixo) continuam sendo estado COMPARTILHADO entre
///    chamadas — se <see cref="SendAudioFrame"/> puder ser chamado por mais de uma thread ao mesmo
///    tempo (nao confirmado como impossivel pra um unico <c>MediaPlayer</c>/callback de audio do
///    LibVLC), duas chamadas concorrentes escrevendo nos MESMOS arrays reusados produzem dado
///    misturado — inclusive a checagem-e-realocacao condicional (<c>if (_interleavedScratch is null
///    || ...Length &lt; totalSamples) { _interleavedScratch = new float[...]; }</c>) sem nenhuma
///    sincronizacao e uma corrida classica de "check-then-act" em cima de um campo compartilhado.
///    Achado que levou a esta correcao: um log de diagnostico mostrou um bloco com <c>first=0</c> e
///    <c>last=0</c> (bordas limpas, silencio de verdade) mas um UNICO valor de pico gigantesco bem no
///    MEIO do bloco (<c>peakIdx</c> por volta de 56% do total) — um padrao muito mais compativel com
///    "uma amostra sobrescrita por outra chamada no meio da escrita" do que com "ponteiro errado" ou
///    "estouro no fim do buffer" (hipoteses ja descartadas nesta mesma sprint). O lock desta vez
///    protege os arrays GERENCIADOS reusados (nao mais um buffer nativo, que ja e local por chamada
///    desde a 4ª tentativa e continua sendo) — serializa qualquer chamada concorrente a
///    <see cref="SendAudioFrame"/> deste Deck, eliminando esta classe de corrida por completo.
/// </summary>
public sealed class NdiSender : IDisposable
{
    private readonly IntPtr _instance;
    private readonly IntPtr _namePtr;
    private readonly AudioDiagnosticLog _audioLog;
    private bool _disposed;

    // CORRECAO pos-6º-crash (2026-08-11, mesmo dia — ver comentario de classe): protege
    // _interleavedScratch/_planarScratch (estado GERENCIADO compartilhado entre chamadas) contra
    // chamadas concorrentes a SendAudioFrame — NAO protege nenhum buffer nativo (esse ja e local por
    // chamada desde a 4ª tentativa, sem estado nativo compartilhado pra proteger).
    private readonly object _audioGate = new();

    // Buffer gerenciado (array .NET, rastreado/liberado pelo GC) reusado so pra evitar alocar heap
    // gerenciado a cada bloco de audio (caminho QUENTE, dezenas de vezes por segundo) — cresce se um
    // bloco maior que o anterior chegar. Protegido por _audioGate desde a correcao pos-6º-crash (ver
    // comentario de classe) — o buffer NATIVO final continua local por chamada, nunca reusado.
    //
    // Sprint NDI-audio-NAudio (2026-08-11, mesmo dia): _interleavedScratch (que so existia pra
    // receber a copia via Marshal.Copy do ponteiro nativo do LibVLC) foi removido — SendAudioFrame
    // agora recebe o bloco ja como array gerenciado (float[]), lido direto, sem copia intermediaria.
    private float[]? _planarScratch;

    /// <summary>
    /// Cria e registra a fonte NDI imediatamente (fica visivel pra outras maquinas na rede assim
    /// que este construtor retorna, mesmo antes do primeiro SendFrame — comportamento padrao da
    /// NDI SDK, e o que a pesquisa tecnica chama de fonte "sempre quente").
    /// </summary>
    /// <param name="ndiName">Nome do canal NDI (arquitetura §13.1, Deck.NdiChannelName) — pode conter acentos, marshalado como UTF-8.</param>
    public NdiSender(string ndiName)
    {
        if (string.IsNullOrWhiteSpace(ndiName))
        {
            throw new ArgumentException("Nome do canal NDI nao pode ser vazio.", nameof(ndiName));
        }

        // Confere ANTES de chamar qualquer outra funcao nativa da NDI SDK — chamar
        // NDIlib_send_create sem NDIlib_initialize ter tido sucesso antes e comportamento
        // indefinido do lado nativo (nao uma excecao gerenciada que da pra capturar depois). Ver
        // comentario de NdiRuntime sobre por que essa checagem existe.
        if (!NdiRuntime.IsAvailable)
        {
            throw new InvalidOperationException(
                $"NDI Runtime indisponivel — {NdiRuntime.FailureReason ?? "motivo desconhecido (NdiRuntime.Initialize() nunca foi chamado?)."}");
        }

        // UTF-8 manual (nao [MarshalAs(UnmanagedType.LPStr)], que e ANSI) — nomes de Deck/canal
        // podem ter acentos (ex.: "DECK - 1" e ok em ANSI, mas o campo e livre pro operador digitar
        // qualquer coisa, e a NDI SDK exige UTF-8 no p_ndi_name).
        _namePtr = Marshal.StringToCoTaskMemUTF8(ndiName);

        var createSettings = new NDIlib_send_create_t
        {
            p_ndi_name = _namePtr,
            p_groups = IntPtr.Zero,
            clock_video = false,
            clock_audio = false
        };

        _instance = NativeMethods.NDIlib_send_create(ref createSettings);

        if (_instance == IntPtr.Zero)
        {
            Marshal.FreeCoTaskMem(_namePtr);
            throw new InvalidOperationException($"NDIlib_send_create falhou para o canal \"{ndiName}\".");
        }

        // 4ª tentativa do audio NDI (2026-08-11) — ver comentario de AudioDiagnosticLog. Um arquivo
        // por Deck (nome do canal), nunca lanca mesmo se a NDI create acima tiver ido bem mas o
        // audio nunca for usado (ex.: Deck sem audio) — so fica um arquivo vazio/nao usado.
        _audioLog = new AudioDiagnosticLog(ndiName);
    }

    /// <summary>
    /// Envia um quadro BGRA nao-comprimido. Sem efeito (retorna silenciosamente) se ja descartado —
    /// evita que uma corrida entre o ultimo frame do Engine e o Dispose do Deck vire excecao.
    /// </summary>
    /// <param name="data">Ponteiro pro buffer BGRA (ownership continua do chamador — este metodo so le, nunca libera).</param>
    /// <param name="width">Largura em pixels.</param>
    /// <param name="height">Altura em pixels.</param>
    /// <param name="strideBytes">Bytes por linha (line_stride_in_bytes) — pra BGRA, normalmente width * 4.</param>
    public void SendFrame(IntPtr data, int width, int height, int strideBytes)
    {
        if (_disposed)
        {
            return;
        }

        var frame = new NDIlib_video_frame_v2_t
        {
            xres = width,
            yres = height,
            FourCC = NDIlib_FourCC_video_type_e.NDIlib_FourCC_video_type_BGRA,
            frame_rate_N = 30000,
            frame_rate_D = 1000,
            picture_aspect_ratio = height > 0 ? width / (float)height : 16f / 9f,
            frame_format_type = NDIlib_frame_format_type_e.NDIlib_frame_format_type_progressive,
            // INT64_MAX = NDIlib_send_timecode_synthesize (header oficial) — pede pra NDI SDK
            // gerar o timecode automaticamente a partir do clock do sistema, em vez deste projeto
            // ter que rastrear timecode de video manualmente (fora de escopo desta sprint).
            timecode = long.MaxValue,
            p_data = data,
            line_stride_in_bytes = strideBytes,
            p_metadata = IntPtr.Zero,
            timestamp = 0
        };

        NativeMethods.NDIlib_send_send_video_v2(_instance, ref frame);
    }

    /// <summary>
    /// Envia um bloco de audio PCM float32. Recebe o buffer JA INTERCALADO — a DESINTERCALACAO pro
    /// formato PLANAR que a NDI SDK espera (<see cref="NDIlib_audio_frame_v2_t"/>) acontece aqui
    /// dentro, manualmente, no buffer GERENCIADO reusado entre chamadas (ver campo
    /// <c>_planarScratch</c> — evita alocar heap gerenciado a cada bloco, caminho quente). O buffer
    /// NATIVO final (o que vai pra <c>p_data</c>) NAO e reusado entre chamadas — ver o bloco
    /// "remarks" abaixo. Sem efeito (retorna silenciosamente) se ja descartado ou se os parametros
    /// forem degenerados (array nulo/vazio, canais/amostras <= 0) — mesma guarda honesta ja usada em
    /// <see cref="SendFrame"/>.
    ///
    /// Sprint NDI-audio-NAudio (2026-08-11, mesmo dia): <paramref name="interleavedSamples"/> deixou
    /// de ser um <c>IntPtr</c> (ponteiro pro buffer do proprio LibVLC) e passou a ser um array
    /// GERENCIADO — a mudanca de <see cref="NdiAudioCapture"/> (NAudio, ver comentario la pro
    /// historico completo dos sete crashes que motivaram a migracao) entrega os blocos de audio ja
    /// como <c>float[]</c> nativamente, entao pinar/desreferenciar um ponteiro aqui so reintroduziria
    /// artificialmente a mesma classe de risco (leitura de memoria com lifetime incerto) que a
    /// migracao inteira existe pra eliminar. <c>_interleavedScratch</c> (o buffer reusado que existia
    /// so pra receber a copia via <see cref="Marshal.Copy"/> do ponteiro nativo) deixou de ser
    /// necessario — o array do chamador ja E os dados, lidos direto.
    /// </summary>
    /// <param name="interleavedSamples">Array gerenciado com as amostras INTERCALADAS (ownership continua do chamador — este metodo so le, nunca guarda a referencia alem desta chamada).</param>
    /// <param name="sampleRate">Taxa de amostragem (Hz) — 48000 no formato fixo deste projeto.</param>
    /// <param name="channels">Numero de canais — 2 (estereo) no formato fixo deste projeto.</param>
    /// <param name="sampleCount">Numero de amostras POR CANAL neste bloco (nao o total).</param>
    ///
    /// <remarks>
    /// 4ª tentativa (2026-08-11): SEM buffer nativo reusado entre chamadas — cada chamada aloca
    /// (<see cref="Marshal.AllocHGlobal"/>) e libera (<see cref="Marshal.FreeHGlobal"/>) seu PROPRIO
    /// buffer nativo, dentro de um try/finally, so pra entregar pra NDI SDK (que exige um ponteiro
    /// nativo em <c>p_data</c>, nao tem variante que aceite array gerenciado direto). Custo aceito:
    /// um alloc/free nativo a mais por bloco de audio (dezenas de vezes por segundo) — pequeno pro
    /// tamanho tipico de um bloco (poucos KB). O array GERENCIADO reusado (<c>_planarScratch</c>)
    /// continua existindo so pra desintercalar sem alocar heap gerenciado a cada bloco.
    /// </remarks>
    public void SendAudioFrame(float[] interleavedSamples, int sampleRate, int channels, int sampleCount)
    {
        if (_disposed || interleavedSamples is null || channels <= 0 || sampleCount <= 0)
        {
            return;
        }

        // CORRECAO pos-6º-crash (2026-08-11, mesmo dia — ver comentario de classe/_audioGate):
        // serializa qualquer chamada concorrente a este metodo neste Deck. Continua valendo depois
        // da migracao pra NAudio (Sprint NDI-audio-NAudio) — protege _planarScratch (estado
        // GERENCIADO compartilhado entre chamadas) contra chamadas concorrentes.
        lock (_audioGate)
        {
            SendAudioFrameLocked(interleavedSamples, sampleRate, channels, sampleCount);
        }
    }

    private void SendAudioFrameLocked(float[] interleavedSamples, int sampleRate, int channels, int sampleCount)
    {
        var totalSamples = channels * sampleCount;

        if (interleavedSamples.Length < totalSamples)
        {
            // Bloco degenerado (chamador passou menos amostras do que sampleCount*channels
            // anunciam) — falha honesta, nunca le alem do array recebido.
            return;
        }

        if (_planarScratch is null || _planarScratch.Length < totalSamples)
        {
            _planarScratch = new float[totalSamples];
        }

        // Diagnostico (2026-08-11): pico de amplitude absoluta do bloco INTERCALADO recebido —
        // calculado aqui, de graca, dentro do mesmo laco que ja precisa varrer o buffer pra
        // desintercalar. NAO afeta o envio (so vai pro log) — mantido apos a migracao pra NAudio
        // como verificacao continua de sanidade (o log ja provou seu valor nos sete crashes
        // anteriores; custo desprezivel de manter).
        var peak = 0f;
        var peakIndex = -1;

        // Desintercala: canal 0 (todas as amostras, em sequencia), depois canal 1, etc. — layout
        // que NDIlib_audio_frame_v2_t.channel_stride_in_bytes espera (cada canal e um bloco
        // contiguo).
        for (var channel = 0; channel < channels; channel++)
        {
            var channelOffset = channel * sampleCount;
            for (var sample = 0; sample < sampleCount; sample++)
            {
                var interleavedIndex = (sample * channels) + channel;
                var value = interleavedSamples[interleavedIndex];
                _planarScratch![channelOffset + sample] = value;

                var absValue = MathF.Abs(value);
                if (absValue > peak)
                {
                    peak = absValue;
                    peakIndex = interleavedIndex;
                }
            }
        }

        var firstSample = totalSamples > 0 ? interleavedSamples[0] : 0f;
        var lastSample = totalSamples > 0 ? interleavedSamples[totalSamples - 1] : 0f;

        var requiredBytes = totalSamples * sizeof(float);
        var nativeBuffer = Marshal.AllocHGlobal(requiredBytes);
        try
        {
            Marshal.Copy(_planarScratch!, 0, nativeBuffer, totalSamples);

            var frame = new NDIlib_audio_frame_v2_t
            {
                sample_rate = sampleRate,
                no_channels = channels,
                no_samples = sampleCount,
                // Mesma escolha ja feita em SendFrame: deixa a NDI SDK sintetizar o timecode a
                // partir do clock do sistema em vez deste projeto rastrear timecode de audio
                // manualmente.
                timecode = long.MaxValue,
                p_data = nativeBuffer,
                channel_stride_in_bytes = sampleCount * sizeof(float),
                p_metadata = IntPtr.Zero,
                timestamp = 0
            };

            // Ver AudioDiagnosticLog — se o processo travar entre estas duas linhas, o arquivo em
            // disco mostra ANTES sem o DEPOIS correspondente, apontando pra dentro da chamada nativa.
            // "peak"/"peakIdx"/"first"/"last" (ver comentario acima) confirmam se o bloco tem audio
            // de verdade, silencio, ou lixo — e, se for lixo, se esta concentrado no fim do bloco
            // (estouro) ou espalhado desde o inicio (ponteiro/formato errado).
            _audioLog.LogBeforeSend(sampleRate, channels, sampleCount, totalSamples, requiredBytes, peak, peakIndex, firstSample, lastSample);
            NativeMethods.NDIlib_send_send_audio_v2(_instance, ref frame);
            _audioLog.LogAfterSend();
        }
        finally
        {
            Marshal.FreeHGlobal(nativeBuffer);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        _audioLog.Dispose();

        NativeMethods.NDIlib_send_destroy(_instance);
        Marshal.FreeCoTaskMem(_namePtr);
    }
}
