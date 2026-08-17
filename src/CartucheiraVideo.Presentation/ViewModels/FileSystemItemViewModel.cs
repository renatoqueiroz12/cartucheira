using System.IO;
using System.Linq;
using CartucheiraVideo.Core;

namespace CartucheiraVideo.Presentation.ViewModels;

/// <summary>
/// Representa um <see cref="MediaItem"/> listado no painel de Explorer (arquitetura §12: o Core
/// so conhece MediaItem — esta classe e a "visao de lista" dele, vive na Presentation).
///
/// Nome, tamanho e tipo vêm direto do sistema de arquivos (reais). Duração/resolução/codec ainda
/// não são lidos aqui — exigem abrir o arquivo com LibVLC, o que entra numa sprint futura (Engine,
/// ver arquitetura §10/§11) — aparecem como "—" honesto até lá, nunca um valor inventado.
/// <see cref="MetaSummary"/> é o resumo de uma linha (padrão "nome + metadados" da v6/v7 — colunas
/// de tabela não cabiam mais lado a lado num Explorer mais largo), na ordem definida pela
/// especificação visual (Sprint 7): Duração • Resolução • Codec • Tamanho • Tipo.
/// </summary>
public sealed class FileSystemItemViewModel
{
    private static readonly IReadOnlyDictionary<string, MediaType> ExtensionToMediaType = new Dictionary<string, MediaType>(StringComparer.OrdinalIgnoreCase)
    {
        [".mp4"] = MediaType.Video,
        [".mov"] = MediaType.Video,
        [".mkv"] = MediaType.Video,
        [".avi"] = MediaType.Video,
        [".wmv"] = MediaType.Video,
    };

    public FileSystemItemViewModel(string fullPath)
    {
        var extension = Path.GetExtension(fullPath);

        Media = new MediaItem
        {
            Name = Path.GetFileName(fullPath),
            Path = fullPath,
            MediaType = ExtensionToMediaType.TryGetValue(extension, out var type) ? type : MediaType.Unknown,
            FileSize = TryGetFileSize(fullPath),
            Created = TryGetCreated(fullPath),
            LastModified = TryGetLastModified(fullPath),
        };

        Extension = extension.TrimStart('.').ToUpperInvariant();
        SizeDisplay = FormatSize(Media.FileSize);
        DurationDisplay = Media.Duration?.ToString(@"mm\:ss") ?? "—";
        ResolutionDisplay = Media.Resolution ?? "—";
        CodecDisplay = Media.Codec ?? "—";

        MetaSummary = string.Join("  •  ", new[] { DurationDisplay, ResolutionDisplay, CodecDisplay, SizeDisplay, Extension }
            .Where(s => !string.IsNullOrWhiteSpace(s)));
    }

    /// <summary>O objeto de dominio que este item representa.</summary>
    public MediaItem Media { get; }

    public string FullPath => Media.Path;

    public string FileName => Media.Name;

    public string Extension { get; }

    public string SizeDisplay { get; }

    /// <summary>Formatada como mm:ss quando o Engine já leu a duração — "—" honesto até lá.</summary>
    public string DurationDisplay { get; }

    /// <summary>"—" honesto até o Engine ler a resolução real do arquivo.</summary>
    public string ResolutionDisplay { get; }

    /// <summary>"—" honesto até o Engine ler o codec real do arquivo.</summary>
    public string CodecDisplay { get; }

    /// <summary>
    /// Resumo de uma linha (padrao "nome + metadados" da v6/v7 — colunas de tabela viraram
    /// subtítulo porque não cabiam mais lado a lado num Explorer mais largo), ordem definida pela
    /// especificação visual (Sprint 7): Duração • Resolução • Codec • Tamanho • Tipo.
    /// </summary>
    public string MetaSummary { get; }

    private static long? TryGetFileSize(string fullPath)
    {
        try
        {
            return new FileInfo(fullPath).Length;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static DateTime? TryGetCreated(string fullPath)
    {
        try
        {
            return new FileInfo(fullPath).CreationTimeUtc;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static DateTime? TryGetLastModified(string fullPath)
    {
        try
        {
            return new FileInfo(fullPath).LastWriteTimeUtc;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static string FormatSize(long? bytes)
    {
        if (bytes is null)
        {
            return "—";
        }

        string[] units = { "B", "KB", "MB", "GB" };
        double size = bytes.Value;
        var unitIndex = 0;

        while (size >= 1024 && unitIndex < units.Length - 1)
        {
            size /= 1024;
            unitIndex++;
        }

        return $"{size:0.#} {units[unitIndex]}";
    }
}
