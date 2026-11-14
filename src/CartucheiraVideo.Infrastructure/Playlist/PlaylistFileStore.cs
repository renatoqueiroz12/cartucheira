using System.Text.Json;

namespace CartucheiraVideo.Infrastructure.Playlist;

/// <summary>Um Programa cadastrado, no vocabulário cru desta camada (só strings — quem traduz pro <c>Core.PlaylistProgram</c> é a Presentation).</summary>
public sealed record PlaylistProgramRecord(string Id, string Name, string FolderName);

/// <summary>Uma Edição (Data) de um Programa, no vocabulário cru desta camada.</summary>
public sealed record PlaylistEditionRecord(string Id, string DateIso, string FolderName, string FolderPath);

/// <summary>
/// Leitura/escrita em disco do módulo Playlist — Sprint Playlist-Dashboard (2026-11-14, ver
/// arquitetura/playlist-modulo-roteiro.md). Mesmo princípio de isolamento já usado por todo o
/// projeto entre os 7 projetos fixos (regra em arquitetura-sistema.md §7): este projeto não conhece
/// <c>CartucheiraVideo.Core</c> — fala só em strings/datas cruas (<see cref="PlaylistProgramRecord"/>/
/// <see cref="PlaylistEditionRecord"/>), e é a Presentation (<c>PlaylistDashboardCoordinator</c>) quem
/// traduz isso pros objetos de domínio (<c>Core.PlaylistProgram</c>/<c>PlaylistEdition</c>).
///
/// Estrutura em disco, dentro da pasta raiz configurada em Configurações → Playlist:
/// <list type="bullet">
/// <item><c>{Raiz}\programs.json</c> — índice de todos os Programas (Id/Nome/nome da pasta).</item>
/// <item><c>{Raiz}\{PastaDoPrograma}\editions.json</c> — índice das Edições (Datas) daquele Programa.</item>
/// <item><c>{Raiz}\{PastaDoPrograma}\{PastaDaData}\playlist.json</c> — itens da Playlist daquela
/// Edição (Sprint 1: sempre vazio — nenhum ingest ainda).</item>
/// </list>
///
/// Nomes de pasta são derivados do nome digitado (<see cref="SanitizeFolderName"/>, troca caractere
/// inválido de pasta do Windows por "_") mas a IDENTIDADE real de cada Programa/Edição é sempre o
/// <c>Id</c> (Guid) gravado no índice — uma futura sprint de renomear Programa só precisa atualizar o
/// índice, nunca mover arquivo (mesma regra de "nunca usar string pra identidade" do Core, aplicada
/// aqui do lado de fora dele).
///
/// Nunca lança: toda falha de disco (permissão, caminho inválido, pasta raiz não configurada) vira
/// <c>(false, mensagem)</c> — quem chama decide o que mostrar/logar, mesmo princípio já usado por
/// <c>VMixClient</c>/<c>NdiFirewallGuard</c>.
/// </summary>
public static class PlaylistFileStore
{
    private const string ProgramsIndexFileName = "programs.json";
    private const string EditionsIndexFileName = "editions.json";
    private const string ManifestFileName = "playlist.json";

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static (bool Ok, string? Error, IReadOnlyList<PlaylistProgramRecord> Programs) ListPrograms(string rootFolder)
    {
        if (string.IsNullOrWhiteSpace(rootFolder))
        {
            return (false, "pasta raiz da Playlist não configurada.", Array.Empty<PlaylistProgramRecord>());
        }

        var (ok, error, index) = ReadProgramsIndex(rootFolder);
        return ok
            ? (true, null, index!.Programs.Select(p => new PlaylistProgramRecord(p.Id, p.Name, p.FolderName)).ToList())
            : (false, error, Array.Empty<PlaylistProgramRecord>());
    }

    public static (bool Ok, string? Error, PlaylistProgramRecord? Program) CreateProgram(string rootFolder, string name)
    {
        if (string.IsNullOrWhiteSpace(rootFolder))
        {
            return (false, "pasta raiz da Playlist não configurada.", null);
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            return (false, "nome do Programa não pode ser vazio.", null);
        }

        try
        {
            Directory.CreateDirectory(rootFolder);

            var (ok, error, index) = ReadProgramsIndex(rootFolder);
            if (!ok)
            {
                return (false, error, null);
            }

            var folderName = MakeUniqueFolderName(rootFolder, name.Trim());
            Directory.CreateDirectory(Path.Combine(rootFolder, folderName));

            var entry = new ProgramEntryDto(Guid.NewGuid().ToString(), name.Trim(), folderName);
            index!.Programs.Add(entry);

            var writeError = WriteJson(Path.Combine(rootFolder, ProgramsIndexFileName), index);
            if (writeError is not null)
            {
                return (false, writeError, null);
            }

            return (true, null, new PlaylistProgramRecord(entry.Id, entry.Name, entry.FolderName));
        }
        catch (Exception ex)
        {
            return (false, ex.Message, null);
        }
    }

    public static (bool Ok, string? Error, IReadOnlyList<PlaylistEditionRecord> Editions) ListEditions(string rootFolder, string programId)
    {
        var (programOk, programError, program) = FindProgram(rootFolder, programId);
        if (!programOk || program is null)
        {
            return (false, programError ?? "Programa não encontrado.", Array.Empty<PlaylistEditionRecord>());
        }

        var (ok, error, index) = ReadEditionsIndex(rootFolder, program.FolderName);
        if (!ok)
        {
            return (false, error, Array.Empty<PlaylistEditionRecord>());
        }

        var programFolder = Path.Combine(rootFolder, program.FolderName);
        var editions = index!.Editions
            .Select(e => new PlaylistEditionRecord(e.Id, e.DateIso, e.FolderName, Path.Combine(programFolder, e.FolderName)))
            .ToList();
        return (true, null, editions);
    }

    public static (bool Ok, string? Error, PlaylistEditionRecord? Edition) CreateEdition(string rootFolder, string programId, DateOnly date)
    {
        var (programOk, programError, program) = FindProgram(rootFolder, programId);
        if (!programOk || program is null)
        {
            return (false, programError ?? "Programa não encontrado.", null);
        }

        try
        {
            var programFolder = Path.Combine(rootFolder, program.FolderName);
            Directory.CreateDirectory(programFolder);

            var (ok, error, index) = ReadEditionsIndex(rootFolder, program.FolderName);
            if (!ok)
            {
                return (false, error, null);
            }

            var dateIso = date.ToString("yyyy-MM-dd");
            if (index!.Editions.Any(e => e.DateIso == dateIso))
            {
                return (false, $"já existe uma Playlist cadastrada para {dateIso} neste Programa.", null);
            }

            var folderName = MakeUniqueFolderName(programFolder, dateIso);
            var editionFolder = Path.Combine(programFolder, folderName);
            Directory.CreateDirectory(editionFolder);

            var entry = new EditionEntryDto(Guid.NewGuid().ToString(), dateIso, folderName);
            index.Editions.Add(entry);

            var writeIndexError = WriteJson(Path.Combine(programFolder, EditionsIndexFileName), index);
            if (writeIndexError is not null)
            {
                return (false, writeIndexError, null);
            }

            var manifestError = WriteJson(Path.Combine(editionFolder, ManifestFileName), new PlaylistManifestDto(new List<PlaylistEntryDto>()));
            if (manifestError is not null)
            {
                return (false, manifestError, null);
            }

            return (true, null, new PlaylistEditionRecord(entry.Id, entry.DateIso, entry.FolderName, editionFolder));
        }
        catch (Exception ex)
        {
            return (false, ex.Message, null);
        }
    }

    private static (bool Ok, string? Error, PlaylistProgramRecord? Program) FindProgram(string rootFolder, string programId)
    {
        var (ok, error, programs) = ListPrograms(rootFolder);
        if (!ok)
        {
            return (false, error, null);
        }

        var match = programs.FirstOrDefault(p => string.Equals(p.Id, programId, StringComparison.OrdinalIgnoreCase));
        return match is null ? (false, "Programa não encontrado.", null) : (true, null, match);
    }

    private static (bool Ok, string? Error, ProgramsIndexDto? Index) ReadProgramsIndex(string rootFolder)
    {
        return ReadJson<ProgramsIndexDto>(Path.Combine(rootFolder, ProgramsIndexFileName), new ProgramsIndexDto(new List<ProgramEntryDto>()));
    }

    private static (bool Ok, string? Error, EditionsIndexDto? Index) ReadEditionsIndex(string rootFolder, string programFolderName)
    {
        return ReadJson<EditionsIndexDto>(Path.Combine(rootFolder, programFolderName, EditionsIndexFileName), new EditionsIndexDto(new List<EditionEntryDto>()));
    }

    private static (bool Ok, string? Error, T? Value) ReadJson<T>(string path, T fallbackWhenMissing) where T : class
    {
        try
        {
            if (!File.Exists(path))
            {
                return (true, null, fallbackWhenMissing);
            }

            var json = File.ReadAllText(path);
            var value = JsonSerializer.Deserialize<T>(json, JsonOptions);
            return (true, null, value ?? fallbackWhenMissing);
        }
        catch (Exception ex)
        {
            return (false, $"erro lendo {Path.GetFileName(path)}: {ex.Message}", null);
        }
    }

    private static string? WriteJson<T>(string path, T value)
    {
        try
        {
            var json = JsonSerializer.Serialize(value, JsonOptions);
            File.WriteAllText(path, json);
            return null;
        }
        catch (Exception ex)
        {
            return $"erro gravando {Path.GetFileName(path)}: {ex.Message}";
        }
    }

    /// <summary>Troca qualquer caractere inválido de nome de pasta do Windows (e separador de caminho) por "_", e usa "sem-nome" se o resultado ficar vazio.</summary>
    private static string SanitizeFolderName(string raw)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = raw.Trim().Select(c => invalid.Contains(c) ? '_' : c).ToArray();
        var sanitized = new string(chars).Trim();
        return string.IsNullOrWhiteSpace(sanitized) ? "sem-nome" : sanitized;
    }

    /// <summary>Sanitiza e, se já existir uma pasta com esse nome dentro de <paramref name="parentFolder"/>, acrescenta " (2)", " (3)" etc. até achar um nome livre — nunca sobrescreve uma pasta de outro Programa/Edição por coincidência de nome.</summary>
    private static string MakeUniqueFolderName(string parentFolder, string rawName)
    {
        var baseName = SanitizeFolderName(rawName);
        var candidate = baseName;
        var suffix = 2;
        while (Directory.Exists(Path.Combine(parentFolder, candidate)))
        {
            candidate = $"{baseName} ({suffix})";
            suffix++;
        }

        return candidate;
    }

    private sealed record ProgramEntryDto(string Id, string Name, string FolderName);

    private sealed record ProgramsIndexDto(List<ProgramEntryDto> Programs);

    private sealed record EditionEntryDto(string Id, string DateIso, string FolderName);

    private sealed record EditionsIndexDto(List<EditionEntryDto> Editions);

    /// <summary>Formato do <c>playlist.json</c> dentro de cada pasta de Edição — Sprint 1 só cria o arquivo vazio; Sprint 2 (ingest) é quem passa a preencher <see cref="Entries"/> de verdade.</summary>
    private sealed record PlaylistEntryDto(string Id, int Order, string FileName, string Status, Dictionary<string, string> GcFields);

    private sealed record PlaylistManifestDto(List<PlaylistEntryDto> Entries);
}
