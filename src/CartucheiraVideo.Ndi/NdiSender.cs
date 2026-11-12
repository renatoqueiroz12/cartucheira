using System.Diagnostics;
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
/// <see cref="SendFrame"/> usa a variante ASSINCRONA da NDI SDK desde a Sprint NDI-video-async
/// (2026-09-11) — <c>NDIlib_send_send_video_async_v2</c>, ver comentario completo em
/// <see cref="NativeMethods.NDIlib_send_send_video_v2"/>/<c>_async_v2</c> em <see cref="NdiInterop"/>.
/// Motivo: a variante sincrona antiga fazia conversao de cor/compressao/transmissao de rede DENTRO
/// da chamada, bloqueando a thread de decodificacao de video do LibVLC inteira — causava travamento
/// de video quadro a quadro num teste real contra vMix numa segunda maquina (o mesmo cenario com
/// OBS+DistroAV, que ja usa a variante async, ficava limpo). Por causa do contrato de lifetime de
/// buffer da variante async (o buffer nao pode ser reescrito ate um "evento sincronizante" — ver
/// comentario em NdiInterop), o chamador (<see cref="Engine.PlaybackEngine"/>) usa dois buffers
/// alternados ("ping-pong") em vez de reusar um unico buffer fixo.
///
/// <see cref="SendAudioFrame"/> continua usando a variante NAO-async
/// (<c>NDIlib_send_send_audio_v2</c>), que copia o buffer internamente antes de retornar — por isso
/// e seguro o chamador reusar/descartar o buffer de audio de origem imediatamente apos a chamada
/// retornar, sem nenhuma sincronizacao adicional. Deliberadamente NAO convertido pra async nesta
/// sprint (escopo minimo: o sintoma reportado era especificamente de VIDEO, e audio ja tem sua
/// propria thread de captura dedicada, separada da thread de decodificacao de video — ver
/// <c>NdiAudioCapture</c>).
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
///    <c>MediaPlayer</c> headless PROPRIO, dedicado so a audio; (c) um log de diagnostico novo
///    (<c>AudioDiagnosticLog</c>, ja removido — ver Sprint NDI-video-async, 2026-09-11, abaixo) —
///    gravacao sincrona em disco a cada bloco.
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
/// Sprint NDI-video-async (2026-09-11): <c>AudioDiagnosticLog</c> removido — a causa raiz que ele
///    existia pra diagnosticar (os sete crashes acima) ja foi confirmada e corrigida ha um mes; seu
///    proprio comentario de classe ja dizia "nao e pra ficar ligado pra sempre". Nao relacionado ao
///    audio: esta mesma sprint tambem trocou <see cref="SendFrame"/> (VIDEO) de sincrono pra
///    assincrono — ver comentario de classe atualizado acima e <see cref="NdiInterop"/> pro
///    diagnostico completo do bug real corrigido (travamento de video quadro a quadro).
///
/// Sprint NDI-clock-sync (2026-09-12) — depois de tres sprints so ajustando o mecanismo CASEIRO de
///    correcao de deriva (carencia inicial, confirmacao dupla, passos graduais — ver comentario de
///    classe de <c>NdiAudioCapture</c>), o usuario sugeriu usar "o mesmo plugin que fez o NDI
///    funcionar no OBS". Pesquisa confirmou que o OBS/DistroAV usa a MESMA NDI SDK oficial que este
///    projeto ja usa (nao existe um "plugin diferente" pra copiar) — a diferenca real esta na
///    CONFIGURACAO da fonte NDI. Encontrado um PR real do DistroAV (github.com/DistroAV/DistroAV/
///    pull/1172, "Synchronize VLC audio with video in dedicated NDI output filter") corrigindo
///    EXATAMENTE este sintoma (fonte VLC, audio/video dessincronizando) — a causa raiz la era, entre
///    outras coisas, <c>clock_video</c>/<c>clock_audio</c> criados como <c>false</c> na
///    <c>NDIlib_send_create_t</c>; a correcao deles foi virar os dois pra <c>true</c>. O comentario
///    oficial do header da NDI SDK (<c>Processing.NDI.Send.h</c>, conferido via um mirror publico do
///    header, github.com/Streampunk/grandiose) explica o racional: "Do you want audio and video to
///    'clock' themselves. When they are clocked then by adding video frames, they will be rate
///    limited to match the current frame rate that you are submitting at. [...] If you are
///    submitting audio and video of separate threads then having both clocked can be useful." —
///    EXATAMENTE o desenho deste projeto (video pela thread do LibVLC, audio pela thread dedicada de
///    <c>NdiAudioCapture</c>). Com os dois em <c>false</c> (valor original deste projeto, nunca
///    revisto desde o MVP), a propria NDI SDK nao impoe NENHUM ritmo real de envio — confia
///    inteiramente que as duas threads independentes deste projeto vao coincidir sozinhas em tempo
///    real, o que e a causa estrutural por tras de toda a saga de deriva das ultimas sprints.
///    Correcao: <c>clock_video</c>/<c>clock_audio</c> agora <c>true</c> no construtor — deixa a
///    propria NDI SDK regular o ritmo de envio de audio e video (mecanismo oficial, documentado,
///    exatamente pra este cenario), em vez de confiar so no pacing caseiro por relogio de parede de
///    <c>NdiAudioCapture</c>. A correcao gradual de deriva das sprints anteriores continua existindo
///    como rede de seguranca (nao removida nesta sprint), mesmo que deva disparar bem menos agora.
///
/// Sprint NDI-video-uyvy (2026-09-12) — usuario testou a Sprint NDI-clock-sync e reportou "melhorou
///    um pouco, mas ainda falha" (confirmado por pergunta direta: o sintoma remanescente e SO de
///    audio, nao de video). Junto com o teste, o usuario enviou 2 capturas do Gerenciador de Tarefas
///    mostrando uso de rede bem maior na Cartucheira (120.2 Mbps) do que no OBS (84.5 Mbps) emitindo
///    o mesmo tipo de sinal (NDI, mesma resolucao/fps) — e o OBS ficava "limpo" enquanto a Cartucheira
///    "engasgava". Hipotese (nao garantida, e uma causa SECUNDARIA possivel, nao a unica): o volume de
///    dados de video BGRX (4 bytes/pixel, RGB) pode estar gerando carga de rede/sistema suficiente pra
///    ainda ocasionalmente atrapalhar a thread de audio em tempo real, mesmo com o clocking da SDK
///    ativado. OBS/DistroAV, como qualquer emissor NDI "profissional", pede video em YUV (nunca RGB)
///    pra saida NDI — exatamente pra evitar a "performance penalty" de BGRA/BGRX que a propria
///    documentacao da NDI ja citava (ver comentario de <see cref="NDIlib_FourCC_video_type_e"/>
///    acima). Correcao aprovada pelo usuario ("quero que resolva"): <see cref="SendFrame"/> passa a
///    declarar UYVY (YCbCr 4:2:2, 2 bytes/pixel em media) em vez de BGRX — reduz o volume de dados de
///    video quase a metade, fechando a maior parte do gap de banda observado. O buffer em si (layout
///    de memoria, calculo de stride/offset de letterbox) muda de verdade desta vez (ao contrario da
///    troca BGRA→BGRX, que so mudava o FourCC declarado) — ver <see cref="Engine.PlaybackEngine"/> pro
///    calculo do novo layout UYVY. Nota de honestidade repetida ao usuario: o objetivo DIRETO desta
///    sprint e fechar o gap de banda com o OBS; o efeito sobre o "cd riscado" de audio e uma segunda
///    hipotese plausivel, nao uma garantia — so o teste real confirma.
///
/// Sprint NDI-audio-buffer-diag (2026-09-12) — usuario testou a Sprint NDI-video-uyvy: rede caiu
///    muito pouco (120,2 pra 113,9 Mbps, nao os quase 50% esperados — a NDI comprime internamente,
///    o formato do buffer que a gente entrega afeta pouco o bitrate final), CPU baixa (~3,6%), e o
///    engasgo de audio continuou, so no NDI recebido. Rede confirmada gigabit cabeada (nao e falta
///    de banda fisica). <see cref="SendAudioFrame"/> agora mede a duracao da chamada sincrona de
///    envio de audio e registra (so quando ultrapassa um limiar folgado — nunca no caminho normal)
///    num log de diagnostico compartilhado com <c>NdiAudioCapture</c> — ver comentario de
///    <see cref="LogDiagnostic"/> e o comentario de classe de <c>NdiAudioCapture</c> pro diagnostico
///    completo desta sprint.
/// </summary>
public sealed class NdiSender : IDisposable
{
    /// <summary>
    /// Sprint NDI-audio-buffer-diag (2026-09-12): limiar de duracao da chamada SINCRONA de envio de
    /// audio (<c>NDIlib_send_send_audio_v2</c>) acima do qual <see cref="SendAudioFrame"/> registra
    /// uma linha em <see cref="LogDiagnostic"/>. Um bloco representa 10ms de audio — 20ms de folga
    /// (o dobro) e bem acima do normal esperado numa chamada que so copia um buffer pequeno pra rede
    /// local, entao ultrapassar isso e sinal real de bloqueio, nao ruido de medicao.
    /// </summary>
    private const double AudioSendWarnThresholdMs = 20.0;

    private readonly IntPtr _instance;
    private readonly IntPtr _namePtr;

    /// <summary>Copia do nome do canal, so pra identificar de qual Deck vem cada linha do log de diagnostico (<see cref="LogDiagnostic"/>) — <see cref="_namePtr"/> e so o ponteiro nativo UTF-8, nao serve pra isso.</summary>
    private readonly string _ndiName;

    private bool _disposed;

    // CORRECAO pos-6º-crash (2026-08-11, mesmo dia — ver comentario de classe): protege
    // _interleavedScratch/_planarScratch (estado GERENCIADO compartilhado entre chamadas) contra
    // chamadas concorrentes a SendAudioFrame — NAO protege nenhum buffer nativo (esse ja e local por
    // chamada desde a 4ª tentativa, sem estado nativo compartilhado pra proteger).
    private readonly object _audioGate = new();

    /// <summary>
    /// Fix v1.0.70 (2026-11-12): incidente real — depois de MUITAS tentativas na pilha gerenciada
    /// (Engine.PlaybackEngine) nao resolverem o crash de Stop ("clica no stop, da um tempinho, ele
    /// fecha"; Pause continuou 100% seguro o tempo todo), o usuario capturou DOIS dumps nativos reais
    /// (Visualizador de Erros + WinDbg) do mesmo incidente, em momentos diferentes — e os dois
    /// mostraram pilhas DIFERENTES: a primeira morrendo dentro de "libavcodec_plugin" (o decoder
    /// FFmpeg da libvlc, liberando um ponteiro corrompido em RtlFreeHeap), a segunda dentro de
    /// "Processing_NDI_Lib_x64" (a propria SDK da NDI, lendo um endereco invalido numa instrucao
    /// vetorial AVX2 de conversao de cor). As duas em threads NAO gerenciadas (`!clrstack` confirmou
    /// nas duas: "likely not a managed thread"), em MODULOS nativos diferentes. Pilhas diferentes pro
    /// MESMO sintoma, numa corrida de dados classica, sao exatamente a assinatura de corrupcao de
    /// memoria cujo ponto de crash real nao e o ponto de origem — quem quer que toque a memoria
    /// corrompida DEPOIS e quem aparece na pilha, nao quem corrompeu.
    ///
    /// Revisando quem chama <see cref="SendFrame"/> (video): hoje sao DUAS fontes concorrentes, sem
    /// nenhuma trava — <c>DeckViewModel.OnEngineFrameReady</c> (a thread de decodificacao de video do
    /// LibVLC, UM quadro real por vez, so enquanto o Deck esta tocando) e, desde a Sprint
    /// Arte-de-Fundo (2026-11-10), <c>DeckViewModel.SendBackgroundArtFrame</c> — um
    /// <see cref="System.Threading.Timer"/> do ThreadPool, rodando a cada 200ms, que LIGA
    /// exatamente quando o Deck entra em Cue/Stopped/Finished (ver
    /// <c>DeckViewModel.IsBackgroundArtIdleState</c> — DELIBERADAMENTE exclui Paused, unica razao
    /// documentada de Pause nunca ter aparecido travando em nenhum log desta investigacao inteira).
    /// Ou seja: no exato instante em que o operador clica Stop, o Engine ainda pode ter um quadro de
    /// video em transito assincrono (contrato documentado em
    /// <see cref="NdiInterop.NativeMethods.NDIlib_send_send_video_async_v2"/> — so sincroniza no
    /// PROXIMO evento sincronizante), E o timer de arte de fundo comeca a chamar
    /// <see cref="SendFrame"/> de novo, de uma thread diferente, no MESMO <see cref="_instance"/> da
    /// NDI SDK — SEM NENHUMA trava protegendo essa concorrencia. Esta MESMA classe de bug (chamadas
    /// concorrentes nao protegidas na mesma instancia da NDI SDK) ja tinha causado sete crashes reais
    /// do lado do AUDIO (ver historico completo no comentario de classe acima) — corrigida la com
    /// <see cref="_audioGate"/>. <see cref="SendFrame"/> (video) nunca recebeu a mesma protecao,
    /// porque ate a Sprint Arte-de-Fundo so existia UM chamador possivel (a propria thread de
    /// decodificacao) — a segunda fonte concorrente so passou a existir depois, sem ninguem conectar
    /// os pontos com a licao ja aprendida do lado do audio.
    ///
    /// CORRIGIDO: <see cref="_videoGate"/>, mesmo padrao exato de <see cref="_audioGate"/>, serializa
    /// toda chamada a <see cref="SendFrame"/> — a thread de decodificacao e o timer de arte de fundo
    /// nunca mais entram ao mesmo tempo na API nativa da NDI SDK pro mesmo canal.
    /// </summary>
    private readonly object _videoGate = new();

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
    /// Sprint NDI-Frame-Rate-Real (2026-09-16): frame rate declarado pro NDI em todo
    /// <see cref="SendFrame"/> — comeca em 30000/1000 (30,000fps exatos, o mesmo valor fixo que
    /// TODA chamada usava antes desta sprint, qualquer que fosse o arquivo) e passa a refletir o
    /// frame rate REAL do arquivo carregado assim que <see cref="SetFrameRate"/> for chamado (o
    /// <c>DeckViewModel</c> chama isso a cada <c>Load</c>, com o valor que
    /// <see cref="Engine.PlaybackEngine.VideoFrameRate"/> detectou). Motivo da mudanca: a fonte NDI
    /// deste Deck e criada com <c>clock_video = true</c> (Sprint NDI-clock-sync, 2026-09-12) — a NDI
    /// SDK usa justamente este par num/den pra ritmar o envio de video internamente. Declarar
    /// 30,000fps exatos pra um arquivo que na verdade e 60fps (ou 29,97/59,94, o padrao NTSC mais
    /// comum) e um metadado ERRADO, e um candidato real pra explicar deriva de sincronismo A/V
    /// medida entre video e audio na saida NDI — ver
    /// `sprint-2026-09-16-ndi-sync-diag-vmix-resultado.md` no projeto pro diagnostico completo.
    /// </summary>
    private int _frameRateNum = 30000;

    private int _frameRateDen = 1000;

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

        _ndiName = ndiName;

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

        // Sprint NDI-clock-sync (2026-09-12): true pros dois — ver comentario de classe pro
        // diagnostico completo (PR real do DistroAV corrigindo o mesmo sintoma numa fonte VLC, e o
        // racional oficial do header da NDI SDK: "clocking" ambos e recomendado justamente quando
        // audio e video sao enviados de threads separadas, exatamente o desenho deste projeto).
        // Antes eram os dois `false` (valor original, nunca revisto desde o MVP) — a NDI SDK nao
        // impunha nenhum ritmo real de envio, so confiava que as duas threads independentes deste
        // projeto (video via LibVLC, audio via NdiAudioCapture) coincidiriam sozinhas em tempo real.
        var createSettings = new NDIlib_send_create_t
        {
            p_ndi_name = _namePtr,
            p_groups = IntPtr.Zero,
            clock_video = true,
            clock_audio = true
        };

        _instance = NativeMethods.NDIlib_send_create(ref createSettings);

        if (_instance == IntPtr.Zero)
        {
            Marshal.FreeCoTaskMem(_namePtr);
            throw new InvalidOperationException($"NDIlib_send_create falhou para o canal \"{ndiName}\".");
        }
    }

    /// <summary>
    /// Envia um quadro UYVY nao-comprimido, de forma ASSINCRONA (Sprint NDI-video-async,
    /// 2026-09-11 — ver comentario de classe). Sem efeito (retorna silenciosamente) se ja
    /// descartado — evita que uma corrida entre o ultimo frame do Engine e o Dispose do Deck vire
    /// excecao.
    ///
    /// Sprint NDI-video-uyvy (2026-09-12): FourCC trocado de BGRX pra UYVY (ver comentario completo
    /// em <see cref="NDIlib_FourCC_video_type_e.NDIlib_FourCC_video_type_UYVY"/> e no comentario de
    /// classe acima) — motivado pelo gap de banda de rede observado pelo usuario contra o OBS.
    /// DIFERENTE da troca BGRA→BGRX (so mudava o FourCC declarado, mesmo layout de memoria), esta
    /// troca MUDA o layout de memoria do buffer de verdade: UYVY empacota 2 pixels em 4 bytes (ordem
    /// U,Y0,V,Y1), 2 bytes/pixel em media, em vez de 4 bytes/pixel do BGRX — ver
    /// <see cref="Engine.PlaybackEngine"/> pro calculo do novo buffer/stride/offset de letterbox.
    /// </summary>
    /// <param name="data">
    /// Ponteiro pro buffer UYVY (ownership continua do chamador — este metodo so le, nunca libera).
    /// CRITICO (variante async): o chamador NAO PODE reescrever este buffer ate o proximo "evento
    /// sincronizante" da NDI SDK (ver <see cref="NativeMethods.NDIlib_send_send_video_async_v2"/>)
    /// — o <see cref="Engine.PlaybackEngine"/> garante isso alternando entre dois buffers a cada
    /// quadro, nunca reescrevendo o mesmo buffer que acabou de ser passado pra esta chamada.
    /// </param>
    /// <param name="width">Largura em pixels.</param>
    /// <param name="height">Altura em pixels.</param>
    /// <param name="strideBytes">Bytes por linha (line_stride_in_bytes) — pra UYVY, width * 2 (2 bytes/pixel em media; width deve ser par, ja garantido por <see cref="Engine.PlaybackEngine"/>).</param>
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
            FourCC = NDIlib_FourCC_video_type_e.NDIlib_FourCC_video_type_UYVY,
            frame_rate_N = _frameRateNum,
            frame_rate_D = _frameRateDen,
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

        // Fix v1.0.70 (2026-11-12): ver comentario completo de _videoGate — serializa esta chamada
        // contra o timer de arte de fundo, que chama este mesmo metodo de uma thread diferente assim
        // que o Deck entra em Cue/Stopped/Finished.
        lock (_videoGate)
        {
            NativeMethods.NDIlib_send_send_video_async_v2(_instance, ref frame);
        }
    }

    /// <summary>
    /// Fix v1.0.72 (2026-11-12): incidente real — mesmo depois da v1.0.70 (trava <see cref="_videoGate"/>
    /// contra chamadas CONCORRENTES a <see cref="SendFrame"/>), o usuario reproduziu o MESMO crash de
    /// novo (terceiro dump: pilha morre de novo dentro de "Processing_NDI_Lib_x64", numa instrucao
    /// vetorial de conversao de cor, lendo memoria invalida — identico em NATUREZA ao segundo dump da
    /// v1.0.70, so que a v1.0.70 ja estava instalada). Ou seja: a v1.0.70 resolveu a corrida de
    /// CHAMADAS CONCORRENTES, mas havia uma SEGUNDA violacao do mesmo contrato de lifetime assincrono
    /// (ver comentario completo em <see cref="NdiInterop.NativeMethods.NDIlib_send_send_video_async_v2"/>),
    /// desta vez nao entre duas THREADS chamando ao mesmo tempo, e sim entre uma chamada e o que
    /// acontece DEPOIS dela retornar: o contrato documentado exige um "evento sincronizante" — (a) a
    /// variante sincrona, (b) chamar esta MESMA funcao assincrona de novo, (c) passar um frame nulo, ou
    /// (d) <c>NDIlib_send_destroy</c> — antes de reescrever OU LIBERAR a memoria que foi passada numa
    /// chamada anterior. <c>DeckViewModel._backgroundArtBuffer</c> (o buffer nativo UNICO, reenviado a
    /// cada 200ms pelo timer de Arte de Fundo) e liberado (<c>Marshal.FreeHGlobal</c>) em dois lugares
    /// — <c>StopBackgroundArtTimer</c> (sempre que o Deck sai de Cue/Stopped/Finished, ex.: Play depois
    /// de um Stop) e <c>ReplaceBackgroundArtBuffer</c> (ao trocar a arte ativa enquanto o Deck ja esta
    /// ocioso) — SEM NENHUM evento sincronizante antes. Se o ULTIMO <see cref="SendFrame"/> daquele
    /// buffer especifico (antes do timer parar) ainda estiver em processamento assincrono dentro da NDI
    /// SDK (conversao de cor/compressao/rede, fora desta chamada, ver comentario de classe) no instante
    /// em que o C# libera essa memoria pro sistema, a SDK fica lendo memoria ja devolvida/reaproveitada
    /// — corrupcao de heap classica, com o ponto de crash real (qualquer alocacao futura que reuse
    /// aquele endereco, ou a propria SDK tocando a memoria corrompida depois) podendo ser bem depois e
    /// num lugar totalmente diferente de onde a corrupcao realmente aconteceu — exatamente o padrao
    /// documentado nos comentarios da v1.0.70.
    ///
    /// CORRIGIDO: este metodo novo manda um frame NULO (<c>p_data = IntPtr.Zero</c>) — a opcao (c) do
    /// contrato oficial, feita exatamente pra isto — e DEVE ser chamado pelo <c>DeckViewModel</c> SEMPRE
    /// antes de liberar <c>_backgroundArtBuffer</c>, nos dois lugares citados acima, DENTRO do mesmo
    /// <c>lock (_backgroundArtGate)</c> que ja protege a troca do ponteiro (garante que nenhum tick do
    /// timer ainda em voo possa estar no meio de um <see cref="SendFrame"/> com o MESMO buffer quando
    /// este metodo roda — ou ele ja terminou e liberou a trava antes de chegarmos aqui, ou ainda nao
    /// comecou porque o timer ja foi parado). Sem efeito (retorna silenciosamente) se ja descartado,
    /// mesma guarda honesta do resto da classe.
    /// </summary>
    public void SyncPendingVideoFrame()
    {
        if (_disposed)
        {
            return;
        }

        var nullFrame = new NDIlib_video_frame_v2_t
        {
            xres = 0,
            yres = 0,
            FourCC = NDIlib_FourCC_video_type_e.NDIlib_FourCC_video_type_UYVY,
            frame_rate_N = _frameRateNum,
            frame_rate_D = _frameRateDen,
            picture_aspect_ratio = 16f / 9f,
            frame_format_type = NDIlib_frame_format_type_e.NDIlib_frame_format_type_progressive,
            timecode = long.MaxValue,
            p_data = IntPtr.Zero,
            line_stride_in_bytes = 0,
            p_metadata = IntPtr.Zero,
            timestamp = 0
        };

        lock (_videoGate)
        {
            NativeMethods.NDIlib_send_send_video_async_v2(_instance, ref nullFrame);
        }
    }

    /// <summary>
    /// Sprint NDI-Frame-Rate-Real (2026-09-16): atualiza o frame rate declarado nos proximos
    /// <see cref="SendFrame"/> — ver comentario completo de <see cref="_frameRateNum"/>. Chamado
    /// pelo <c>DeckViewModel</c> a cada <c>Load</c>, com o valor detectado pelo
    /// <see cref="Engine.PlaybackEngine"/> pro arquivo que acabou de carregar. Ignora silenciosamente
    /// um par invalido (num ou den <= 0) em vez de aplicar — mantem o ultimo valor valido conhecido
    /// (ou o fallback fixo 30000/1000 do construtor, se nunca tiver recebido um valor valido ainda)
    /// em vez de declarar um frame rate ainda pior (zero/negativo) pro NDI.
    /// </summary>
    public void SetFrameRate(int frameRateNum, int frameRateDen)
    {
        if (frameRateNum <= 0 || frameRateDen <= 0)
        {
            return;
        }

        _frameRateNum = frameRateNum;
        _frameRateDen = frameRateDen;
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

        // Desintercala: canal 0 (todas as amostras, em sequencia), depois canal 1, etc. — layout
        // que NDIlib_audio_frame_v2_t.channel_stride_in_bytes espera (cada canal e um bloco
        // contiguo).
        for (var channel = 0; channel < channels; channel++)
        {
            var channelOffset = channel * sampleCount;
            for (var sample = 0; sample < sampleCount; sample++)
            {
                var interleavedIndex = (sample * channels) + channel;
                _planarScratch![channelOffset + sample] = interleavedSamples[interleavedIndex];
            }
        }

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

            // Sprint NDI-audio-buffer-diag (2026-09-12): mede a duracao desta chamada SINCRONA —
            // e exatamente a chamada que a documentacao oficial da NDI admite poder bloquear "um
            // round-trip inteiro" em condicoes de rede ruins (ver comentario de classe). So mede
            // (Stopwatch.GetTimestamp, sem alocar) e so grava em disco se ultrapassar o limiar —
            // no caminho normal, nenhum custo extra alem da medicao em si.
            var startTimestamp = Stopwatch.GetTimestamp();
            NativeMethods.NDIlib_send_send_audio_v2(_instance, ref frame);
            var elapsedMs = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;

            if (elapsedMs > AudioSendWarnThresholdMs)
            {
                LogDiagnostic($"ENVIO-LENTO canal=\"{_ndiName}\" duracao={elapsedMs:F1}ms (limiar={AudioSendWarnThresholdMs:F0}ms)");
            }
        }
        finally
        {
            Marshal.FreeHGlobal(nativeBuffer);
        }
    }

    /// <summary>
    /// Sprint NDI-audio-buffer-diag (2026-09-12) — mesmo log de diagnostico TEMPORARIO de
    /// <c>NdiAudioCapture.LogDiagnostic</c> (projeto <c>CartucheiraVideo.Engine</c>, sem
    /// ProjectReference entre os dois projetos NDI/Engine nas duas direcoes — ver comentario do
    /// .csproj deste projeto — entao duplicado aqui de proposito, em vez de extrair um projeto
    /// compartilhado novo so pra isto, fora do escopo desta sprint). Mesmo arquivo
    /// (<c>%AppData%\CartucheiraVideo\ndi-audio-buffer-diagnostico.log</c>), pra poder comparar os
    /// eventos dos dois lados (fila cheia/profundidade, aqui do lado do Engine; envio lento, aqui do
    /// lado do Ndi) na mesma linha do tempo. So grava em eventos raros — nunca no caminho normal de
    /// cada bloco. Falha honesta: qualquer erro de E/S e engolido, nunca derruba o envio real.
    /// </summary>
    private static void LogDiagnostic(string message)
    {
        try
        {
            var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CartucheiraVideo");
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, "ndi-audio-buffer-diagnostico.log");
            var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}{Environment.NewLine}";
            File.AppendAllText(path, line);
        }
        catch (Exception)
        {
            // Falha honesta — diagnostico nunca deve afetar o envio real.
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // NDIlib_send_destroy e, por si so, um dos "eventos sincronizantes" documentados da variante
        // async de video (ver comentario em NdiInterop.NDIlib_send_send_video_async_v2) — nao
        // precisa de nenhuma chamada extra aqui so pra garantir que o ultimo frame em voo termine
        // antes da instancia ser destruida.
        //
        // Fix v1.0.70 (2026-11-12): ver comentario completo de _videoGate — _disposed vira true ACIMA
        // antes de pegar a trava, entao qualquer SendFrame que ainda nao tinha passado do "if
        // (_disposed)" desiste sozinho; se um SendFrame JA estava dentro da trava quando este Dispose
        // chegou aqui, espera ele terminar antes de destruir a instancia por baixo dele.
        lock (_videoGate)
        {
            NativeMethods.NDIlib_send_destroy(_instance);
        }
        Marshal.FreeCoTaskMem(_namePtr);
    }
}
