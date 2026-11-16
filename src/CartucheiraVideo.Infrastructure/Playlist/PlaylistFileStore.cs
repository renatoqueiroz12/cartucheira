using System.Text.Json;

namespace CartucheiraVideo.Infrastructure.Playlist;

/// <summary>Um Programa cadastrado, no vocabulário cru desta camada (só strings — quem traduz pro <c>Core.PlaylistProgram</c> é a Presentation).</summary>
public sealed record PlaylistProgramRecord(string Id, string Name, string FolderName);

/// <summary>Uma Edição (Data) de um Programa, no vocabulário cru desta camada.</summary>
public sealed record PlaylistEditionRecord(string Id, string DateIso, string FolderName, string FolderPath);

/// <summary>
/// Uma Lauda do Espelho de uma Edição, no vocabulário cru desta camada (só strings/listas — quem
/// traduz pro <c>Core.PlaylistLauda</c> é a Presentation) — Sprint Playlist-Dashboard, Sprint 2
/// (2026-11-15). <see cref="Status"/> é o nome cru gravado no disco (ex.: "PendingIngest"/"Ready"),
/// casando com <c>Core.PlaylistLaudaStatus</c>.
/// </summary>
public sealed record PlaylistLaudaRecord(
    string Id,
    int Order,
    string Title,
    string Type,
    string FileName,
    Dictionary<string, string> GcFields,
    string? TpText,
    string? OffText,
    IReadOnlyList<string> ImageFileNames,
    string? DurationPlanned,
    string? Duration,
    string Status,
    string? Notes);

/// <summary>Qual dos dois tipos de arquivo que uma Lauda aceita está sendo enviado — Sprint 2 (2026-11-15).</summary>
public enum PlaylistUploadKind
{
    Video,
    Image
}

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

    /// <summary>Valores crus de <c>Status</c> gravados no disco — precisam casar com os nomes de <c>Core.PlaylistLaudaStatus</c> (esta camada não referencia o enum do Core de propósito, ver comentário de classe).</summary>
    private const string PendingIngestStatus = "PendingIngest";
    private const string ReadyStatus = "Ready";

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

            var manifestError = WriteJson(Path.Combine(editionFolder, ManifestFileName), new PlaylistManifestDto(new List<PlaylistLaudaDto>()));
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

    /// <summary>Lista as Laudas do Espelho de uma Edição, já ordenadas por <c>Order</c> — Sprint 2 (2026-11-15).</summary>
    public static (bool Ok, string? Error, IReadOnlyList<PlaylistLaudaRecord> Laudas) ListLaudas(string rootFolder, string programId, string editionId)
    {
        var (resolveOk, resolveError, editionFolder) = ResolveEditionFolder(rootFolder, programId, editionId);
        if (!resolveOk || editionFolder is null)
        {
            return (false, resolveError, Array.Empty<PlaylistLaudaRecord>());
        }

        var (ok, error, manifest) = ReadManifest(editionFolder);
        if (!ok)
        {
            return (false, error, Array.Empty<PlaylistLaudaRecord>());
        }

        var laudas = manifest!.Laudas.OrderBy(l => l.Order).Select(ToRecord).ToList();
        return (true, null, laudas);
    }

    /// <summary>
    /// Cria uma Lauda nova no fim do Espelho (sem reordenação ainda — próxima sprint) — Sprint 2
    /// (2026-11-15). Não recebe VT/Imagens aqui: os dois são enviados depois, já com o Id da Lauda
    /// criada, via <see cref="SaveUploadedFile"/>.
    /// </summary>
    public static (bool Ok, string? Error, PlaylistLaudaRecord? Lauda) CreateLauda(
        string rootFolder,
        string programId,
        string editionId,
        string? title,
        string? type,
        Dictionary<string, string>? gcFields,
        string? tpText,
        string? offText,
        string? durationPlanned,
        string? notes)
    {
        // Pedido do usuário (2026-11-15): nenhum campo da Lauda, inclusive o Título, é obrigatório
        // de verdade pra salvar — qualquer campo em branco (Tipo, GC, TP, OFF, Duração, Observações
        // e, desde esta mudança, também o Título) é um caso válido, não um erro. Uma Lauda sem
        // Título nenhum ainda precisa de algo pra mostrar na lista — "(sem título)" só na exibição,
        // nunca grava um valor inventado no lugar do que o produtor realmente digitou (ou não
        // digitou).
        var resolvedTitle = string.IsNullOrWhiteSpace(title) ? "(sem título)" : title.Trim();

        var (resolveOk, resolveError, editionFolder) = ResolveEditionFolder(rootFolder, programId, editionId);
        if (!resolveOk || editionFolder is null)
        {
            return (false, resolveError, null);
        }

        var (ok, error, manifest) = ReadManifest(editionFolder);
        if (!ok)
        {
            return (false, error, null);
        }

        var nextOrder = manifest!.Laudas.Count == 0 ? 1 : manifest.Laudas.Max(l => l.Order) + 1;
        var entry = new PlaylistLaudaDto(
            Guid.NewGuid().ToString(),
            nextOrder,
            resolvedTitle,
            (type ?? string.Empty).Trim(),
            string.Empty,
            gcFields ?? new Dictionary<string, string>(),
            tpText,
            offText,
            new List<string>(),
            durationPlanned,
            null,
            null,
            null,
            PendingIngestStatus,
            notes);
        manifest.Laudas.Add(entry);

        var writeError = WriteManifest(editionFolder, manifest);
        return writeError is not null ? (false, writeError, null) : (true, null, ToRecord(entry));
    }

    /// <summary>
    /// Grava um arquivo recebido da dashboard (VT ou imagem de apoio) no disco e atualiza a Lauda
    /// correspondente no manifesto — Sprint 2 (2026-11-15). Sem validação real de conteúdo ainda
    /// (nenhuma checagem de que é realmente um vídeo/imagem válido — fica pra uma sprint futura,
    /// mesmo princípio já registrado desde a Sprint 1); só grava os bytes recebidos e confia no nome/
    /// extensão informados pelo navegador. VT grava direto na pasta da Edição; imagens vão pra uma
    /// subpasta "imagens" (evita misturar os dois tipos de arquivo na mesma pasta que futuramente o
    /// ingest de VT vai escanear).
    /// </summary>
    public static (bool Ok, string? Error, string? SavedFileName) SaveUploadedFile(
        string rootFolder,
        string programId,
        string editionId,
        string laudaId,
        PlaylistUploadKind kind,
        string requestedFileName,
        Stream content)
    {
        var (resolveOk, resolveError, editionFolder) = ResolveEditionFolder(rootFolder, programId, editionId);
        if (!resolveOk || editionFolder is null)
        {
            return (false, resolveError, null);
        }

        try
        {
            var targetFolder = kind == PlaylistUploadKind.Image ? Path.Combine(editionFolder, "imagens") : editionFolder;
            Directory.CreateDirectory(targetFolder);

            var finalName = MakeUniqueFileName(targetFolder, string.IsNullOrWhiteSpace(requestedFileName) ? "arquivo" : requestedFileName);

            using (var fileStream = File.Create(Path.Combine(targetFolder, finalName)))
            {
                content.CopyTo(fileStream);
            }

            var (ok, error, manifest) = ReadManifest(editionFolder);
            if (!ok)
            {
                return (false, error, null);
            }

            var index = manifest!.Laudas.FindIndex(l => string.Equals(l.Id, laudaId, StringComparison.OrdinalIgnoreCase));
            if (index < 0)
            {
                return (false, "Lauda não encontrada.", null);
            }

            var current = manifest.Laudas[index];
            var updated = kind == PlaylistUploadKind.Video
                ? current with { FileName = finalName, Status = ReadyStatus }
                : current with { ImageFileNames = new List<string>(current.ImageFileNames) { finalName } };
            manifest.Laudas[index] = updated;

            var writeError = WriteManifest(editionFolder, manifest);
            return writeError is not null ? (false, writeError, null) : (true, null, finalName);
        }
        catch (Exception ex)
        {
            return (false, ex.Message, null);
        }
    }

    private static PlaylistLaudaRecord ToRecord(PlaylistLaudaDto dto) => new(
        dto.Id,
        dto.Order,
        dto.Title,
        dto.Type,
        dto.FileName,
        new Dictionary<string, string>(dto.GcFields),
        dto.TpText,
        dto.OffText,
        new List<string>(dto.ImageFileNames),
        dto.DurationPlanned,
        dto.Duration,
        dto.Status,
        dto.Notes);

    private static (bool Ok, string? Error, string? EditionFolder) ResolveEditionFolder(string rootFolder, string programId, string editionId)
    {
        var (programOk, programError, program) = FindProgram(rootFolder, programId);
        if (!programOk || program is null)
        {
            return (false, programError ?? "Programa não encontrado.", null);
        }

        var (editionsOk, editionsError, editions) = ListEditions(rootFolder, programId);
        if (!editionsOk)
        {
            return (false, editionsError, null);
        }

        var edition = editions.FirstOrDefault(e => string.Equals(e.Id, editionId, StringComparison.OrdinalIgnoreCase));
        if (edition is null)
        {
            return (false, "Edição não encontrada.", null);
        }

        return (true, null, edition.FolderPath);
    }

    private static (bool Ok, string? Error, PlaylistManifestDto? Manifest) ReadManifest(string editionFolder) =>
        ReadJson<PlaylistManifestDto>(Path.Combine(editionFolder, ManifestFileName), new PlaylistManifestDto(new List<PlaylistLaudaDto>()));

    private static string? WriteManifest(string editionFolder, PlaylistManifestDto manifest) =>
        WriteJson(Path.Combine(editionFolder, ManifestFileName), manifest);

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

    /// <summary>
    /// Mesma ideia de <see cref="MakeUniqueFolderName"/>, mas pra ARQUIVO (checa <c>File.Exists</c>,
    /// não <c>Directory.Exists</c>, e preserva a extensão ao acrescentar o sufixo " (2)") — Sprint 2
    /// (2026-11-15), usada pelo upload de VT/imagens: nunca sobrescreve um arquivo já enviado antes
    /// só porque o produtor subiu outro com o mesmo nome.
    /// </summary>
    private static string MakeUniqueFileName(string targetFolder, string rawFileName)
    {
        var sanitized = SanitizeFolderName(rawFileName);
        var extension = Path.GetExtension(sanitized);
        var nameWithoutExtension = Path.GetFileNameWithoutExtension(sanitized);
        var candidate = sanitized;
        var suffix = 2;
        while (File.Exists(Path.Combine(targetFolder, candidate)))
        {
            candidate = $"{nameWithoutExtension} ({suffix}){extension}";
            suffix++;
        }

        return candidate;
    }

    private sealed record ProgramEntryDto(string Id, string Name, string FolderName);

    private sealed record ProgramsIndexDto(List<ProgramEntryDto> Programs);

    private sealed record EditionEntryDto(string Id, string DateIso, string FolderName);

    private sealed record EditionsIndexDto(List<EditionEntryDto> Editions);

    /// <summary>
    /// Formato de uma Lauda dentro do <c>playlist.json</c> de uma Edição — Sprint 2 (2026-11-15,
    /// renomeado de <c>PlaylistEntryDto</c> e expandido com os campos do Espelho: Título, Tipo, TP,
    /// OFF, Imagens, Duração prevista e Observações). <see cref="Status"/> é texto cru (ver
    /// <see cref="PendingIngestStatus"/>/<see cref="ReadyStatus"/>), nunca o enum do Core.
    /// </summary>
    private sealed record PlaylistLaudaDto(
        string Id,
        int Order,
        string Title,
        string Type,
        string FileName,
        Dictionary<string, string> GcFields,
        string? TpText,
        string? OffText,
        List<string> ImageFileNames,
        string? DurationPlanned,
        string? Duration,
        string? Resolution,
        string? Codec,
        string Status,
        string? Notes);

    private sealed record PlaylistManifestDto(List<PlaylistLaudaDto> Laudas);
}
