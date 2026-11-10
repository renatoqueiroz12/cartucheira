using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;
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
///
/// ENVIO PRA REDE DESACOPLADO DO PACING (Sprint NDI-audio-envio-assincrono, 2026-09-12) — usuario
/// reportou, monitorando o NDI por um segundo PC (mesmo cenario da Sprint NDI-video-async/bgrx),
/// audio "pulando/repetindo, tipo cd arranhado" mesmo depois das correcoes de video (envio
/// assincrono + BGRX) confirmadas presentes no build testado (v1.0.1). Diagnostico, pesquisado
/// contra a documentacao oficial da NDI SDK (docs.ndi.video): a NDI SDK NAO tem nenhuma variante
/// assincrona pra audio (a pagina "Asynchronous Sending Completions" so documenta
/// <c>NDIlib_send_send_video_async_v2</c>/<c>_scatter_async</c> — nenhuma equivalente de audio existe
/// na SDK inteira), entao <see cref="Ndi.NdiSender.SendAudioFrame"/> so pode chamar a variante
/// SINCRONA (<c>NDIlib_send_send_audio_v2</c>). A pagina "NDI Sending On High Latency Connections"
/// documenta que o envio da NDI de modo geral e confiavel (com reenvio) e que, em conexoes de
/// latencia mais alta, uma chamada de envio pode precisar esperar "um round-trip-time inteiro" antes
/// de liberar o buffer — o MESMO mecanismo ja comprovado (Sprint NDI-video-async) fazendo a antiga
/// chamada sincrona de VIDEO bloquear a thread inteira durante a transmissao de rede de verdade, nao
/// so copiar buffer. Antes desta sprint, essa chamada de audio bloqueante acontecia DENTRO da propria
/// <see cref="AudioThreadLoop"/> — a mesma thread responsavel pelo pacing por relogio de parede e
/// pela leitura do arquivo. Um engasgo de rede (plausivel com uma segunda placa de rede monitorando o
/// mesmo sinal) trava essa thread; o relogio de parede continua andando; quando a chamada volta, o
/// loop se ve "atrasado" e manda blocos em rajada pra recuperar — e a correcao de deriva (que ja
/// produz um "pulo audivel" por design, ver acima) fica mais propensa a disparar por causa desse
/// atraso artificial. Padrao compativel com "cd arranhado": nao corrupcao de dado, uma thread de
/// tempo real bloqueada por I/O de rede que ela nao deveria estar fazendo.
///
/// Correcao (nao muda NENHUMA logica de decodificacao/pacing/deriva acima, so onde o bloco JA PRONTO
/// sai desta classe): <see cref="AudioThreadLoop"/> nao dispara mais <see cref="AudioFrameReady"/>
/// diretamente — em vez disso, enfileira o bloco numa <see cref="BlockingCollection{T}"/> limitada
/// (<see cref="SendQueueCapacityBlocks"/>) e volta IMEDIATAMENTE pro pacing. Uma thread NOVA e
/// dedicada (<see cref="AudioSendThreadLoop"/>) consome essa fila e e quem de fato dispara
/// <see cref="AudioFrameReady"/> (e portanto quem sofre o bloqueio de rede, se houver) — isolada da
/// thread de pacing/leitura. Se a fila encher (rede muito atrasada por tempo suficiente pra esgotar
/// o buffer), o bloco MAIS ANTIGO e descartado pra abrir espaco pro mais novo: perder um pedaco de
/// audio que ja "venceu" seu tempo de exibicao e preferivel a acumular atraso sem fim (prioridade #1
/// do projeto, Latencia). Fila pequena de proposito (30 blocos de 10ms = ~300ms) — so pra absorver um
/// engasgo passageiro, nunca virar um buffer de atraso permanente.
///
/// CAUSA RAIZ REAL (Sprint NDI-audio-drift-startup, 2026-09-12) — a sprint acima NAO resolveu o
/// sintoma: usuario confirmou "cd riscado, engasgando" continua IDENTICO, presente DESDE O INICIO da
/// reproducao (nao so sob rede carregada) — descartando definitivamente a hipotese de rede. Revendo
/// <see cref="Engine.PlaybackEngine"/>: cada Deck decodifica o MESMO arquivo em TRES pipelines
/// totalmente independentes, sem nenhuma barreira de sincronizacao de inicio entre eles —
/// <c>_mediaPlayer</c> (preview local, LibVLC, e quem alimenta <see cref="SyncToVideoPosition"/> via
/// <c>MediaPlayer.TimeChanged</c>), <c>_framePlayer</c> (video pro NDI, LibVLC) e esta classe (audio
/// pro NDI, NAudio, pacing por relogio de parede puro). O LibVLC tipicamente tem um atraso de
/// pre-carga/buffer antes de <c>TimeChanged</c> comecar a refletir posicao real — enquanto isso, o
/// pacing desta classe (sem NENHUM buffer, so relogio de parede) ja avancou normalmente desde o
/// instante exato de <see cref="Play"/>. Isso cria uma diferenca GRANDE e ESPURIA logo nos primeiros
/// instantes (nada a ver com deriva real), grande o suficiente pra passar do
/// <see cref="DriftCorrectionThresholdMs"/> — <see cref="TryCorrectDrift"/> entao FORCA o audio pra
/// TRAS (repete um trecho ja tocado) pra "alcancar" um video que na real so estava atrasado pelo
/// proprio buffer dele. Checado a cada <see cref="DriftCheckIntervalMs"/> (~1s), o padrao se repete
/// — exatamente um "cd riscado" rítmico desde o inicio, batendo com os dois dados confirmados pelo
/// usuario (rítmico/engasgando E presente desde o primeiro instante).
///
/// Correcao (SO em <see cref="TryCorrectDrift"/> — nenhuma mudanca na fila/thread de envio da sprint
/// anterior, nem no pacing/decodificacao): (a) carencia inicial de
/// <see cref="DriftCorrectionStartupGraceMs"/> (contada em tempo de reproducao ATIVA via
/// <see cref="_pacingClock"/>, que ja exclui tempo pausado) — nenhuma correcao acontece antes disso,
/// dando tempo do <c>_mediaPlayer</c> sair do atraso de buffer inicial do LibVLC; (b) so corrige de
/// fato quando a MESMA deriva aparece em DUAS checagens seguidas (~1s de intervalo) — evita reagir a
/// um pico isolado e passageiro (ex.: um <c>Read()</c> lento por disco, uma pausa de GC) como se
/// fosse deriva real. Deriva genuina e duradoura (o motivo original deste mecanismo existir)
/// continua sendo corrigida normalmente, so que confirmada por duas leituras em vez de uma.
///
/// CORRECAO FICA MENOS ABRUPTA (Sprint NDI-audio-drift-suave, 2026-09-12) — usuario confirmou "
/// diminuiu as falhas mas ainda acontece" depois da sprint acima: a carencia/confirmacao dupla
/// reduziu QUANTAS VEZES a correcao dispara (menos falso-positivo de inicializacao), mas nunca mudou
/// COMO ela corrige quando dispara de verdade — ainda era <c>reader.CurrentTime = videoPosition</c>,
/// um salto INTEIRO de uma vez so, sempre audivel não importa a causa (deriva real remanescente, ou
/// um atraso de buffer do LibVLC ocasionalmente maior que os <see cref="DriftCorrectionStartupGraceMs"/>
/// de carencia). Correcao: em vez de saltar a diferenca inteira de uma vez, <see cref="TryCorrectDrift"/>
/// agora avanca no MAXIMO <see cref="DriftCorrectionMaxStepMs"/> por checagem (~1s), na direcao certa,
/// repetindo a cada checagem seguinte ate a diferenca cair abaixo de <see cref="DriftCorrectionStopMs"/>
/// — uma deriva grande converge em varios passos pequenos ao longo de alguns segundos, cada um bem
/// menos perceptivel que o salto completo de antes, em vez de um unico corte audivel. A carencia
/// inicial e a confirmacao de duas checagens seguidas (campo <see cref="_driftExceededStreak"/>,
/// renomeado/virou contador na Sprint NDI-audio-video-clock-lock, ver mais abaixo)
/// continuam exatamente iguais — so decidem QUANDO comecar uma sequencia de correcao; um novo campo
/// (<see cref="_driftCorrectionEngaged"/>) controla se ja esta "no meio" de uma sequencia de passos
/// pequenos em andamento.
///
/// FOLGA DA FILA DE ENVIO + DIAGNOSTICO (Sprint NDI-audio-buffer-diag, 2026-09-12) — depois das
/// Sprints NDI-clock-sync e NDI-video-uyvy, usuario reportou o mesmo engasgo de audio ("gaguejando"),
/// ainda so no NDI recebido (nunca no preview local), com evidencia nova que descarta as duas
/// hipoteses anteriores: rede cabeada gigabit (nao e falta de banda fisica) e CPU baixa (~3,6%,
/// nao e disputa de CPU). Hipotese atual: um soluco PASSAGEIRO de rede (a doc oficial da NDI admite
/// que o envio sincrono de audio pode bloquear "um round-trip inteiro" em condicoes ruins, mesmo numa
/// rede boa na maior parte do tempo) estoura a folga da fila entre <see cref="AudioThreadLoop"/> e
/// <see cref="AudioSendThreadLoop"/> (ver Sprint NDI-audio-envio-assincrono acima) e faz o codigo
/// descartar um bloco de audio de verdade — perda real de dado, nao so dessincronismo, e so audivel
/// em quem recebe o NDI (exatamente o padrao relatado). Mudanca: (a) a folga da fila dobra de 30 pra
/// 60 blocos (~300ms pra ~600ms — nao mais que isso de proposito, ver <see cref="SendQueueCapacityBlocks"/>);
/// (b) <see cref="LogDiagnostic"/>, um log TEMPORARIO que so grava nos eventos raros (bloco
/// descartado, ou profundidade da fila acima do normal — ver <see cref="QueueBacklogWarnBlocks"/>),
/// nunca no caminho normal de cada bloco. Deliberadamente NAO muda <see cref="TryCorrectDrift"/> pra
/// considerar o atraso da fila de envio (so a posicao de LEITURA) — prefere confirmar com o log, no
/// uso real, se a fila realmente acumula atraso antes de arriscar mais uma mudanca no mecanismo de
/// sincronismo, ja ajustado tres vezes nesta saga.
///
/// TRAVAMENTO CONTINUO NO RELOGIO DO VIDEO REAL (Sprint NDI-audio-video-clock-lock, 2026-09-12) —
/// usuario testou a Sprint NDI-audio-buffer-diag: log ficou vazio (nenhum bloco descartado, fila
/// nunca acumulou atraso, nenhum envio lento) — descarta de vez a hipotese de rede/fila como causa do
/// "gaguejando", que continuou identico. Usuario perguntou por que nao "usar o mesmo formato/taxa do
/// OBS" — resposta: o OBS nunca precisa de nenhuma correcao de deriva porque tem UM pipeline so
/// (video e audio saem do MESMO decodificador, mesmo relogio interno, por construcao); a Cartucheira
/// decodifica em TRES lugares independentes (preview, captura de video, audio via NAudio), entao
/// drift e estrutural, nao um bug pontual. Literalmente unificar o decodificador (audio saindo do
/// MESMO LibVLC que decodifica o video, como o OBS faz) foi cogitado e DELIBERADAMENTE REJEITADO:
/// e exatamente o caminho que causou os sete crashes ao vivo (AccessViolationException/
/// ExecutionEngineException) documentados na Sprint NDI-audio-NAudio, que motivaram a migracao pra
/// NAudio pra comecar — reabrir essa via reintroduz um risco real e ja conhecido, contra a
/// prioridade #2 do projeto (Estabilidade).
///
/// Achado real ao revisar o codigo pra esta sprint: a correcao de deriva NUNCA comparou o audio com
/// o video que realmente vai pro NDI — comparava com <c>_mediaPlayer</c> (o player de PREVIEW, um
/// TERCEIRO decodificador independente), nao com <c>_framePlayer</c> (o que decodifica o video que de
/// fato sai pro NDI). Correcao desta sprint, SEM tocar em nenhum decodificador de audio: (a)
/// <see cref="SyncToVideoPosition"/> passa a ser alimentado pelo <c>_framePlayer</c>, nao mais pelo
/// preview (ver nota do hotfix logo abaixo sobre COMO essa leitura e feita); (b)
/// <see cref="DriftCheckIntervalMs"/> cai de 1000ms pra 50ms (20x mais frequente) e
/// <see cref="DriftCorrectionMaxStepMs"/> cai na mesma proporcao (40ms→2ms) — mesma VELOCIDADE de
/// convergencia por segundo, so que espalhada em passos 20x menores e 20x mais frequentes; (c) a
/// exigencia de "duas checagens seguidas" pra engajar virou <see cref="DriftConfirmationChecksRequired"/>
/// checagens seguidas (recalibrada pra cobrir a MESMA janela real de ~1s de confirmacao, so que
/// amostrada 20x mais fino — ver <see cref="_driftExceededStreak"/>). O objetivo e que a correcao,
/// que ate aqui era um "empurrao" perceptivel disparando de vez em quando, vire tao frequente e tao
/// pequena que fique praticamente continua e inaudivel — o mais perto que da de "os dois relogios
/// andarem grudados" sem unificar os decodificadores de verdade.
///
/// Sprint NDI-audio-video-clock-lock-hotfix (2026-09-12, mesmo dia): a sprint acima chamava
/// <see cref="SyncToVideoPosition"/> de DENTRO do callback nativo de exibicao de video do
/// <c>_framePlayer</c> (LibVLC). Isso causou um deadlock real: esse callback roda na thread interna
/// do LibVLC, e ler <c>MediaPlayer.Time</c> dali disputa um lock interno do player com
/// <c>MediaPlayer.Pause()</c> — ao pausar, a thread da interface ficava presa em <c>Pause()</c> e a
/// thread do LibVLC presa nessa leitura, travando a aplicacao inteira. Corrigido no
/// <see cref="PlaybackEngine"/>: a leitura de <c>_framePlayer.Time</c> saiu do callback nativo e
/// passou pra um timer comum (thread do ThreadPool), preservando a mesma referencia (video real) e
/// a mesma cadencia (~50ms) sem o risco de deadlock.
///
/// Sprint NDI-audio-video-clock-lock-hotfix-2 (2026-09-12, mesmo dia): usuario testou a v1.0.10 —
/// o travamento sumiu, mas reportou audio "robotizando" continuamente, pior que o pulo ocasional de
/// antes. Causa: o item (b)/(c) do paragrafo desta sprint acima (checar/corrigir 20x mais frequente,
/// em passos 20x menores) partiu de uma premissa errada — <c>AudioFileReader.CurrentTime = ...</c>
/// (a linha que de fato aplica a correcao, em <see cref="TryCorrectDrift"/>) e um SEEK real no
/// decodificador (Media Foundation), nao um ajuste barato. O TAMANHO do passo nunca foi o que causava
/// o "empurrao" audivel — foi o seek em si. Reduzir o passo sem reduzir a frequencia so trocou 1 seek
/// grande por segundo por 20 seeks pequenos por segundo, e 20 re-sincronizacoes de decodificador por
/// segundo soa como audio robotizado continuo. Revertido: <see cref="DriftCheckIntervalMs"/> e
/// <see cref="DriftCorrectionMaxStepMs"/> voltam aos valores de antes desta sprint (1000ms/40ms) — a
/// leitura de posicao do video (item (a) acima, e o hotfix de COMO ela e feita) continua exatamente
/// como estava, sem nenhuma mudanca: nunca foi o problema, so a taxa de SEEKS precisava voltar a ser
/// baixa. Ver comentarios de <see cref="DriftCheckIntervalMs"/>/<see cref="DriftCorrectionMaxStepMs"/>
/// pro detalhe completo.
/// </summary>
public sealed class NdiAudioCapture : IDisposable
{
    /// <summary>Formato fixo entregue por <see cref="AudioFrameReady"/> — mesmo formato que a NDI SDK ja consumia do mecanismo anterior (48kHz estereo float32 intercalado), sem negociacao dinamica.</summary>
    private const int AudioSampleRate = 48000;
    private const int AudioChannels = 2;

    /// <summary>Tamanho de bloco entregue por chamada — 10ms a 48kHz (480 amostras/canal). Pequeno de proposito (prioridade #1 do projeto, Latencia): blocos menores custam mais chamadas/s, mas reduzem o atraso entre "amostra decodificada" e "amostra no NDI".</summary>
    private const int BlockSamplesPerChannel = 480;

    /// <summary>
    /// Diferenca maxima tolerada entre a posicao do audio e a posicao do video antes da correcao
    /// secundaria (ver comentario de classe) ENGAJAR uma sequencia de correcao (depois de confirmada
    /// por <see cref="DriftConfirmationChecksRequired"/> checagens seguidas — ver
    /// <see cref="_driftExceededStreak"/>).
    ///
    /// Sprint NDI-audio-video-clock-lock-diag-2 (2026-09-13): de 200ms pra 750ms. O log de
    /// diagnostico da v1.0.12 mostrou que a leitura de <c>_framePlayer.Time</c> (a cada ~1s) NAO
    /// avanca de forma suave — varia entre 748ms e 1501ms por checagem (desvio padrao ~168ms),
    /// artefato do proprio LibVLC atualizando sua posicao interna em lotes, nao continuamente. Isso
    /// fazia <c>driftMs</c> oscilar ate ±546ms so por RUIDO de medicao, bem acima do limiar antigo de
    /// 200ms — resultado: 56 seeks reais aplicados em so 3,6 minutos de teste (~1 a cada 4s), a
    /// correcao reagindo ao ruido, nao a deriva real. 750ms fica com folga confortavel acima do maior
    /// ruido ja medido (546ms), mas ainda pega uma deriva real e grande se um dia acontecer (o teste
    /// do OBS, mesmo PC/mesma rede/mesmo receptor, sugere que a deriva real entre os dois
    /// decodificadores independentes pode nem ser significativa na pratica).
    /// </summary>
    private const int DriftCorrectionThresholdMs = 750;

    /// <summary>
    /// Sprint NDI-audio-drift-suave (2026-09-12): uma vez ENGAJADA uma sequencia de correcao (ver
    /// <see cref="_driftCorrectionEngaged"/>), <see cref="TryCorrectDrift"/> continua corrigindo a
    /// cada checagem ate a diferenca cair abaixo deste valor — deliberadamente menor que
    /// <see cref="DriftCorrectionThresholdMs"/> (histerese: evita reengajar imediatamente por causa
    /// de uma diferenca residual bem perto do limite de entrada).
    /// </summary>
    private const int DriftCorrectionStopMs = 50;

    /// <summary>
    /// Sprint NDI-audio-drift-suave (2026-09-12): quanto, no MAXIMO, uma unica aplicacao da correcao
    /// pode mover <see cref="AudioFileReader.CurrentTime"/> — em vez de saltar a diferenca inteira de
    /// uma vez (sempre audivel, o problema que esta sprint resolveu), uma deriva grande converge aos
    /// poucos, um passo pequeno por checagem (ver <see cref="DriftCheckIntervalMs"/>), cada um bem
    /// menos perceptivel que um corte completo.
    ///
    /// Sprint NDI-audio-video-clock-lock (2026-09-12): tentou 40ms→2ms (junto com checagem 20x mais
    /// frequente, ver <see cref="DriftCheckIntervalMs"/>) apostando que passos menores seriam menos
    /// perceptiveis. ERRADO — revertido na Sprint NDI-audio-video-clock-lock-hotfix-2 (2026-09-12,
    /// mesmo dia): ver o porque completo la. Resumo: <c>AudioFileReader.CurrentTime = ...</c> nao e um
    /// ajuste barato/inaudivel, e um SEEK de verdade no decodificador (Media Foundation) — o TAMANHO
    /// do passo nunca foi o que causava o "empurrao" audivel, foi o seek em si. Reduzir o passo sem
    /// reduzir a FREQUENCIA so trocou "1 seek grande por segundo" por "20 seeks pequenos por segundo",
    /// que soa como audio robotizado continuo — bem pior que o pulo ocasional de antes. De volta a
    /// 40ms, na mesma cadencia (~1s) de antes da Sprint NDI-audio-video-clock-lock.
    /// </summary>
    private const int DriftCorrectionMaxStepMs = 40;

    /// <summary>
    /// Intervalo minimo entre duas checagens/correcoes de deriva.
    ///
    /// Sprint NDI-audio-video-clock-lock (2026-09-12): tentou 1000ms→50ms (checar e corrigir 20x mais
    /// rapido) — mesma premissa errada do <see cref="DriftCorrectionMaxStepMs"/> acima: cada correcao
    /// aplicada e um seek real no decodificador, entao 20x mais frequente virou 20 seeks/segundo em vez
    /// de 1, causando audio "robotizando" continuamente (relatado pelo usuario logo depois de instalar
    /// a v1.0.10, que so corrigia OUTRO bug desta mesma sprint — o travamento ao pausar, sem tocar
    /// nisto). Revertido na Sprint NDI-audio-video-clock-lock-hotfix-2 (2026-09-12, mesmo dia) de volta
    /// pra 1000ms — a CORRECAO (o seek em si) volta a ser rara, no maximo 1x/segundo, como era ate a
    /// v1.0.8. A leitura da posicao do video (<see cref="NdiAudioCapture.SyncToVideoPosition"/>, via
    /// <see cref="PlaybackEngine.PollFramePlayerPosition"/>) continua barata e frequente (a cada 50ms)
    /// — isso nunca foi o problema, so a taxa de SEEKS precisava voltar a ser baixa.
    /// </summary>
    private const int DriftCheckIntervalMs = 1000;

    /// <summary>
    /// Janela de tempo real (em ms) que uma deriva precisa se sustentar, CONSECUTIVAMENTE, antes de
    /// <see cref="TryCorrectDrift"/> engajar uma sequencia de correcao — evita reagir a um pico isolado
    /// e passageiro (ex.: um <c>Read()</c> lento por disco, uma pausa de GC, um atraso de buffer do
    /// LibVLC). Sprint NDI-audio-video-clock-lock-hotfix-2 (2026-09-12): 2000ms — junto com
    /// <see cref="DriftCheckIntervalMs"/> de volta a 1000ms, da exatamente as "duas checagens seguidas"
    /// (~2s de confirmacao) que o mecanismo sempre teve antes da Sprint NDI-audio-video-clock-lock (que
    /// tinha recalibrado esta janela pra granularidade de 50ms, desfeita junto com o resto desta
    /// reversao). Ver <see cref="DriftConfirmationChecksRequired"/>.
    /// </summary>
    private const int DriftConfirmationWindowMs = 2000;

    /// <summary>
    /// Quantas checagens CONSECUTIVAS de deriva acima do limiar (<see cref="DriftCorrectionThresholdMs"/>)
    /// sao exigidas antes de engajar uma sequencia de correcao — <see cref="DriftConfirmationWindowMs"/>
    /// dividido por <see cref="DriftCheckIntervalMs"/> (2000/1000 = 2), calculado em tempo de
    /// compilacao. Preserva a exigencia original de "duas checagens seguidas" que o mecanismo sempre
    /// teve, expressa como formula pra continuar correta se qualquer um dos dois valores mudar de novo.
    /// </summary>
    private const int DriftConfirmationChecksRequired = DriftConfirmationWindowMs / DriftCheckIntervalMs;

    /// <summary>Falhas seguidas de <c>chain.Read</c> (ver comentario no laco de <see cref="AudioThreadLoop"/> sobre o 8º crash da saga) toleradas antes de desistir e encerrar a thread — cobre um blip passageiro do Media Foundation sem tentar pra sempre num arquivo genuinamente com problema.</summary>
    private const int MaxConsecutiveReadFailures = 10;

    /// <summary>
    /// Sprint NDI-audio-video-clock-lock-diag (2026-09-13) — TEMPORARIO: quantos segundos de audio
    /// (tempo de reproducao ativa, somado entre Play/Stop se necessario) sao gravados num .wav local
    /// pra diagnostico (ver <see cref="FinalizeDiagnosticWav"/>). Usuario testou OBS a partir do MESMO
    /// PC, pra o MESMO PC receptor, e o audio chegou limpo — descarta de vez rede/RX/hardware de audio
    /// como causa, sobrando só a propria Cartucheira (TX) como suspeita. Este .wav captura o audio
    /// EXATAMENTE como ele sai desta classe pra <see cref="AudioFrameReady"/> (o mesmo bloco que
    /// <c>DeckViewModel</c> repassa sem nenhuma alteracao pra <c>NdiSender.SendAudioFrame</c>) — se
    /// ele sair robotizado, o problema esta aqui dentro, antes mesmo de qualquer rede; se sair limpo,
    /// o problema seria em outro lugar (mas o teste do OBS ja torna isso bem improvavel).
    ///
    /// Sprint NDI-audio-video-clock-lock-diag-2 (2026-09-13): de 60s pra 180s (3 minutos). Analise do
    /// primeiro .wav (60s) achou pelo menos um trecho robotizado (~t=40s) SEM nenhum SEEK ou evento de
    /// fila/rede correspondente no log — evidencia de uma SEGUNDA causa de distorcao, ainda nao
    /// identificada, independente da correcao de deriva. 60s so cobriu o inicio do teste (que teve
    /// uma rajada de correcoes logo apos o Play — achado esperado, ver comentario de
    /// DriftCorrectionThresholdMs); 180s cobre uma faixa bem maior do teste tipico (~3-4min ate aqui)
    /// pra dar mais chance de capturar outras ocorrencias desse segundo problema e ajudar a achar um
    /// padrao. 180s a 48kHz estereo float32 é ~69MB por Deck — mais pesado que os 23MB de antes, mas
    /// ainda aceitavel pra um arquivo TEMPORARIO de diagnostico (nunca fica no build final).
    /// </summary>
    private const int DiagnosticWavDurationSeconds = 180;

    /// <summary>
    /// Sprint NDI-audio-envio-assincrono (2026-09-12): tamanho maximo da fila entre
    /// <see cref="AudioThreadLoop"/> (pacing/decodificacao) e <see cref="AudioSendThreadLoop"/> (envio
    /// pra rede, ver comentario de classe pro diagnostico completo). Buffer antes de comecar a
    /// descartar o bloco mais antigo. Numero pequeno de proposito (prioridade #1 do projeto,
    /// Latencia): so existe pra absorver um engasgo de rede passageiro, nunca pra virar um buffer de
    /// atraso permanente.
    ///
    /// Sprint NDI-audio-buffer-diag (2026-09-12): de 30 blocos (~300ms) pra 60 blocos (~600ms) —
    /// usuario reportou engasgo de audio (so no NDI recebido, nunca no preview local) mesmo depois
    /// das Sprints NDI-clock-sync e NDI-video-uyvy, com rede cabeada gigabit (banda fisica nao e
    /// gargalo) e CPU baixa (~3,6%) durante o teste — descartando as duas hipoteses anteriores.
    /// Hipotese atual: um soluco PASSAGEIRO de rede (a doc oficial da NDI admite que o envio pode
    /// bloquear "um round-trip inteiro" em condicoes ruins) estoura os 300ms de folga antigos e faz
    /// o codigo descartar audio de verdade (ver o descarte em <see cref="AudioThreadLoop"/>) —
    /// dobrar a folga reduz a chance disso sem deixar a janela de atraso nao-detectado grande demais
    /// (ver <see cref="TryCorrectDrift"/>, que so enxerga a posicao de LEITURA, nao a de envio: um
    /// buffer muito maior arriscaria acumular atraso real sem a correcao de deriva perceber). Nao
    /// mexe na formula de deriva nesta sprint de proposito — so o log novo (<see cref="LogDiagnostic"/>)
    /// vai confirmar se a fila realmente acumula atraso na pratica antes de qualquer mudanca ali.
    /// </summary>
    private const int SendQueueCapacityBlocks = 60;

    /// <summary>
    /// Sprint NDI-audio-buffer-diag (2026-09-12): profundidade da fila de envio acima da qual
    /// <see cref="TryCorrectDrift"/> registra uma linha no log de diagnostico (ver
    /// <see cref="LogDiagnostic"/>) — 15 blocos (~150ms) e bem acima do que a fila deveria ter em uso
    /// normal (deveria ficar perto de zero, ja que ler e enviar andam praticamente juntos), entao
    /// ultrapassar isso e sinal real de atraso se acumulando, nao ruido de medicao.
    /// </summary>
    private const int QueueBacklogWarnBlocks = 15;

    /// <summary>
    /// Sprint NDI-audio-drift-startup (2026-09-12): tempo de reproducao ATIVA (via
    /// <see cref="_pacingClock"/>, que ja exclui tempo pausado) antes do qual
    /// <see cref="TryCorrectDrift"/> nunca corrige nada, mesmo que a diferenca passe do limite normal
    /// — ver comentario de classe pro diagnostico completo (atraso de pre-carga/buffer do LibVLC no
    /// inicio da reproducao, que nao tem nada a ver com deriva real).
    /// </summary>
    private const int DriftCorrectionStartupGraceMs = 2000;

    /// <summary>
    /// Sprint NDI-audio-buffer-diag (2026-09-12): contador estatico so pra dar um identificador curto
    /// a cada instancia (um Deck normalmente cria uma unica <see cref="NdiAudioCapture"/> pra vida
    /// toda) nas linhas do log de diagnostico (<see cref="LogDiagnostic"/>) — sem precisar passar o
    /// nome do canal/Deck de fora (fora do escopo desta sprint, que so toca esta classe e
    /// <c>NdiSender</c>). <see cref="Interlocked.Increment(ref int)"/> por seguranca, mesmo sem
    /// concorrencia real esperada na construcao de Decks.
    /// </summary>
    private static int _instanceCounter;

    private readonly int _instanceId = Interlocked.Increment(ref _instanceCounter);

    /// <summary>
    /// Sprint NDI-audio-video-clock-lock-diag (2026-09-13) — TEMPORARIO, ver
    /// <see cref="DiagnosticWavDurationSeconds"/>. Buffer PRE-ALOCADO de proposito: só
    /// <see cref="AudioSendThreadLoop"/> escreve nele (uma unica thread, sem concorrencia, sem
    /// necessidade de lock pra copiar), e alocar uma vez evita qualquer realocacao/GC no meio da
    /// captura — mesma prioridade de sempre (Latencia) mesmo num mecanismo temporario de diagnostico.
    /// </summary>
    private readonly float[] _diagnosticWavBuffer = new float[DiagnosticWavDurationSeconds * AudioSampleRate * AudioChannels];

    private int _diagnosticWavWritePos;
    private int _diagnosticWavFinalizing;

    private readonly object _gate = new();
    private readonly Stopwatch _pacingClock = new();

    private AudioFileReader? _reader;
    private ISampleProvider? _chain;
    private Thread? _thread;
    private BlockingCollection<float[]>? _sendQueue;
    private Thread? _sendThread;
    private volatile bool _stopRequested;
    private volatile bool _loopEnabled;
    private bool _disposed;

    /// <summary>Ultima posicao de video conhecida (ticks de <see cref="TimeSpan"/>) reportada via <see cref="SyncToVideoPosition"/> — <c>-1</c> = nenhuma ainda. <see cref="long"/> e naturalmente atomico pra leitura/escrita em runtimes 64-bit (.NET 8/WPF desktop), sem necessidade de lock nem <see cref="System.Threading.Interlocked"/> pra este uso (um produtor, um consumidor, valor sempre consistente mesmo sem sincronizacao explicita).</summary>
    private long _lastVideoPositionTicks = -1;

    /// <summary>
    /// Sprint NDI-audio-drift-startup (2026-09-12): quantas checagens de deriva SEGUIDAS ja passaram
    /// do limite — usado por <see cref="TryCorrectDrift"/> pra exigir <see cref="DriftConfirmationChecksRequired"/>
    /// checagens CONSECUTIVAS antes de ENGAJAR uma sequencia de correcao (ver comentario de classe e
    /// <see cref="_driftCorrectionEngaged"/>). So acessado pela thread de <see cref="AudioThreadLoop"/>
    /// (mesma thread que chama <see cref="TryCorrectDrift"/>) — sem necessidade de lock nem volatile.
    ///
    /// Sprint NDI-audio-video-clock-lock (2026-09-12): era um <see cref="bool"/> (só "a checagem
    /// anterior passou do limite?", suficiente quando bastavam duas checagens seguidas) — virou um
    /// contador genérico pra suportar qualquer <see cref="DriftConfirmationChecksRequired"/>. Chegou a
    /// exigir 20 (na granularidade de 50ms/checagem daquela sprint); revertido pra 2 na Sprint
    /// NDI-audio-video-clock-lock-hotfix-2 (2026-09-12, mesmo dia — ver comentario de classe) junto com
    /// a checagem voltando a 1000ms, mas o contador em si (em vez do bool antigo) foi mantido — mesmo
    /// comportamento, código mais simples de ajustar se a granularidade mudar de novo.
    /// </summary>
    private int _driftExceededStreak;

    /// <summary>
    /// Sprint NDI-audio-drift-suave (2026-09-12): verdadeiro enquanto uma sequencia de correcao
    /// gradual esta em andamento (ver comentario de classe) — <see cref="TryCorrectDrift"/> continua
    /// aplicando passos pequenos (no maximo <see cref="DriftCorrectionMaxStepMs"/> por checagem) a
    /// cada ciclo enquanto isto for verdadeiro, ate a diferenca cair abaixo de
    /// <see cref="DriftCorrectionStopMs"/>. So acessado pela thread de <see cref="AudioThreadLoop"/>
    /// — sem necessidade de lock nem volatile.
    /// </summary>
    private bool _driftCorrectionEngaged;

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

            // Sprint NDI-audio-drift-startup/drift-suave (2026-09-12): reseta a contagem de
            // checagens seguidas e qualquer sequencia de correcao em andamento — um Load() novo
            // comeca uma sessao de reproducao nova, sem nenhum historico de deriva herdado da sessao
            // anterior (ver comentario de classe/campos).
            _driftExceededStreak = 0;
            _driftCorrectionEngaged = false;

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

                // Sprint NDI-audio-envio-assincrono (2026-09-12): fila+thread NOVAS a cada inicio de
                // reproducao (ver comentario de classe) — a fila anterior (se houve uma) ja foi
                // completada/descartada por StopInternal antes de _thread voltar a null, entao nunca
                // ha duas geracoes vivas ao mesmo tempo.
                var sendQueue = new BlockingCollection<float[]>(SendQueueCapacityBlocks);
                _sendQueue = sendQueue;
                _sendThread = new Thread(() => AudioSendThreadLoop(sendQueue)) { IsBackground = true, Name = "NdiAudioSend" };
                _sendThread.Start();

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
    /// Alimenta a correcao secundaria de deriva (ver comentario de classe) com a posicao mais recente
    /// do vídeo. Seguro chamar de qualquer thread (a escrita e de um <see cref="long"/>, atomica em
    /// runtimes 64-bit, sem necessidade de lock).
    ///
    /// Sprint NDI-audio-video-clock-lock (2026-09-12): ate esta sprint, era chamado pelo
    /// <see cref="PlaybackEngine"/> a cada <c>MediaPlayer.TimeChanged</c> do player de PREVIEW
    /// (<c>_mediaPlayer</c>) — um TERCEIRO decodificador, diferente do que realmente decodifica o
    /// video que vai pro NDI (<c>_framePlayer</c>). Ou seja, a correcao de deriva nunca mirou no alvo
    /// certo: o audio podia estar perfeitamente alinhado com o PREVIEW e ainda assim desalinhado do
    /// video de verdade saindo pro NDI. Passou a ser alimentado pela posicao real de
    /// <c>_framePlayer</c>, lida a cada ~50ms (nao mais algumas vezes por segundo como o
    /// <c>TimeChanged</c> do preview).
    ///
    /// Sprint NDI-audio-video-clock-lock-hotfix (2026-09-12, mesmo dia): a sprint acima chamava
    /// esta funcao de DENTRO do callback nativo de exibicao de video do LibVLC — causou um deadlock
    /// real ao pausar (ver comentario de classe pro diagnostico completo). Corrigido: quem chama
    /// esta funcao agora e <see cref="PlaybackEngine.PollFramePlayerPosition"/>, um timer comum
    /// (thread do ThreadPool), nunca mais de dentro de um callback do LibVLC.
    /// </summary>
    public void SyncToVideoPosition(TimeSpan videoPosition)
    {
        Volatile.Write(ref _lastVideoPositionTicks, videoPosition.Ticks);
    }

    private void StopInternal()
    {
        Thread? thread;
        Thread? sendThread;
        BlockingCollection<float[]>? sendQueue;

        lock (_gate)
        {
            _stopRequested = true;
            thread = _thread;
            _thread = null;
            sendThread = _sendThread;
            _sendThread = null;
            sendQueue = _sendQueue;
            _sendQueue = null;
        }

        // Fora do lock: a propria AudioThreadLoop precisa do _gate pra ler _reader/_chain a cada
        // iteracao — segurar o lock aqui enquanto espera a thread parar seria um deadlock potencial.
        thread?.Join();

        // Sprint NDI-audio-envio-assincrono (2026-09-12): so fecha a fila de envio pra novos itens
        // (CompleteAdding) DEPOIS que a thread produtora (AudioThreadLoop, join acima) ja garantidamente
        // parou de chamar TryAdd — evita uma corrida onde um ultimo bloco decodificado seria
        // silenciosamente perdido contra uma fila ja fechada. AudioSendThreadLoop esvazia o que
        // sobrou (GetConsumingEnumerable termina sozinho quando a fila fica vazia E completada) antes
        // do Join abaixo retornar.
        sendQueue?.CompleteAdding();
        sendThread?.Join();
        sendQueue?.Dispose();

        lock (_gate)
        {
            _pacingClock.Reset();

            // Sprint NDI-audio-drift-startup/drift-suave (2026-09-12): reseta a contagem de
            // checagens seguidas e qualquer sequencia de correcao em andamento — o proximo Play()
            // comeca uma nova geracao de AudioThreadLoop (e portanto de _pacingClock, ver Play()),
            // sem nenhum historico de deriva herdado.
            _driftExceededStreak = 0;
            _driftCorrectionEngaged = false;

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

        // Sprint NDI-audio-envio-assincrono (2026-09-12): capturada UMA vez aqui — fixa pela vida
        // inteira desta geracao de thread (Play() sempre cria fila+thread juntas, e uma nova geracao
        // so comeca depois de StopInternal ter encerrado esta por completo, ver comentario de
        // classe/Play/StopInternal).
        var sendQueue = _sendQueue;

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

            // Sprint NDI-audio-envio-assincrono (2026-09-12): NAO dispara mais AudioFrameReady direto
            // daqui (ver comentario de classe pro diagnostico completo) — so enfileira e volta
            // IMEDIATAMENTE pro pacing. Quem de fato dispara o evento (e quem sofre o bloqueio de
            // rede da chamada NDI sincrona de audio, se houver) e a thread dedicada nova,
            // AudioSendThreadLoop, isolada desta.
            if (sendQueue is not null && !sendQueue.IsAddingCompleted && !sendQueue.TryAdd(block))
            {
                // Fila cheia — a thread de envio esta mais lenta que a decodificacao/pacing (ex.:
                // rede congestionada). Descarta o bloco MAIS ANTIGO pra abrir espaco: perder audio
                // que ja "venceu" seu tempo de exibicao e preferivel a acumular atraso sem fim
                // (prioridade #1, Latencia). TryTake pode falhar numa corrida rarissima com o proprio
                // consumidor esvaziando a fila ao mesmo tempo (so 1 produtor, 1 consumidor aqui) — se
                // isso acontecer, so aceita perder este bloco em vez de insistir.
                if (sendQueue.TryTake(out _))
                {
                    sendQueue.TryAdd(block);

                    // Sprint NDI-audio-buffer-diag (2026-09-12): evento RARO (fila cheia de verdade)
                    // — gravar em disco aqui nunca vira custo por bloco no caminho normal, so nesta
                    // excecao. Registra pra confirmar (ou descartar) com dado real se isso esta
                    // acontecendo na pratica, e com que frequencia.
                    LogDiagnostic($"DESCARTE instancia={_instanceId} — bloco de audio perdido (fila de envio cheia, {SendQueueCapacityBlocks} blocos/{SendQueueCapacityBlocks * 10}ms de folga esgotados)");
                }
            }
        }
    }

    /// <summary>
    /// Sprint NDI-audio-envio-assincrono (2026-09-12): corpo da thread dedicada de ENVIO (consome
    /// <paramref name="queue"/>, alimentada por <see cref="AudioThreadLoop"/> — ver comentario de
    /// classe pro diagnostico/racional completo). E esta thread, nao a de pacing, que dispara
    /// <see cref="AudioFrameReady"/> — e portanto quem eventualmente bloqueia, se a chamada de rede
    /// sincrona da NDI SDK (<c>NDIlib_send_send_audio_v2</c>, sem variante assincrona nesta SDK)
    /// demorar. <see cref="BlockingCollection{T}.GetConsumingEnumerable()"/> bloqueia esperando o
    /// proximo item e termina sozinho quando a fila fica vazia E <c>CompleteAdding</c> foi chamado
    /// (ver <see cref="StopInternal"/>) — sem necessidade de checar nenhuma flag de parada aqui.
    /// </summary>
    private void AudioSendThreadLoop(BlockingCollection<float[]> queue)
    {
        foreach (var block in queue.GetConsumingEnumerable())
        {
            // Sprint NDI-audio-video-clock-lock-diag (2026-09-13) — TEMPORARIO: copia (memcpy puro,
            // sem I/O aqui) o MESMO bloco que vai pra AudioFrameReady logo abaixo, byte a byte igual
            // ao que a NDI SDK recebe. So um Array.Copy num buffer ja pre-alocado — custo desprezivel,
            // nada de disco nesta thread (ver comentario de FinalizeDiagnosticWav pro motivo de NUNCA
            // fazer I/O de arquivo aqui). Para sozinho depois de DiagnosticWavDurationSeconds de audio
            // (buffer pre-alocado do tamanho exato, sem crescer).
            if (_diagnosticWavWritePos < _diagnosticWavBuffer.Length)
            {
                var copyCount = Math.Min(block.Length, _diagnosticWavBuffer.Length - _diagnosticWavWritePos);
                Array.Copy(block, 0, _diagnosticWavBuffer, _diagnosticWavWritePos, copyCount);
                _diagnosticWavWritePos += copyCount;

                if (_diagnosticWavWritePos >= _diagnosticWavBuffer.Length)
                {
                    // Buffer cheio — grava no disco numa thread do ThreadPool, NUNCA aqui (ver
                    // comentario de FinalizeDiagnosticWav).
                    ThreadPool.QueueUserWorkItem(_ => FinalizeDiagnosticWav());
                }
            }

            try
            {
                AudioFrameReady?.Invoke(this, new EngineAudioFrameReadyEventArgs(block, AudioSampleRate, AudioChannels, block.Length / AudioChannels));
            }
            catch (Exception)
            {
                // Quem ouve este evento (NdiSender, via DeckViewModel) faz uma chamada de rede
                // sincrona pra NDI SDK — uma falha inesperada aqui nunca deve derrubar a thread de
                // envio nem o Deck. Falha honesta: bloco perdido, proximo bloco da fila continua
                // normalmente.
            }
        }
    }

    /// <summary>
    /// Compara a posicao atual do leitor de audio com a ultima posicao de video conhecida
    /// (<see cref="SyncToVideoPosition"/>, desde a Sprint NDI-audio-video-clock-lock alimentada pelo
    /// video REAL que vai pro NDI, nao mais o preview) e, se necessario, corrige gradualmente — ver
    /// comentario de classe pro racional completo. Duas fases distintas: (1) ENGAJAR uma sequencia de
    /// correcao exige que a MESMA deriva apareca em <see cref="DriftConfirmationChecksRequired"/>
    /// checagens seguidas (a cada <see cref="DriftCheckIntervalMs"/>), depois de uma carencia inicial
    /// de <see cref="DriftCorrectionStartupGraceMs"/> (evita reagir ao atraso de buffer do LibVLC no
    /// inicio, ou a um pico isolado passageiro); (2) uma vez engajada, corrige em passos pequenos
    /// (no maximo <see cref="DriftCorrectionMaxStepMs"/> por checagem, nunca a diferenca inteira de
    /// uma vez) a cada checagem seguinte, ate a diferenca cair abaixo de
    /// <see cref="DriftCorrectionStopMs"/>.
    /// </summary>
    private void TryCorrectDrift(AudioFileReader reader)
    {
        // Sprint NDI-audio-buffer-diag (2026-09-12): checagem de profundidade da fila de envio —
        // DELIBERADAMENTE independente da carencia inicial/logica de deriva abaixo (e um sinal de
        // saude da fila de envio, nao de sincronismo A/V). So registra quando passar do limiar (ver
        // comentario de QueueBacklogWarnBlocks) — no dia a dia a fila deve ficar perto de zero, entao
        // isto nao deveria gerar nenhuma linha em uso normal.
        var queueDepth = _sendQueue?.Count ?? 0;
        if (queueDepth > QueueBacklogWarnBlocks)
        {
            LogDiagnostic($"FILA instancia={_instanceId} profundidade={queueDepth} blocos (~{queueDepth * 10}ms de audio esperando pra ser enviado)");
        }

        // Sprint NDI-audio-drift-startup (2026-09-12): carencia inicial, contada em tempo de
        // reproducao ATIVA (_pacingClock ja exclui tempo pausado) — nunca corrige antes disso.
        if (_pacingClock.Elapsed.TotalMilliseconds < DriftCorrectionStartupGraceMs)
        {
            return;
        }

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

        // Sprint NDI-audio-drift-suave (2026-09-12): SINALIZADO — positivo quando o audio esta
        // ATRASADO em relacao ao video (precisa avancar/pular pra frente pra alcancar), negativo
        // quando o audio esta ADIANTADO (precisa voltar um pouco). O valor absoluto e o que decide
        // engajar/desengajar; o sinal decide a DIRECAO de cada passo pequeno abaixo.
        var driftMs = (videoPosition - audioPosition).TotalMilliseconds;
        var absDriftMs = Math.Abs(driftMs);

        // Sprint NDI-audio-video-clock-lock-diag (2026-09-13) — TEMPORARIO: uma linha por checagem
        // (no maximo 1x/segundo, ver DriftCheckIntervalMs — custo desprezivel, bem diferente do
        // caminho por-bloco que este arquivo sempre evitou). Objetivo: ver com numeros reais se a
        // deriva medida contra o video REAL (desde a Sprint NDI-audio-video-clock-lock) fica pequena
        // e estavel (correcao raramente engaja) ou grande/persistente (engaja quase o tempo todo,
        // explicando uma "robotizacao" continua em vez de um pulo ocasional).
        LogDiagnostic($"MEDIDA instancia={_instanceId} audioMs={audioPosition.TotalMilliseconds:F0} videoMs={videoPosition.TotalMilliseconds:F0} driftMs={driftMs:F0} engajada={_driftCorrectionEngaged}");

        if (!_driftCorrectionEngaged)
        {
            if (absDriftMs < DriftCorrectionThresholdMs)
            {
                // Sem deriva relevante nesta checagem — reseta a contagem de checagens seguidas (a
                // sustentacao exigida pra engajar precisa ser CONSECUTIVA, um "sim" isolado entre
                // dois "nao" nao conta).
                _driftExceededStreak = 0;
                return;
            }

            // Exige DriftConfirmationChecksRequired checagens CONSECUTIVAS acima do limite antes de
            // engajar — evita reagir a um pico isolado e passageiro (ex.: um Read() lento por disco,
            // uma pausa de GC, ou um atraso de buffer do LibVLC ocasionalmente maior que a carencia
            // inicial). Chegou a exigir 20 checagens a 50ms cada (Sprint NDI-audio-video-clock-lock);
            // revertido pra 2 checagens a 1000ms cada (Sprint NDI-audio-video-clock-lock-hotfix-2,
            // 2026-09-12 — ver comentario de classe pro motivo: a frequencia alta nao era o problema
            // aqui, mas na cadencia de SEEKS aplicados logo abaixo).
            _driftExceededStreak++;
            if (_driftExceededStreak < DriftConfirmationChecksRequired)
            {
                return;
            }

            // Confirmado por DriftConfirmationChecksRequired checagens seguidas — engaja uma
            // sequencia de correcao gradual. Cai direto pro bloco de aplicar um passo abaixo, na
            // MESMA checagem que engajou (sem esperar mais um ciclo so pra comecar a corrigir).
            _driftExceededStreak = 0;
            _driftCorrectionEngaged = true;
        }

        // Sprint NDI-audio-drift-suave (2026-09-12): ja engajada — continua corrigindo um passo
        // pequeno por checagem ate a diferenca cair abaixo do limiar de parada (menor que o de
        // entrada de proposito, histerese: evita reengajar imediatamente por causa de um residuo
        // bem perto do limite).
        if (absDriftMs < DriftCorrectionStopMs)
        {
            _driftCorrectionEngaged = false;
            return;
        }

        var stepMs = Math.Min(absDriftMs, DriftCorrectionMaxStepMs);
        var signedStepMs = driftMs >= 0 ? stepMs : -stepMs;
        var newPosition = audioPosition + TimeSpan.FromMilliseconds(signedStepMs);

        try
        {
            reader.CurrentTime = newPosition;
            // Sprint NDI-audio-video-clock-lock-diag (2026-09-13) — TEMPORARIO: marca exatamente
            // quando um SEEK de verdade e aplicado (ver comentario de classe da Sprint
            // NDI-audio-video-clock-lock-hotfix-2 sobre CurrentTime ser um seek real, nao um ajuste
            // barato) — pra cruzar com o .wav gravado em FinalizeDiagnosticWav e ver se o instante
            // de cada seek bate com um trecho robotizado no audio capturado.
            LogDiagnostic($"SEEK instancia={_instanceId} passoMs={signedStepMs:F0} novaPosMs={newPosition.TotalMilliseconds:F0}");
        }
        catch (Exception)
        {
            // Fronteira nativa — falha honesta, so desiste deste passo especifico; continua
            // engajada, a proxima checagem (1s depois) tenta de novo.
        }
    }

    /// <summary>
    /// Sprint NDI-audio-video-clock-lock-diag (2026-09-13) — TEMPORARIO: grava
    /// <see cref="_diagnosticWavBuffer"/> (o audio exato entregue a <see cref="AudioFrameReady"/>,
    /// ver <see cref="AudioSendThreadLoop"/>) num arquivo .wav local, UMA UNICA VEZ, sempre fora da
    /// thread de envio (chamado via <see cref="ThreadPool.QueueUserWorkItem"/> quando o buffer enche,
    /// ou direto a partir de <see cref="Dispose"/> se a captura for encerrada antes disso — nunca de
    /// dentro de <see cref="AudioSendThreadLoop"/>, que precisa continuar rapida pra nao competir com
    /// a chamada sincrona de rede da NDI SDK). <see cref="Interlocked.CompareExchange(ref int, int, int)"/>
    /// garante que so uma finalizacao realmente escreve o arquivo, mesmo se o buffer encher E o app
    /// for fechado quase ao mesmo tempo. Path:
    /// <c>%AppData%\CartucheiraVideo\ndi-audio-tx-diagnostico-{instancia}.wav</c> — 32-bit float,
    /// 48kHz estereo, sem nenhuma conversao (o mesmo formato que <see cref="AudioFrameReady"/> ja
    /// entrega). Falha honesta: qualquer erro de E/S e engolido — mesmo padrao de
    /// <see cref="LogDiagnostic"/>.
    /// </summary>
    private void FinalizeDiagnosticWav()
    {
        if (Interlocked.CompareExchange(ref _diagnosticWavFinalizing, 1, 0) != 0)
        {
            return;
        }

        var lengthFloats = _diagnosticWavWritePos;
        if (lengthFloats <= 0)
        {
            return;
        }

        try
        {
            var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CartucheiraVideo");
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, $"ndi-audio-tx-diagnostico-{_instanceId}.wav");
            using var writer = new WaveFileWriter(path, WaveFormat.CreateIeeeFloatWaveFormat(AudioSampleRate, AudioChannels));
            writer.WriteSamples(_diagnosticWavBuffer, 0, lengthFloats);
            LogDiagnostic($"WAV instancia={_instanceId} gravado em {path} ({lengthFloats / AudioChannels} amostras/canal, ~{lengthFloats / AudioChannels / AudioSampleRate}s)");
        }
        catch (Exception)
        {
            // Falha honesta — diagnostico nunca deve afetar a reproducao real.
        }
    }

    /// <summary>
    /// Sprint NDI-audio-buffer-diag (2026-09-12) — log de diagnostico TEMPORARIO (mesmo espirito do
    /// antigo <c>AudioDiagnosticLog</c> da Sprint NDI-audio, ja removido, mas com uma diferenca
    /// importante: aquele gravava de forma SINCRONA A CADA BLOCO de audio (custo aceito na epoca pra
    /// caçar um crash) — este so grava nos eventos RAROS (fila cheia de verdade, ou profundidade
    /// acima do normal), nunca no caminho normal de cada bloco, entao nunca vira um custo por bloco
    /// como o antigo. Path: <c>%AppData%\CartucheiraVideo\ndi-audio-buffer-diagnostico.log</c> — uma
    /// linha por evento, arquivo compartilhado entre os dois Decks (identificados por
    /// <see cref="_instanceId"/>) pra facilitar comparar a ordem no tempo. Falha honesta: qualquer
    /// erro de E/S (disco cheio, pasta sem permissao, etc.) e engolido — um log de diagnostico nunca
    /// pode derrubar a reproducao de audio de verdade.
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
            // Falha honesta — diagnostico nunca deve afetar a reproducao real.
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

        // Sprint NDI-audio-video-clock-lock-diag (2026-09-13) — TEMPORARIO: se o app fechar antes do
        // buffer encher (menos de DiagnosticWavDurationSeconds de audio tocado no total), grava o que
        // foi capturado ate aqui em vez de perder o teste inteiro. Seguro chamar aqui: Clear() acima
        // ja chamou StopInternal(), que ja deu Join() em _sendThread — a UNICA thread que escreve em
        // _diagnosticWavBuffer ja parou de vez, sem nenhuma corrida possivel com a leitura abaixo.
        FinalizeDiagnosticWav();
    }
}
