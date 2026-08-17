using System.Runtime.InteropServices;

namespace CartucheiraVideo.Ndi;

/// <summary>
/// Structs/enums/DllImport da NDI SDK 6 (Processing.NDI.Lib.x64.dll), so o subconjunto que este
/// projeto usa. Sinaturas verificadas em 2026-08-09 (video), 2026-08-11 (audio, Sprint NDI-audio) e
/// 2026-08-11 mesmo dia (correcao pos-entrega: audio trocado de intercalado/utilitario pra
/// planar/nativo, ver <see cref="NDIlib_audio_frame_v2_t"/>) por pesquisa direta contra copias
/// espelhadas dos headers oficiais (Processing.NDI.structs.h, Processing.NDI.Send.h,
/// Processing.NDI.Lib.h) E, na correcao, contra um wrapper C#/.NET real de producao
/// (<c>vvvv/VL.IO.NDI</c>) — nao de memoria — por serem o ponto de maior risco do projeto (erro de
/// layout/marshaling nao aparece em tempo de compilacao, so como crash ou imagem/audio corrompido).
///
/// Tudo aqui e <c>internal</c> de proposito: o unico ponto de contato publico deste projeto com o
/// resto da solucao e <see cref="NdiSender"/> (e <see cref="NdiRuntime"/> pro composition root) —
/// ninguem fora deste projeto deveria montar um NDIlib_video_frame_v2_t/NDIlib_audio_frame_v2_t na
/// mao.
/// </summary>
internal static class NdiInterop
{
    /// <summary>Nome da DLL nativa no Windows 64-bit (unico SO alvo desta solucao).</summary>
    internal const string DllName = "Processing.NDI.Lib.x64.dll";
}

/// <summary>FourCC dos formatos de video suportados pelo envio — so BGRA e usado por este projeto (decisao da Sprint NDI: formato fixo 1920x1080 BGRA, sem negociacao dinamica de formato).</summary>
internal enum NDIlib_FourCC_video_type_e
{
    /// <summary>NDI_LIB_FOURCC('B','G','R','A') calculado (little-endian: 'B' | 'G'&lt;&lt;8 | 'R'&lt;&lt;16 | 'A'&lt;&lt;24) = 0x41524742.</summary>
    NDIlib_FourCC_video_type_BGRA = 0x41524742
}

/// <summary>Formato de quadro (entrelaçado/progressivo) — este projeto so envia progressivo (video de arquivo, sem entrelaçamento).</summary>
internal enum NDIlib_frame_format_type_e
{
    NDIlib_frame_format_type_progressive = 1
}

/// <summary>
/// Parametros de criacao de um emissor NDI. Layout sequencial exato do struct C
/// <c>NDIlib_send_create_t</c>: <c>clock_video</c>/<c>clock_audio</c> sao <c>bool</c> de 1 byte em
/// C++ — <see cref="MarshalAsAttribute"/> com <see cref="UnmanagedType.I1"/> e obrigatorio em cada
/// um, senao o marshaling padrao (BOOL de 4 bytes) desalinha o struct inteiro.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct NDIlib_send_create_t
{
    /// <summary>Nome do canal NDI (UTF-8, terminado em nulo) — marshalado manualmente via Marshal.StringToCoTaskMemUTF8 em NdiSender, nao via [MarshalAs(UnmanagedType.LPStr)] (esse e ANSI, quebraria nomes com acento).</summary>
    public IntPtr p_ndi_name;

    /// <summary>Grupos NDI (opcional) — sempre IntPtr.Zero nesta sprint (sem suporte a grupos ainda).</summary>
    public IntPtr p_groups;

    [MarshalAs(UnmanagedType.I1)]
    public bool clock_video;

    [MarshalAs(UnmanagedType.I1)]
    public bool clock_audio;
}

/// <summary>
/// Um quadro de video a enviar. Layout sequencial exato do struct C <c>NDIlib_video_frame_v2_t</c>
/// (ordem dos campos confirmada contra o header oficial em 2026-08-09) — <c>line_stride_in_bytes</c>
/// ocupa o mesmo offset que a union C <c>{ line_stride_in_bytes; data_size_in_bytes; }</c>; como
/// este projeto so envia video nao-comprimido (BGRA), so o campo de stride e usado.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct NDIlib_video_frame_v2_t
{
    public int xres;
    public int yres;
    public NDIlib_FourCC_video_type_e FourCC;
    public int frame_rate_N;
    public int frame_rate_D;
    public float picture_aspect_ratio;
    public NDIlib_frame_format_type_e frame_format_type;
    public long timecode;
    public IntPtr p_data;
    public int line_stride_in_bytes;
    public IntPtr p_metadata;
    public long timestamp;
}

/// <summary>
/// Um bloco de audio PCM float32 PLANAR a enviar — layout sequencial exato do struct C
/// <c>NDIlib_audio_frame_v2_t</c> (Processing.NDI.structs.h, ordem dos campos confirmada contra o
/// header oficial E contra um wrapper C#/.NET real de producao — <c>vvvv/VL.IO.NDI</c>, que usa
/// exatamente esta struct/funcao pra enviar audio, nao a variante utilitaria intercalada).
///
/// Sprint NDI-audio (2026-08-11) — CORRECAO pos-entrega, mesmo dia: a primeira versao desta sprint
/// usava <c>NDIlib_audio_frame_interleaved_32f_t</c> + a funcao utilitaria
/// <c>NDIlib_util_send_send_audio_interleaved_32f</c> (Processing.NDI.utilities.h), que aceitava o
/// buffer intercalado do jeito que o LibVLC ja entrega sem nenhuma desintercalacao manual — em
/// teoria a via mais simples. Na pratica, causou <c>System.AccessViolationException</c> ao vivo (
/// reportado pelo usuario testando no Windows de verdade), confirmado pela pilha de chamadas dentro
/// de <c>NdiSender.SendAudioFrame</c>. Pesquisa pos-crash nao achou NENHUM consumidor C#/.NET real
/// (produtivo, testado) da funcao utilitaria intercalada — inclusive o proprio <c>VL.IO.NDI</c>, que
/// TEM essa funcao portada em seu Pinvoke, usa a funcao "nativa" <c>NDIlib_send_send_audio_v2</c>
/// (PLANAR) no Sender.cs de verdade, nao a utilitaria. Trocado por isso: mais uma prova de que a
/// funcao utilitaria intercalada e pouco testada em bindings gerenciados (P/Invoke), mesmo
/// documentada como export publico da DLL — em vez de continuar arriscando a mesma classe de bug, a
/// desintercalacao agora e feita manualmente aqui em C# (ver <see cref="NdiSender.SendAudioFrame"/>),
/// contra a funcao "nativa" NDIlib_send_send_audio_v2, a mesma que exemplos oficiais/wrappers reais
/// usam ha anos.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct NDIlib_audio_frame_v2_t
{
    public int sample_rate;
    public int no_channels;
    public int no_samples;
    public long timecode;
    public IntPtr p_data;

    /// <summary>Distancia em bytes entre o inicio de um canal e o proximo dentro de <see cref="p_data"/> (layout PLANAR — cada canal e um bloco contiguo de <see cref="no_samples"/> floats). Sempre <c>no_samples * sizeof(float)</c> neste projeto (canais empacotados sem folga).</summary>
    public int channel_stride_in_bytes;

    public IntPtr p_metadata;

    public long timestamp;
}

internal static class NativeMethods
{
    /// <summary>
    /// Construtor estatico explicito (nao <c>beforefieldinit</c>) — garante que
    /// <see cref="NativeLibrary.SetDllImportResolver"/> ja esteja registrado ANTES de qualquer
    /// metodo <c>extern</c> desta classe ser chamado pela primeira vez, sem depender de nenhum outro
    /// codigo lembrar de chamar isso primeiro.
    ///
    /// Motivo (achado depois da primeira entrega desta sprint, quando a fonte NDI simplesmente nao
    /// aparecia no NDI Studio Monitor, sem nenhum erro visivel): o instalador do NDI Runtime/Tools
    /// NAO garante que a pasta da DLL esteja no PATH do sistema — ele so cria a variavel de ambiente
    /// <c>NDI_RUNTIME_DIR_V6</c> apontando pra ela (ex.: "C:\Program Files\NDI\NDI 6 Runtime\v6\").
    /// A propria documentacao oficial da NDI SDK recomenda exatamente essa variavel como estrategia
    /// primaria de localizacao da DLL, nao assumir PATH
    /// (https://docs.ndi.video/all/developing-with-ndi/sdk/dynamic-loading-of-ndi-libraries). Sem
    /// isso, <c>DllImport("Processing.NDI.Lib.x64.dll")</c> simples falha com
    /// <see cref="DllNotFoundException"/> — capturada silenciosamente por
    /// <see cref="NdiRuntime.Initialize"/> (pra nao derrubar o programa inteiro numa maquina sem NDI
    /// instalado), o que explica "rodou sem erro, mas a fonte nao aparece": a falha era real, so
    /// nunca virava uma excecao visivel.
    /// </summary>
    static NativeMethods()
    {
        NativeLibrary.SetDllImportResolver(typeof(NativeMethods).Assembly, ResolveNdiLibrary);
    }

    private static IntPtr ResolveNdiLibrary(string libraryName, System.Reflection.Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (libraryName != NdiInterop.DllName)
        {
            // Nao e a nossa DLL — devolve Zero pra deixar a resolucao padrao do .NET cuidar (mesmo
            // contrato de "nao resolvido por este resolver" usado abaixo).
            return IntPtr.Zero;
        }

        // 1ª tentativa: variavel de ambiente oficial (ver comentario do construtor estatico acima).
        // Precisa de um processo novo pra existir — se o usuario instalou o NDI Runtime com o Visual
        // Studio ja aberto, essa variavel so aparece depois de reiniciar o Visual Studio (ou a
        // maquina); variavel de ambiente de instalador e lida uma vez, na criacao do processo, nao
        // atualiza processos ja rodando.
        var runtimeDir = Environment.GetEnvironmentVariable("NDI_RUNTIME_DIR_V6");
        if (!string.IsNullOrEmpty(runtimeDir) && TryLoadFrom(runtimeDir, out var handleFromEnv))
        {
            return handleFromEnv;
        }

        // 2ª tentativa: caminhos padrao de instalacao conhecidos (NDI Tools e NDI SDK usam a mesma
        // pasta de Runtime) — rede de seguranca pro caso da variavel de ambiente nao estar visivel
        // por qualquer motivo (instalador antigo, processo iniciado antes da instalacao, etc.).
        string[] fallbackDirs =
        [
            @"C:\Program Files\NDI\NDI 6 Runtime\v6",
            @"C:\Program Files\NDI\NDI 6 SDK\Bin\x64"
        ];

        foreach (var dir in fallbackDirs)
        {
            if (TryLoadFrom(dir, out var handleFromFallback))
            {
                return handleFromFallback;
            }
        }

        // Zero aqui significa "nao resolvi, tenta do jeito padrao" (PATH, pasta do .exe, System32)
        // — cobre o caso de alguem copiar a DLL manualmente pra dentro da pasta de saida do build.
        return IntPtr.Zero;
    }

    private static bool TryLoadFrom(string directory, out IntPtr handle)
    {
        var fullPath = Path.Combine(directory, NdiInterop.DllName);
        if (File.Exists(fullPath) && NativeLibrary.TryLoad(fullPath, out handle))
        {
            return true;
        }

        handle = IntPtr.Zero;
        return false;
    }

    /// <summary>
    /// Precisa ser chamado uma vez por processo antes de qualquer outra funcao NDIlib — feito pelo
    /// composition root (App.xaml.cs) via <see cref="NdiRuntime.Initialize"/>, nao por NdiSender
    /// (que e por-Deck, criado/destruido varias vezes ao longo da vida do processo).
    /// </summary>
    [DllImport(NdiInterop.DllName, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static extern bool NDIlib_initialize();

    [DllImport(NdiInterop.DllName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void NDIlib_destroy();

    [DllImport(NdiInterop.DllName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr NDIlib_send_create(ref NDIlib_send_create_t p_create_settings);

    [DllImport(NdiInterop.DllName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void NDIlib_send_destroy(IntPtr p_instance);

    /// <summary>
    /// Variante NAO-async de proposito (decisao da Sprint NDI): copia o buffer internamente antes
    /// de retornar, entao e seguro reusar um unico buffer fixo logo em seguida — a variante _async
    /// exigiria sincronizar antes de tocar no buffer de novo, complexidade desnecessaria pro MVP.
    /// </summary>
    [DllImport(NdiInterop.DllName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void NDIlib_send_send_video_v2(IntPtr p_instance, ref NDIlib_video_frame_v2_t p_video_data);

    /// <summary>
    /// Sprint NDI-audio (2026-08-11), CORRECAO pos-entrega mesmo dia: funcao "nativa" de envio de
    /// audio da NDI SDK (Processing.NDI.Send.h), a mesma que exemplos oficiais e wrappers C#/.NET
    /// reais de producao usam (confirmado contra <c>vvvv/VL.IO.NDI</c>) — substitui a funcao
    /// utilitaria intercalada (<c>NDIlib_util_send_send_audio_interleaved_32f</c>), que causou
    /// <see cref="System.AccessViolationException"/> ao vivo (ver comentario completo em
    /// <see cref="NDIlib_audio_frame_v2_t"/>). Espera o buffer PLANAR (<see cref="NdiSender.SendAudioFrame"/>
    /// desintercala manualmente antes de chamar isto) — mesmo racional de seguranca de buffer da
    /// variante de video acima: nao tem sufixo <c>_async</c>, entao copia/consome o buffer
    /// internamente antes de retornar.
    /// </summary>
    [DllImport(NdiInterop.DllName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void NDIlib_send_send_audio_v2(IntPtr p_instance, ref NDIlib_audio_frame_v2_t p_audio_data);
}
