using System;
using System.IO;
using System.Linq;
using System.Text;

namespace CartucheiraVideo.Ndi;

/// <summary>
/// Sprint NDI-audio, QUARTA tentativa (2026-08-11) — log de emergencia pro pipeline de audio NDI,
/// que ate aqui travou o programa tres vezes ao vivo (ver historico completo em
/// <see cref="NdiSender"/>) sem deixar nenhum rastro managed aproveitavel:
/// <see cref="System.ExecutionEngineException"/> nao tem StackTrace/Source/InnerException por design
/// e nao e interceptavel por try/catch no .NET Core (excecao de estado corrompido do CLR). Este log
/// existe pra dar um rastro real em DISCO, gravado de forma SINCRONA (<see cref="FileStream.Flush(bool)"/>
/// com <c>true</c>, forcando o SO a persistir antes de continuar) a cada bloco de audio processado —
/// mesmo que o processo trave sem nenhuma excecao gerenciada, o que ja estiver escrito no arquivo
/// sobrevive.
///
/// Formato ANTES/DEPOIS de proposito: <see cref="LogBeforeSend"/> escreve uma linha logo ANTES de
/// chamar a funcao nativa da NDI SDK, <see cref="LogAfterSend"/> escreve outra logo DEPOIS que ela
/// retorna. Se o processo travar de novo, comparando o indice de chamada (idx) das duas linhas dá
/// pra saber ONDE: se ANTES.idx for maior que DEPOIS.idx, a chamada nativa
/// (NDIlib_send_send_audio_v2) e a suspeita principal — travou dentro dela, nunca chegou a retornar;
/// se os dois baterem, o problema aconteceu DEPOIS do retorno desta chamada especifica (no proximo
/// bloco, ou em outro lugar do pipeline de audio/video).
///
/// Duas linhas de tamanho FIXO (preenchidas com espaco), sempre reescritas no mesmo offset dentro do
/// arquivo — nunca cresce, nunca precisa de truncamento, seguro abrir com qualquer editor de texto
/// (mesmo com o processo ainda rodando, <see cref="FileShare.ReadWrite"/>) pra inspecionar depois de
/// um crash. Um arquivo por Deck (nomeado a partir do canal NDI), pra nao misturar as chamadas de
/// dois Decks tocando ao mesmo tempo no mesmo arquivo.
///
/// Custo aceito conscientemente: dois <c>Flush(true)</c> sincronos por bloco de audio (dezenas de
/// vezes por segundo, ex.: ~47/s pra blocos de 1024 amostras a 48kHz) — normalmente sub-milissegundo
/// em disco local/SSD, mas e trabalho real na thread de audio do LibVLC que nao existia antes deste
/// diagnostico. Trade-off deliberado, aprovado explicitamente pelo usuario nesta sprint como preco
/// de finalmente ter um dado real caso esta quarta tentativa tambem falhe ao vivo — nao e pra ficar
/// ligado pra sempre: remover (ou tornar opcional) assim que a causa raiz for confirmada por um caso
/// real (com este log ou com o proximo crash, se houver).
/// </summary>
internal sealed class AudioDiagnosticLog : IDisposable
{
    // Aumentado de 220 pra 400 (2026-08-11, mesmo dia — segunda rodada de diagnostico): valores de
    // "peak"/"first"/"last" quando o buffer contem lixo de memoria podem ser numeros de ate ~39
    // digitos (proximos do maximo representavel por um float, formatados por extenso via F4, sem
    // notacao cientifica) — com os tres campos (peak/first/last) juntos, a linha antiga de 220
    // caracteres estourava e truncava silenciosamente o fim da linha (ver WriteLine). 400 sobra
    // folga confortavel mesmo com os tres no maximo ao mesmo tempo.
    private const int LineWidth = 400;

    private readonly FileStream? _stream;
    private long _callIndex;
    private bool _disposed;

    /// <summary>
    /// Nunca lanca — se a pasta/arquivo nao puder ser criado por qualquer motivo (permissao, disco
    /// cheio), o log so fica desligado (todo metodo publico vira no-op), sem derrubar o pipeline de
    /// audio por causa de uma ferramenta de diagnostico.
    /// </summary>
    public AudioDiagnosticLog(string ndiName)
    {
        try
        {
            var folder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "CartucheiraVideo");
            Directory.CreateDirectory(folder);

            var safeName = SanitizeFileName(ndiName);
            var path = Path.Combine(folder, $"ndi-audio-diagnostico-{safeName}.log");

            _stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite);
        }
        catch (Exception)
        {
            _stream = null;
        }
    }

    /// <summary>
    /// Chamar imediatamente ANTES de <c>NDIlib_send_send_audio_v2</c>. <paramref name="peak"/>
    /// (adicionado 2026-08-11, mesmo dia — ver comentario em <c>NdiSender.SendAudioFrame</c>) e o
    /// pico de amplitude absoluta do bloco INTERCALADO recebido do LibVLC pra esta chamada — perto
    /// de 0.0 em varias linhas seguidas indica que o buffer capturado e silencio/lixo (problema na
    /// CAPTURA), valores tipicos de audio real (ex.: 0.05–0.9) confirmam que o dado que chega aqui
    /// esta correto (problema seria mais adiante, na entrega pro NDI SDK/rede/receptor).
    ///
    /// <paramref name="peakIndex"/>/<paramref name="firstSample"/>/<paramref name="lastSample"/>
    /// (adicionados 2026-08-11, mesmo dia — SEGUNDA rodada de diagnostico, depois do primeiro teste
    /// mostrar peak sempre proximo do maximo de um float, em chamadas bem diferentes: idx=2 E
    /// idx=960): servem pra distinguir duas hipoteses sem precisar de mais um ciclo de teste depois
    /// deste. Se <paramref name="peakIndex"/> estiver sempre perto do FIM do bloco (perto de
    /// totalSamples-1) e <paramref name="firstSample"/> parecer um valor plausivel de audio (perto
    /// de 0.0–1.0) enquanto <paramref name="lastSample"/> nao, aponta pra um estouro de buffer (lendo
    /// alem do fim real dos dados validos — ex.: <c>count</c> do callback do LibVLC interpretado com
    /// a unidade errada). Se o lixo aparecer em QUALQUER posicao (inclusive <paramref name="firstSample"/>,
    /// posicao 0), aponta pra um ponteiro/formato errado desde o inicio, nao um estouro no fim.
    /// </summary>
    public void LogBeforeSend(int sampleRate, int channels, int sampleCount, int totalSamples, int requiredBytes, float peak, int peakIndex, float firstSample, float lastSample)
    {
        if (_stream is null || _disposed)
        {
            return;
        }

        _callIndex++;
        WriteLine(0, $"ANTES  idx={_callIndex} ts={DateTime.UtcNow:O} sampleRate={sampleRate} channels={channels} sampleCount={sampleCount} totalSamples={totalSamples} bytes={requiredBytes} peak={peak:F4} peakIdx={peakIndex} first={firstSample:F4} last={lastSample:F4}");
    }

    /// <summary>Chamar imediatamente DEPOIS que <c>NDIlib_send_send_audio_v2</c> retornar.</summary>
    public void LogAfterSend()
    {
        if (_stream is null || _disposed)
        {
            return;
        }

        WriteLine(1, $"DEPOIS idx={_callIndex} ts={DateTime.UtcNow:O}");
    }

    private void WriteLine(int slot, string text)
    {
        try
        {
            var padded = text.Length >= LineWidth
                ? text[..LineWidth]
                : text.PadRight(LineWidth);
            var bytes = Encoding.UTF8.GetBytes(padded + "\n");

            _stream!.Seek(slot * (LineWidth + 1), SeekOrigin.Begin);
            _stream.Write(bytes, 0, bytes.Length);
            _stream.Flush(true);
        }
        catch (Exception)
        {
            // Mesma filosofia do construtor — falha de escrita no log nunca pode propagar pro
            // chamador (NdiSender.SendAudioFrame, caminho quente de audio).
        }
    }

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = name.Select(c => invalid.Contains(c) ? '_' : c).ToArray();
        return new string(chars);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _stream?.Dispose();
    }
}
