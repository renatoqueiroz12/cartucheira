using System.Text.Json;

namespace CartucheiraVideo.Infrastructure.Cartucheira;

/// <summary>Um Programa de uma Cartucheira (Vinhetas ou Trilhas), no vocabulário cru desta camada — ver comentário de classe de <see cref="CartucheiraFileStore"/>.</summary>
public sealed record CartucheiraProgramRecord(string Id, string Name, string FolderName);

/// <summary>Um cartucho (botão) dentro de um Programa, no vocabulário cru desta camada. <see cref="Color"/> nulo = sem cor escolhida ainda nesta sprint (quem exibe decide a cor padrão do banco, igual <c>AudioBankViewModel.PadColorFor</c> já fazia pra cartuchos sem override).</summary>
public sealed record CartucheiraCartridgeRecord(string Id, int Order, string Title, string FileName, string? Color);

/// <summary>
/// Leitura/escrita em disco do módulo Vinhetas/Trilhas da dashboard web — Sprint
/// Cartucheira-Dashboard (2026-11-23, pedido do usuário "o menu vinhetas, trilhas, parece estar meio
/// incompleto. Vamos colocar eles no dashboard? [...] a gente cria os programas e cada programa a
/// gente coloca lá uma pasta raiz de áudios e cada programa mesmo formato de upload de vídeo [...]
/// aí lá do dashboard eu posso organizar o posicionamento das teclinhas [...] e renomear, colocar
/// cor"). Mesmo princípio de isolamento e mesmo formato em disco já usado por
/// <c>CartucheiraVideo.Infrastructure.Playlist.PlaylistFileStore</c> (ver comentário de classe lá) —
/// esta classe não conhece <c>Core</c>, fala só em strings cruas; quem traduz pro resto do sistema é a
/// Presentation (<c>PlaylistDashboardCoordinator</c> do lado da dashboard, <c>AudioBankViewModel</c>
/// do lado do app desktop).
///
/// Diferença deliberada em relação ao Playlist de vídeo: NÃO existe uma camada "Edição" (Data) aqui —
/// um Programa de Vinhetas/Trilhas é permanente (os cartuchos ficam ali até alguém apagar/trocar),
/// não um espelho por dia. Por isso a estrutura em disco tem um nível a menos:
/// <list type="bullet">
/// <item><c>{Raiz}\programs.json</c> — índice dos Programas deste banco (Vinhetas OU Trilhas — cada
/// banco usa a própria pasta raiz configurada em Configurações → Cartucheira, ver
/// <c>Configuration.CartridgeSettingsSnapshot.VinhetasFolderPath</c>/<c>TrilhasFolderPath</c>; esta
/// classe em si não sabe qual banco é, só recebe a raiz já resolvida).</item>
/// <item><c>{Raiz}\{PastaDoPrograma}\cartuchos.json</c> — índice ORDENADO dos cartuchos daquele
/// Programa (Id/Ordem/Título/nome do arquivo/cor).</item>
/// <item><c>{Raiz}\{PastaDoPrograma}\{arquivo de áudio}</c> — o arquivo de verdade, enviado via
/// upload (mesmo formato cru-no-corpo-da-requisição do upload de VT/imagem do Playlist, ver
/// <c>PlaylistDashboardHost.PlaylistUploadHandler</c>).</item>
/// </list>
///
/// Convivência deliberada com o fluxo antigo (pasta manual, sem dashboard) — decisão confirmada com o
/// usuário ("Mantém os dois funcionando juntos"): uma pasta raiz SEM <c>programs.json</c> (nunca
/// usada pela dashboard, ou criada manualmente no Explorer antes desta sprint) continua sendo lida do
/// jeito antigo pelo <c>AudioBankViewModel</c> (subpasta = Programa, arquivo de áudio = cartucho,
/// ordem alfabética) — ver <see cref="HasDashboardIndex"/>, que é o único jeito de saber qual dos
/// dois modos usar pra uma raiz dada. Depois que a dashboard cria o primeiro Programa numa raiz, ela
/// vira dashboard-managed pra sempre (não há caminho de volta nesta sprint).
///
/// Nunca lança: toda falha de disco vira <c>(false, mensagem)</c>, mesmo princípio de
/// <c>PlaylistFileStore</c>/<c>VMixClient</c>.
/// </summary>
public static class CartucheiraFileStore
{
    private const string ProgramsIndexFileName = "programs.json";
    private const string CartridgesIndexFileName = "cartuchos.json";

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>
    /// Verdadeiro quando esta raiz já foi usada pela dashboard (tem <c>programs.json</c>, mesmo que
    /// vazio) — é o sinal que o <c>AudioBankViewModel</c> usa pra decidir entre ler os Programas daqui
    /// (dashboard-managed) ou escanear subpastas cruas do jeito antigo (ver comentário de classe).
    /// </summary>
    public static bool HasDashboardIndex(string rootFolder) =>
        !string.IsNullOrWhiteSpace(rootFolder) && File.Exists(Path.Combine(rootFolder, ProgramsIndexFileName));

    public static (bool Ok, string? Error, IReadOnlyList<CartucheiraProgramRecord> Programs) ListPrograms(string rootFolder)
    {
        if (string.IsNullOrWhiteSpace(rootFolder))
        {
            return (false, "pasta raiz não configurada (Configurações → Cartucheira).", Array.Empty<CartucheiraProgramRecord>());
        }

        var (ok, error, index) = ReadProgramsIndex(rootFolder);
        return ok
            ? (true, null, index!.Programs.Select(ToProgramRecord).ToList())
            : (false, error, Array.Empty<CartucheiraProgramRecord>());
    }

    public static (bool Ok, string? Error, CartucheiraProgramRecord? Program) CreateProgram(string rootFolder, string name)
    {
        if (string.IsNullOrWhiteSpace(rootFolder))
        {
            return (false, "pasta raiz não configurada (Configurações → Cartucheira).", null);
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

            // Garante o próprio cartuchos.json vazio já na criação — não é estritamente necessário
            // (ReadCartridgesIndex cai pro fallback vazio se não existir), mas deixa a pasta do
            // Programa honesta pra quem for olhar no Explorer antes do primeiro upload.
            var cartridgesWriteError = WriteJson(Path.Combine(rootFolder, folderName, CartridgesIndexFileName), new CartridgesIndexDto(new List<CartridgeEntryDto>()));
            if (cartridgesWriteError is not null)
            {
                return (false, cartridgesWriteError, null);
            }

            return (true, null, ToProgramRecord(entry));
        }
        catch (Exception ex)
        {
            return (false, ex.Message, null);
        }
    }

    /// <summary>Lista os cartuchos de um Programa, já ordenados por <c>Order</c>.</summary>
    public static (bool Ok, string? Error, IReadOnlyList<CartucheiraCartridgeRecord> Cartridges) ListCartridges(string rootFolder, string programId)
    {
        var (resolveOk, resolveError, programFolder) = ResolveProgramFolder(rootFolder, programId);
        if (!resolveOk || programFolder is null)
        {
            return (false, resolveError, Array.Empty<CartucheiraCartridgeRecord>());
        }

        var (ok, error, index) = ReadCartridgesIndex(programFolder);
        if (!ok)
        {
            return (false, error, Array.Empty<CartucheiraCartridgeRecord>());
        }

        var cartridges = index!.Cartridges.OrderBy(c => c.Order).Select(ToCartridgeRecord).ToList();
        return (true, null, cartridges);
    }

    /// <summary>
    /// Grava um arquivo de áudio enviado pela dashboard e cria o cartucho correspondente no fim da
    /// lista (Ordem = última + 1, Título = nome do arquivo sem extensão, sem cor escolhida ainda —
    /// quem exibe cai pra cor padrão do banco). Diferente do Playlist (Lauda criada vazia, upload
    /// depois), aqui upload e criação são um passo só — não há outro metadado pra preencher antes.
    /// </summary>
    public static (bool Ok, string? Error, CartucheiraCartridgeRecord? Cartridge) UploadCartridge(string rootFolder, string programId, string requestedFileName, Stream content)
    {
        var (resolveOk, resolveError, programFolder) = ResolveProgramFolder(rootFolder, programId);
        if (!resolveOk || programFolder is null)
        {
            return (false, resolveError, null);
        }

        try
        {
            Directory.CreateDirectory(programFolder);
            var finalName = MakeUniqueFileName(programFolder, string.IsNullOrWhiteSpace(requestedFileName) ? "audio" : requestedFileName);

            using (var fileStream = File.Create(Path.Combine(programFolder, finalName)))
            {
                content.CopyTo(fileStream);
            }

            var (ok, error, index) = ReadCartridgesIndex(programFolder);
            if (!ok)
            {
                return (false, error, null);
            }

            var nextOrder = index!.Cartridges.Count == 0 ? 1 : index.Cartridges.Max(c => c.Order) + 1;
            var title = Path.GetFileNameWithoutExtension(finalName);
            var entry = new CartridgeEntryDto(Guid.NewGuid().ToString(), nextOrder, title, finalName, null);
            index.Cartridges.Add(entry);

            var writeError = WriteJson(Path.Combine(programFolder, CartridgesIndexFileName), index);
            return writeError is not null ? (false, writeError, null) : (true, null, ToCartridgeRecord(entry));
        }
        catch (Exception ex)
        {
            return (false, ex.Message, null);
        }
    }

    public static (bool Ok, string? Error, CartucheiraCartridgeRecord? Cartridge) RenameCartridge(string rootFolder, string programId, string cartridgeId, string title)
    {
        var resolvedTitle = string.IsNullOrWhiteSpace(title) ? "(sem nome)" : title.Trim();
        return UpdateCartridgeEntry(rootFolder, programId, cartridgeId, entry => entry with { Title = resolvedTitle });
    }

    /// <summary><paramref name="color"/> nulo/vazio volta o cartucho pra cor padrão do banco (remove o override).</summary>
    public static (bool Ok, string? Error, CartucheiraCartridgeRecord? Cartridge) SetCartridgeColor(string rootFolder, string programId, string cartridgeId, string? color)
    {
        var resolvedColor = string.IsNullOrWhiteSpace(color) ? null : color.Trim();
        return UpdateCartridgeEntry(rootFolder, programId, cartridgeId, entry => entry with { Color = resolvedColor });
    }

    private static (bool Ok, string? Error, CartucheiraCartridgeRecord? Cartridge) UpdateCartridgeEntry(string rootFolder, string programId, string cartridgeId, Func<CartridgeEntryDto, CartridgeEntryDto> update)
    {
        var (resolveOk, resolveError, programFolder) = ResolveProgramFolder(rootFolder, programId);
        if (!resolveOk || programFolder is null)
        {
            return (false, resolveError, null);
        }

        var (ok, error, index) = ReadCartridgesIndex(programFolder);
        if (!ok)
        {
            return (false, error, null);
        }

        var entryIndex = index!.Cartridges.FindIndex(c => string.Equals(c.Id, cartridgeId, StringComparison.OrdinalIgnoreCase));
        if (entryIndex < 0)
        {
            return (false, "cartucho não encontrado.", null);
        }

        var updated = update(index.Cartridges[entryIndex]);
        index.Cartridges[entryIndex] = updated;

        var writeError = WriteJson(Path.Combine(programFolder, CartridgesIndexFileName), index);
        return writeError is not null ? (false, writeError, null) : (true, null, ToCartridgeRecord(updated));
    }

    /// <summary>
    /// Reordena os cartuchos conforme uma sequência de Ids dada pela dashboard (arrastar e soltar) —
    /// mesmo princípio de <c>PlaylistFileStore.ReorderLaudas</c>: rejeita (sem aplicar nada) se a
    /// lista enviada não bater exatamente com o conjunto de Ids existentes.
    /// </summary>
    public static (bool Ok, string? Error, IReadOnlyList<CartucheiraCartridgeRecord> Cartridges) ReorderCartridges(string rootFolder, string programId, IReadOnlyList<string> orderedIds)
    {
        var (resolveOk, resolveError, programFolder) = ResolveProgramFolder(rootFolder, programId);
        if (!resolveOk || programFolder is null)
        {
            return (false, resolveError, Array.Empty<CartucheiraCartridgeRecord>());
        }

        var (ok, error, index) = ReadCartridgesIndex(programFolder);
        if (!ok)
        {
            return (false, error, Array.Empty<CartucheiraCartridgeRecord>());
        }

        var currentIds = index!.Cartridges.Select(c => c.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var givenIds = orderedIds.ToList();
        if (givenIds.Count != currentIds.Count || !givenIds.All(id => currentIds.Contains(id)) || givenIds.Distinct(StringComparer.OrdinalIgnoreCase).Count() != givenIds.Count)
        {
            return (false, "a lista de cartuchos enviada para reordenar não bate com os cartuchos existentes deste Programa.", Array.Empty<CartucheiraCartridgeRecord>());
        }

        var byId = index.Cartridges.ToDictionary(c => c.Id, c => c, StringComparer.OrdinalIgnoreCase);
        var reordered = new List<CartridgeEntryDto>();
        for (var i = 0; i < givenIds.Count; i++)
        {
            reordered.Add(byId[givenIds[i]] with { Order = i + 1 });
        }

        var writeError = WriteJson(Path.Combine(programFolder, CartridgesIndexFileName), new CartridgesIndexDto(reordered));
        return writeError is not null
            ? (false, writeError, Array.Empty<CartucheiraCartridgeRecord>())
            : (true, null, reordered.OrderBy(c => c.Order).Select(ToCartridgeRecord).ToList());
    }

    /// <summary>Remove o cartucho do índice e tenta apagar o arquivo de áudio correspondente — falha ao apagar o ARQUIVO (permissão, em uso por outro processo) não impede remover do índice: melhor um cartucho "fantasma" sem entrada do que travar a operação toda.</summary>
    public static (bool Ok, string? Error) DeleteCartridge(string rootFolder, string programId, string cartridgeId)
    {
        var (resolveOk, resolveError, programFolder) = ResolveProgramFolder(rootFolder, programId);
        if (!resolveOk || programFolder is null)
        {
            return (false, resolveError);
        }

        var (ok, error, index) = ReadCartridgesIndex(programFolder);
        if (!ok)
        {
            return (false, error);
        }

        var entryIndex = index!.Cartridges.FindIndex(c => string.Equals(c.Id, cartridgeId, StringComparison.OrdinalIgnoreCase));
        if (entryIndex < 0)
        {
            return (false, "cartucho não encontrado.");
        }

        var entry = index.Cartridges[entryIndex];
        index.Cartridges.RemoveAt(entryIndex);

        var writeError = WriteJson(Path.Combine(programFolder, CartridgesIndexFileName), index);
        if (writeError is not null)
        {
            return (false, writeError);
        }

        try
        {
            var filePath = Path.Combine(programFolder, entry.FileName);
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }
        }
        catch (IOException)
        {
            // Arquivo em uso ou sem permissão — ver comentário do método.
        }

        return (true, null);
    }

    private static CartucheiraProgramRecord ToProgramRecord(ProgramEntryDto dto) => new(dto.Id, dto.Name, dto.FolderName);

    private static CartucheiraCartridgeRecord ToCartridgeRecord(CartridgeEntryDto dto) => new(dto.Id, dto.Order, dto.Title, dto.FileName, dto.Color);

    private static (bool Ok, string? Error, string? ProgramFolder) ResolveProgramFolder(string rootFolder, string programId)
    {
        var (ok, error, programs) = ListPrograms(rootFolder);
        if (!ok)
        {
            return (false, error, null);
        }

        var program = programs.FirstOrDefault(p => string.Equals(p.Id, programId, StringComparison.OrdinalIgnoreCase));
        return program is null ? (false, "Programa não encontrado.", null) : (true, null, Path.Combine(rootFolder, program.FolderName));
    }

    private static (bool Ok, string? Error, ProgramsIndexDto? Index) ReadProgramsIndex(string rootFolder) =>
        ReadJson<ProgramsIndexDto>(Path.Combine(rootFolder, ProgramsIndexFileName), new ProgramsIndexDto(new List<ProgramEntryDto>()));

    /// <summary>
    /// Mesma cautela de <c>PlaylistFileStore.ReadManifest</c> (ver comentário completo lá): um
    /// <c>cartuchos.json</c> sem a chave "Cartridges" (ou com ela nula) desserializaria como
    /// <c>null</c> sem lançar — normalizado aqui, no único lugar que lê o índice, pra não precisar
    /// repetir essa defesa em cada chamador.
    /// </summary>
    private static (bool Ok, string? Error, CartridgesIndexDto? Index) ReadCartridgesIndex(string programFolder)
    {
        var (ok, error, index) = ReadJson<CartridgesIndexDto>(Path.Combine(programFolder, CartridgesIndexFileName), new CartridgesIndexDto(new List<CartridgeEntryDto>()));
        if (ok && index is not null && index.Cartridges is null)
        {
            index = index with { Cartridges = new List<CartridgeEntryDto>() };
        }

        return (ok, error, index);
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

    private static string SanitizeFolderName(string raw)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = raw.Trim().Select(c => invalid.Contains(c) ? '_' : c).ToArray();
        var sanitized = new string(chars).Trim();
        return string.IsNullOrWhiteSpace(sanitized) ? "sem-nome" : sanitized;
    }

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

    private sealed record CartridgeEntryDto(string Id, int Order, string Title, string FileName, string? Color);

    private sealed record CartridgesIndexDto(List<CartridgeEntryDto> Cartridges);
}
