using System.Globalization;
using System.Text;
using System.Text.Json;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using Vosk;

namespace CartucheiraVideo.Engine;

/// <summary>
/// Acompanhamento automático do Teleprompter por voz — Sprint Teleprompter (2026-11-16). Pedido do
/// usuário: "vai ter uma entrada de áudio [...] uma detecção de voz [...] e ele vai interpretando a
/// voz e aí ele vai andando". Pesquisa feita antes de implementar (ver nota da conversa/doc do
/// projeto): nenhum teleprompter de voz comercial (PromptSmart, a "Voice Scrolling" da própria
/// teleprompter.com) faz uma transcrição perfeita — todos funcionam do mesmo jeito, reaproveitado
/// aqui: reconhecimento de voz contínuo, e as palavras reconhecidas são comparadas contra o SCRIPT
/// JÁ CONHECIDO (não contra um dicionário livre) só pra achar EM QUE PONTO do texto o locutor está
/// agora — nunca tenta "entender" o que foi dito, só achar a posição. Quando a fala sai do script
/// (pausa, tosse, ad-lib) a posição simplesmente para de avançar até ouvir palavras que batam de
/// novo — mesmo comportamento documentado dos produtos comerciais pesquisados.
///
/// Decisão confirmada com o usuário: o controle manual (seta/roda do mouse na janela de saída, ver
/// <c>Presentation.Views.TeleprompterOutputWindow</c>) fica SEMPRE disponível por cima disto — esta
/// classe nunca é a única forma de mexer no texto, só mais uma fonte de "avançar sozinho".
///
/// Motor local (<b>Vosk</b>, pacote NuGet <c>Vosk</c>) — mesmo princípio de sempre deste projeto de
/// rodar tudo localmente, sem depender de nuvem/internet (NDI, Livewire, vMix são todos locais/LAN).
/// Precisa de um MODELO de voz baixado à parte (não vem no pacote NuGet, são arquivos binários de
/// ~30-50MB) — ver <see cref="DefaultModelFolderName"/> e o LEIA-ME desta entrega pro link de
/// download do modelo pt-BR. Sem o modelo presente, <see cref="Start"/> devolve um erro honesto
/// (nunca lança, nunca finge que está funcionando) e o Teleprompter continua 100% utilizável só no
/// manual.
///
/// Qualidade esperada, documentada honestamente (mesma pesquisa citada acima): reconhecimento de
/// voz em português, mesmo offline, erra palavras de vez em quando — isso é normal, não um bug
/// desta implementação. O algoritmo de casamento (<see cref="TryAdvance"/>) é tolerante a isso
/// (procura numa JANELA de palavras à frente, não exige acerto de 100%), mas frases parecidas perto
/// uma da outra no script podem fazer a posição pular adiantado — mesma limitação documentada do
/// PromptSmart.
/// </summary>
public sealed class TeleprompterVoiceEngine : IDisposable
{
    /// <summary>Nome da pasta, ao lado do .exe, onde o modelo Vosk pt-BR precisa estar extraído (ex.: <c>vosk-model-small-pt-0.3</c> renomeada/copiada pra esta pasta) — mesmo padrão de pasta fixa ao lado do .exe já usado por <c>PlaylistDashboardWebRoot</c>.</summary>
    public const string DefaultModelFolderName = "TeleprompterVoiceModel";

    /// <summary>Vosk espera PCM 16-bit mono nesta taxa — todo áudio capturado é convertido pra isto antes de <see cref="VoskRecognizer.AcceptWaveform(byte[], int)"/>.</summary>
    private const int VoskSampleRate = 16000;

    /// <summary>Quantas palavras à frente da posição atual o casamento procura (ver <see cref="TryAdvance"/>) — tolera 1-2 palavras erradas/puladas pelo reconhecimento sem perder o lugar, mas não deixa o texto "pular" páginas inteiras por um falso positivo distante.</summary>
    private const int MatchWindowWords = 18;

    /// <summary>Sem nenhum casamento novo por este tempo (e com áudio chegando) — status vira "Aguardando fala no script..." em vez de continuar dizendo "Ouvindo" indefinidamente.</summary>
    private static readonly TimeSpan NoMatchWarningDelay = TimeSpan.FromSeconds(4);

    private readonly object _gate = new();
    private readonly string _modelPath;

    private Model? _model;
    private VoskRecognizer? _recognizer;
    private WasapiCapture? _capture;
    private MMDevice? _device;
    private LinearMonoResampler? _resampler;

    private List<ScriptWord> _scriptWords = new();
    private int _currentIndex;
    private string _lastPartialSeen = string.Empty;
    private DateTime _lastMatchUtc = DateTime.UtcNow;
    private bool _running;
    private bool _waiting;
    private bool _warnedWaiting;

    /// <param name="modelPath">Caminho explícito pro modelo Vosk — se omitido, usa <see cref="DefaultModelFolderName"/> ao lado do .exe (<see cref="AppContext.BaseDirectory"/>). Parâmetro existe só pra permitir apontar pra outro lugar em diagnóstico/teste, mesmo papel do construtor alternativo de <c>Configuration.SettingsStore</c>.</param>
    public TeleprompterVoiceEngine(string? modelPath = null)
    {
        _modelPath = modelPath ?? Path.Combine(AppContext.BaseDirectory, DefaultModelFolderName);
    }

    /// <summary>Disparado quando o casamento de voz avança a posição no script — <c>CharOffset</c> é o deslocamento (em caracteres) dentro do texto passado a <see cref="Start"/>/<see cref="UpdateScript"/> de onde a leitura está agora; quem ouve (<c>Presentation.ViewModels.TeleprompterViewModel</c>) converte isso numa posição de rolagem na janela de saída. Nunca disparado na UI thread — vem da thread de captura do NAudio.</summary>
    public event EventHandler<int>? ScriptPositionChanged;

    /// <summary>Texto curto de status pra mostrar ao operador (ex.: "Ouvindo", "Aguardando fala no script...", "Microfone não encontrado", "Modelo de voz não encontrado em ...") — nunca lança, só informa.</summary>
    public event EventHandler<string>? StatusChanged;

    /// <summary>
    /// Carrega o modelo (só na primeira chamada — reaproveitado entre Start/Stop seguintes, pesa
    /// ~50MB em memória e demora alguns segundos pra abrir) e começa a escutar o microfone
    /// escolhido. Nunca lança: qualquer falha (modelo ausente/corrompido, microfone não encontrado
    /// ou ocupado por outro programa em modo exclusivo) devolve <c>Ok = false</c> com
    /// <c>Error</c> honesto — o Teleprompter continua funcionando normalmente no manual.
    /// </summary>
    public (bool Ok, string? Error) Start(string? audioDeviceId, string scriptText)
    {
        lock (_gate)
        {
            StopNoLock();

            if (_model is null)
            {
                if (!Directory.Exists(_modelPath))
                {
                    var error = $"Modelo de voz não encontrado em \"{_modelPath}\" — baixe o modelo Vosk pt-BR e extraia nessa pasta (ver LEIA-ME desta entrega). O Teleprompter continua funcionando no manual.";
                    StatusChanged?.Invoke(this, error);
                    return (false, error);
                }

                try
                {
                    _model = new Model(_modelPath);
                }
                catch (Exception ex)
                {
                    var error = $"Não consegui carregar o modelo de voz em \"{_modelPath}\" ({ex.Message}). O Teleprompter continua funcionando no manual.";
                    StatusChanged?.Invoke(this, error);
                    return (false, error);
                }
            }

            MMDevice device;
            try
            {
                using var enumerator = new MMDeviceEnumerator();
                device = string.IsNullOrEmpty(audioDeviceId)
                    ? enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications)
                    : enumerator.GetDevice(audioDeviceId);
            }
            catch (Exception ex)
            {
                var error = $"Microfone configurado não encontrado ({ex.Message}). O Teleprompter continua funcionando no manual.";
                StatusChanged?.Invoke(this, error);
                return (false, error);
            }

            WasapiCapture capture;
            try
            {
                capture = new WasapiCapture(device);
            }
            catch (Exception ex)
            {
                device.Dispose();
                var error = $"Não consegui abrir o microfone \"{device.FriendlyName}\" ({ex.Message}) — pode estar em uso exclusivo por outro programa. O Teleprompter continua funcionando no manual.";
                StatusChanged?.Invoke(this, error);
                return (false, error);
            }

            SetScriptNoLock(scriptText);

            // Sem SetWords/SetPartialWords: só os campos "text"/"partial" (texto corrido) são
            // usados pelo casamento (ver HandleRecognizerText) — não precisamos da lista de
            // palavras com timestamp que esses dois ligariam.
            _recognizer = new VoskRecognizer(_model, VoskSampleRate);
            _resampler = new LinearMonoResampler(capture.WaveFormat.SampleRate, VoskSampleRate);
            _device = device;
            _capture = capture;
            _lastMatchUtc = DateTime.UtcNow;

            capture.DataAvailable += OnDataAvailable;
            capture.RecordingStopped += OnRecordingStopped;

            try
            {
                capture.StartRecording();
            }
            catch (Exception ex)
            {
                var error = $"Não consegui iniciar a captura do microfone \"{device.FriendlyName}\" ({ex.Message}). O Teleprompter continua funcionando no manual.";
                DisposeCaptureNoLock();
                StatusChanged?.Invoke(this, error);
                return (false, error);
            }

            _running = true;
            StatusChanged?.Invoke(this, "Ouvindo");
            return (true, null);
        }
    }

    /// <summary>Para de escutar o microfone — o modelo de voz (carregamento pesado) NÃO é descartado, só a captura, pra um próximo <see cref="Start"/> ser rápido. Seguro chamar mesmo já parado (idempotente).</summary>
    public void Stop()
    {
        lock (_gate)
        {
            StopNoLock();
        }
    }

    /// <summary>
    /// Troca o script acompanhado (nova Lauda carregada no Deck fonte do Teleprompter) e reseta a
    /// posição pro início — chamado pelo <c>TeleprompterViewModel</c> toda vez que o texto muda,
    /// enquanto a voz pode continuar escutando o tempo todo (não precisa parar/religar o
    /// microfone/modelo a cada troca de Lauda).
    /// </summary>
    public void UpdateScript(string scriptText)
    {
        lock (_gate)
        {
            SetScriptNoLock(scriptText);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            StopNoLock();
            _recognizer?.Dispose();
            _recognizer = null;
            _model?.Dispose();
            _model = null;
        }
    }

    private void SetScriptNoLock(string scriptText)
    {
        _scriptWords = Tokenize(scriptText ?? string.Empty);
        _currentIndex = 0;
        _lastPartialSeen = string.Empty;
        _lastMatchUtc = DateTime.UtcNow;
        _waiting = false;
        _warnedWaiting = false;
    }

    private void StopNoLock()
    {
        _running = false;
        DisposeCaptureNoLock();
    }

    private void DisposeCaptureNoLock()
    {
        if (_capture is not null)
        {
            _capture.DataAvailable -= OnDataAvailable;
            _capture.RecordingStopped -= OnRecordingStopped;
            try
            {
                _capture.StopRecording();
            }
            catch (Exception)
            {
                // Mesma tolerância honesta de sempre — parar uma captura já em erro/desconectada
                // não pode derrubar o resto do programa.
            }

            _capture.Dispose();
            _capture = null;
        }

        _device?.Dispose();
        _device = null;
        _resampler = null;
    }

    /// <summary>Thread de captura do NAudio — nunca a UI thread. Converte o bloco recebido (formato nativo do dispositivo, tipicamente float estéreo em WASAPI compartilhado) pra PCM 16-bit mono 16kHz e alimenta o Vosk.</summary>
    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        VoskRecognizer? recognizer;
        LinearMonoResampler? resampler;
        WaveFormat? sourceFormat;

        lock (_gate)
        {
            if (!_running || _recognizer is null || _resampler is null || _capture is null)
            {
                return;
            }

            recognizer = _recognizer;
            resampler = _resampler;
            sourceFormat = _capture.WaveFormat;
        }

        float[] mono;
        try
        {
            mono = ToMonoFloats(e.Buffer, e.BytesRecorded, sourceFormat!);
        }
        catch (Exception)
        {
            // Bloco com formato inesperado (ex.: bits por amostra não suportado) — descarta este
            // bloco só, nunca derruba a thread de captura por um bloco ruim isolado.
            return;
        }

        var resampled = resampler!.Process(mono);
        if (resampled.Length == 0)
        {
            return;
        }

        var pcm = new byte[resampled.Length * 2];
        for (var i = 0; i < resampled.Length; i++)
        {
            var sample = Math.Clamp(resampled[i], -1f, 1f);
            var value = (short)(sample * short.MaxValue);
            pcm[i * 2] = (byte)(value & 0xFF);
            pcm[i * 2 + 1] = (byte)((value >> 8) & 0xFF);
        }

        bool finalReady;
        try
        {
            finalReady = recognizer!.AcceptWaveform(pcm, pcm.Length);
        }
        catch (Exception)
        {
            // Mesma tolerância — um bloco que o Vosk não consiga processar não pode matar a
            // thread de captura; o próximo bloco segue normalmente.
            return;
        }

        if (finalReady)
        {
            HandleRecognizerText(recognizer.Result(), isFinal: true);
        }
        else
        {
            HandleRecognizerText(recognizer.PartialResult(), isFinal: false);
        }
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception is not null)
        {
            StatusChanged?.Invoke(this, $"Microfone desconectado ou com erro ({e.Exception.Message}) — Teleprompter continua funcionando no manual.");
        }
    }

    /// <summary>Extrai o campo "partial" ou "text" do JSON devolvido pelo Vosk, isola só as palavras NOVAS desde a última checagem (partial é cumulativo dentro da mesma frase) e tenta avançar o casamento com elas.</summary>
    private void HandleRecognizerText(string json, bool isFinal)
    {
        string text;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var propertyName = isFinal ? "text" : "partial";
            text = doc.RootElement.TryGetProperty(propertyName, out var value) ? value.GetString() ?? string.Empty : string.Empty;
        }
        catch (JsonException)
        {
            return;
        }

        lock (_gate)
        {
            string newPortion;
            if (isFinal)
            {
                // Resultado final fecha a frase inteira — usa o texto completo dela (não só o que
                // sobrou depois do último parcial, que pode já ter sido reconhecido diferente na
                // versão final) e reseta o rastreio de parcial pra próxima frase.
                newPortion = text;
                _lastPartialSeen = string.Empty;
            }
            else
            {
                newPortion = text.Length > _lastPartialSeen.Length && text.StartsWith(_lastPartialSeen, StringComparison.Ordinal)
                    ? text[_lastPartialSeen.Length..]
                    : text;
                _lastPartialSeen = text;
            }

            if (string.IsNullOrWhiteSpace(newPortion))
            {
                return;
            }

            TryAdvance(Tokenize(newPortion).Select(w => w.Normalized));
        }
    }

    /// <summary>
    /// Chamado só de dentro de <see cref="_gate"/>. Pra cada palavra reconhecida (já normalizada),
    /// procura o primeiro casamento dentro de <see cref="MatchWindowWords"/> palavras à frente da
    /// posição atual no script — se achar, avança a posição pra logo depois da palavra casada.
    /// Nenhum casamento em nenhuma das palavras desta rodada = posição não muda (locutor pausou ou
    /// saiu do script; mesmo comportamento documentado dos produtos comerciais pesquisados) — nunca
    /// "inventa" um avanço sem achar a palavra de verdade no texto.
    /// </summary>
    private void TryAdvance(IEnumerable<string> recognizedWords)
    {
        if (_scriptWords.Count == 0)
        {
            return;
        }

        var advanced = false;

        foreach (var word in recognizedWords)
        {
            if (word.Length == 0)
            {
                continue;
            }

            var windowEnd = Math.Min(_scriptWords.Count, _currentIndex + MatchWindowWords);
            for (var i = _currentIndex; i < windowEnd; i++)
            {
                if (_scriptWords[i].Normalized != word)
                {
                    continue;
                }

                _currentIndex = i + 1;
                advanced = true;
                break;
            }
        }

        if (!advanced)
        {
            // Avisa só UMA vez ao entrar no estado de espera (não a cada bloco de áudio sem
            // casamento, que chegaria a vários por segundo) — <see cref="_warnedWaiting"/> evita
            // inundar a barra de status do programa com a mesma mensagem repetida.
            if (!_warnedWaiting && DateTime.UtcNow - _lastMatchUtc > NoMatchWarningDelay)
            {
                _warnedWaiting = true;
                _waiting = true;
                StatusChanged?.Invoke(this, "Aguardando fala que bata com o script...");
            }

            return;
        }

        _lastMatchUtc = DateTime.UtcNow;

        // Mesma ideia do aviso acima, espelhada: só avisa "Acompanhando" na TRANSIÇÃO de volta
        // (saindo de espera), nunca a cada palavra casada — isso faria a barra de status piscar
        // a cada ~1s inteiros durante uma leitura normal.
        if (_waiting)
        {
            _waiting = false;
            _warnedWaiting = false;
            StatusChanged?.Invoke(this, "Acompanhando");
        }

        var charOffset = _currentIndex < _scriptWords.Count
            ? _scriptWords[_currentIndex].CharOffset
            : (_scriptWords.Count > 0 ? _scriptWords[^1].CharOffset : 0);

        ScriptPositionChanged?.Invoke(this, charOffset);
    }

    /// <summary>Separa o texto em palavras com seu deslocamento (em caracteres) no texto ORIGINAL (<see cref="ScriptWord.CharOffset"/>, pra <see cref="ScriptPositionChanged"/> apontar pra um lugar de verdade no texto exibido) e uma versão normalizada (minúsculas, sem acento, sem pontuação) só pra comparar contra o que o Vosk reconhece.</summary>
    private static List<ScriptWord> Tokenize(string text)
    {
        var words = new List<ScriptWord>();
        var i = 0;

        while (i < text.Length)
        {
            while (i < text.Length && !char.IsLetterOrDigit(text[i]))
            {
                i++;
            }

            if (i >= text.Length)
            {
                break;
            }

            var start = i;
            while (i < text.Length && char.IsLetterOrDigit(text[i]))
            {
                i++;
            }

            var raw = text[start..i];
            var normalized = Normalize(raw);
            if (normalized.Length > 0)
            {
                words.Add(new ScriptWord(normalized, start));
            }
        }

        return words;
    }

    /// <summary>Minúsculas + remoção de acentos (decomposição Unicode, descarta marcas combinantes) — o modelo pequeno do Vosk em português nem sempre acerta acentuação, então comparar sem acento reduz falso-negativo sem custo real (o texto EXIBIDO na tela nunca passa por esta normalização, só a cópia usada pra comparar).</summary>
    private static string Normalize(string word)
    {
        var decomposed = word.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);

        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
            {
                builder.Append(char.ToLowerInvariant(c));
            }
        }

        return builder.ToString().Normalize(NormalizationForm.FormC);
    }

    /// <summary>Converte um bloco capturado (bytes no formato nativo do dispositivo) pra float mono — soma/tira a média dos canais quando estéreo. Só os dois formatos realmente produzidos pelo WASAPI em modo compartilhado (os únicos que este projeto usa, mesmo princípio de <see cref="AudioEngine"/>) são suportados; qualquer outro lança, capturado por quem chama.</summary>
    private static float[] ToMonoFloats(byte[] buffer, int bytesRecorded, WaveFormat format)
    {
        var channels = Math.Max(1, format.Channels);
        var bytesPerSample = format.BitsPerSample / 8;
        var frameSize = bytesPerSample * channels;
        var frameCount = bytesRecorded / frameSize;
        var result = new float[frameCount];

        for (var frame = 0; frame < frameCount; frame++)
        {
            var frameOffset = frame * frameSize;
            var sum = 0f;

            for (var ch = 0; ch < channels; ch++)
            {
                var sampleOffset = frameOffset + ch * bytesPerSample;
                sum += ReadSample(buffer, sampleOffset, format);
            }

            result[frame] = sum / channels;
        }

        return result;
    }

    private static float ReadSample(byte[] buffer, int offset, WaveFormat format)
    {
        if (format.Encoding == WaveFormatEncoding.IeeeFloat && format.BitsPerSample == 32)
        {
            return BitConverter.ToSingle(buffer, offset);
        }

        if (format.Encoding == WaveFormatEncoding.Pcm && format.BitsPerSample == 16)
        {
            return BitConverter.ToInt16(buffer, offset) / 32768f;
        }

        throw new NotSupportedException($"Formato de captura não suportado ({format.Encoding}, {format.BitsPerSample} bits).");
    }

    private readonly record struct ScriptWord(string Normalized, int CharOffset);

    /// <summary>
    /// Resampler linear simples, mono, com estado entre blocos (a fase fracionária não reseta a
    /// cada <see cref="Process"/> — sem isso, cada bloco começaria a reamostragem do zero e
    /// introduziria um pequeno "clique"/deriva de fase a cada chamada). Não é um resampler de
    /// qualidade de estúdio (sem filtro anti-aliasing) — pra reconhecimento de voz (não pra
    /// gravação/transmissão) isso é aceitável, mesmo tipo de contrapartida já aceita em
    /// <see cref="AudioEngine"/> pro <c>WdlResamplingSampleProvider</c> (ali com filtro, aqui sem,
    /// porque a entrada já é só pra um reconhecedor de fala, não pro ouvido).
    /// </summary>
    private sealed class LinearMonoResampler
    {
        private readonly double _ratio;
        private double _position;
        private float _previousSample;
        private bool _hasPrevious;

        public LinearMonoResampler(int sourceSampleRate, int targetSampleRate)
        {
            _ratio = (double)sourceSampleRate / targetSampleRate;
        }

        public float[] Process(float[] input)
        {
            if (input.Length == 0)
            {
                return Array.Empty<float>();
            }

            // "extended" sempre começa com a ÚLTIMA amostra do bloco anterior (índice 0), pra
            // interpolar corretamente logo no início deste bloco novo sem um salto/clique na
            // emenda — por isso o deslocamento de posição ao fim (_position = pos - (extended.Length - 1))
            // é consistente nos dois ramos: extended[^1] é sempre input[^1], em ambos os casos.
            var extended = _hasPrevious ? Prepend(_previousSample, input) : input;

            var output = new List<float>(input.Length);
            var pos = _position;

            while (pos <= extended.Length - 1)
            {
                var index = (int)pos;
                var frac = pos - index;
                var a = extended[index];
                var b = index + 1 < extended.Length ? extended[index + 1] : a;
                output.Add((float)(a + (b - a) * frac));
                pos += _ratio;
            }

            _position = Math.Max(0.0, pos - (extended.Length - 1));
            _previousSample = input[^1];
            _hasPrevious = true;

            return output.ToArray();
        }

        private static float[] Prepend(float value, float[] rest)
        {
            var result = new float[rest.Length + 1];
            result[0] = value;
            Array.Copy(rest, 0, result, 1, rest.Length);
            return result;
        }
    }
}
