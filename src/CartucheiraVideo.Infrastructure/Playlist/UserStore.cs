using System.Security.Cryptography;
using System.Text.Json;

namespace CartucheiraVideo.Infrastructure.Playlist;

/// <summary>
/// Um usuário cadastrado da dashboard de Playlist, no vocabulário cru desta camada — nunca expõe
/// hash/sal de senha pra fora (ver <see cref="UserStore"/>). <see cref="AllowedProgramIds"/> só tem
/// sentido quando <see cref="IsAdmin"/> é falso: um Admin sempre vê/edita todos os Programas,
/// independente do que estiver nesta lista (fica vazia por convenção pra um Admin, ver
/// <see cref="UserStore.CreateUser"/>).
/// </summary>
public sealed record DashboardUserRecord(string Id, string Username, bool IsAdmin, IReadOnlyList<string> AllowedProgramIds);

/// <summary>
/// Login/permissão por usuário da dashboard de Playlist — Sprint 5 (2026-11-16, pedido do usuário
/// "precisamos de uma barra de login... criar usuários... dando permissão"). Decisões confirmadas
/// com o usuário antes de implementar: (1) login só vale pra dashboard web dos produtores, a
/// Cartucheira desktop continua abrindo sem senha; (2) permissão é por Programa — o Admin escolhe,
/// usuário por usuário, quais Programas cada um pode ver/editar; (3) o primeiro Admin é criado pela
/// própria Cartucheira (Configurações → Playlist → "Usuários da Dashboard"), não por uma tela de
/// "primeiro acesso" na dashboard; (4) só Admin cria Programa novo — um usuário comum só cria
/// Data/Lauda DENTRO de Programas que já tem acesso.
///
/// Mesmo princípio de isolamento de sempre (regra em arquitetura-sistema.md §7): esta camada não
/// conhece <c>CartucheiraVideo.Core</c> nem <c>CartucheiraVideo.Configuration</c> — fala só em
/// strings/listas cruas (<see cref="DashboardUserRecord"/>). Em particular, o hash de senha
/// (PBKDF2 via <see cref="Rfc2898DeriveBytes"/>) está DUPLICADO aqui a partir de
/// <c>CartucheiraVideo.Configuration.PasswordHasher</c> (usado pra senha de admin da própria
/// Cartucheira) em vez de referenciado — mesmos parâmetros (100k iterações, SHA256, sal de 16
/// bytes), só que esta camada não pode depender do projeto <c>Configuration</c> sem quebrar a regra
/// de zero <c>ProjectReference</c> entre Core/Engine/Ndi/Infrastructure/Api/Configuration (só a
/// Presentation conhece todos) — duplicar um algoritmo conhecido-bom é mais barato e seguro do que
/// inventar uma 8ª camada compartilhada só pra isto.
///
/// Persistência: <c>{Raiz da Playlist}\users.json</c>, mesmo padrão de JSON simples via
/// <c>System.Text.Json</c> (BCL, sem pacote novo) já usado por <see cref="PlaylistFileStore"/>.
///
/// Nunca lança: toda falha (disco, usuário duplicado, senha curta, Id desconhecido) vira
/// <c>(false, mensagem)</c> — mesmo princípio de sempre.
/// </summary>
public static class UserStore
{
    private const string UsersFileName = "users.json";
    private const int MinPasswordLength = 4;

    // Mesmos parâmetros de CartucheiraVideo.Configuration.PasswordHasher — ver comentário de classe
    // pro porquê de estarem duplicados aqui em vez de referenciados.
    private const int SaltSizeBytes = 16;
    private const int HashSizeBytes = 32;
    private const int Iterations = 100_000;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static (bool Ok, string? Error, IReadOnlyList<DashboardUserRecord> Users) ListUsers(string rootFolder)
    {
        if (string.IsNullOrWhiteSpace(rootFolder))
        {
            return (false, "pasta raiz da Playlist não configurada.", Array.Empty<DashboardUserRecord>());
        }

        var (ok, error, index) = ReadIndex(rootFolder);
        return ok
            ? (true, null, index!.Users.Select(ToRecord).ToList())
            : (false, error, Array.Empty<DashboardUserRecord>());
    }

    /// <summary>
    /// Cria um usuário novo. <paramref name="isAdmin"/> true ignora <paramref name="allowedProgramIds"/>
    /// (grava lista vazia — um Admin sempre vê/edita todos os Programas, não precisa de lista).
    /// </summary>
    public static (bool Ok, string? Error, DashboardUserRecord? User) CreateUser(
        string rootFolder, string username, string password, bool isAdmin, IReadOnlyList<string>? allowedProgramIds)
    {
        if (string.IsNullOrWhiteSpace(rootFolder))
        {
            return (false, "pasta raiz da Playlist não configurada.", null);
        }

        var trimmedUsername = (username ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(trimmedUsername))
        {
            return (false, "nome de usuário não pode ser vazio.", null);
        }

        if (string.IsNullOrEmpty(password) || password.Length < MinPasswordLength)
        {
            return (false, $"a senha precisa ter pelo menos {MinPasswordLength} caracteres.", null);
        }

        try
        {
            Directory.CreateDirectory(rootFolder);

            var (ok, error, index) = ReadIndex(rootFolder);
            if (!ok)
            {
                return (false, error, null);
            }

            if (index!.Users.Any(u => string.Equals(u.Username, trimmedUsername, StringComparison.OrdinalIgnoreCase)))
            {
                return (false, $"já existe um usuário chamado \"{trimmedUsername}\".", null);
            }

            // O primeiro usuário cadastrado precisa ser Admin — senão ninguém conseguiria gerenciar
            // usuários depois (um usuário comum nunca pode se auto-promover) nem criar Programas (só
            // Admin cria, decisão confirmada da Sprint 5). Nunca deixa esse estado acontecer, em vez
            // de só avisar na UI e confiar que o operador lê o aviso.
            if (index.Users.Count == 0 && !isAdmin)
            {
                return (false, "o primeiro usuário cadastrado precisa ser Administrador (marque a opção \"Administrador\").", null);
            }

            var (hash, salt) = HashPassword(password);
            var entry = new UserEntryDto(
                Guid.NewGuid().ToString(),
                trimmedUsername,
                hash,
                salt,
                isAdmin,
                isAdmin ? new List<string>() : (allowedProgramIds ?? Array.Empty<string>()).Distinct().ToList());
            index.Users.Add(entry);

            var writeError = WriteIndex(rootFolder, index);
            return writeError is not null ? (false, writeError, null) : (true, null, ToRecord(entry));
        }
        catch (Exception ex)
        {
            return (false, ex.Message, null);
        }
    }

    /// <summary>Troca papel (Admin/Produtor) e, se não-Admin, a lista de Programas liberados — nunca muda nome de usuário/senha (ver <see cref="ResetPassword"/>).</summary>
    public static (bool Ok, string? Error, DashboardUserRecord? User) UpdateUserPermissions(
        string rootFolder, string userId, bool isAdmin, IReadOnlyList<string>? allowedProgramIds)
    {
        var (ok, error, index, idx) = ReadIndexAndFind(rootFolder, userId);
        if (!ok || index is null || idx < 0)
        {
            return (false, error ?? "usuário não encontrado.", null);
        }

        var current = index.Users[idx];

        // Nunca deixa remover o acesso de Admin do ÚLTIMO Admin cadastrado — travaria todo mundo
        // fora da gestão de usuários (só um Admin consegue editar permissões), mesmo cuidado já
        // aplicado em DeleteUser.
        if (current.IsAdmin && !isAdmin && index.Users.Count(u => u.IsAdmin) <= 1)
        {
            return (false, "este é o único Administrador cadastrado — crie outro Admin antes de tirar esta permissão.", null);
        }

        var updated = current with
        {
            IsAdmin = isAdmin,
            AllowedProgramIds = isAdmin ? new List<string>() : (allowedProgramIds ?? Array.Empty<string>()).Distinct().ToList()
        };
        index.Users[idx] = updated;

        var writeError = WriteIndex(rootFolder, index);
        return writeError is not null ? (false, writeError, null) : (true, null, ToRecord(updated));
    }

    /// <summary>Define uma senha nova pra um usuário já existente — mesma validação de tamanho mínimo de <see cref="CreateUser"/>.</summary>
    public static string? ResetPassword(string rootFolder, string userId, string newPassword)
    {
        if (string.IsNullOrEmpty(newPassword) || newPassword.Length < MinPasswordLength)
        {
            return $"a senha precisa ter pelo menos {MinPasswordLength} caracteres.";
        }

        var (ok, error, index, idx) = ReadIndexAndFind(rootFolder, userId);
        if (!ok || index is null || idx < 0)
        {
            return error ?? "usuário não encontrado.";
        }

        var (hash, salt) = HashPassword(newPassword);
        index.Users[idx] = index.Users[idx] with { PasswordHash = hash, PasswordSalt = salt };

        return WriteIndex(rootFolder, index);
    }

    /// <summary>Remove um usuário — nunca o único Admin cadastrado (ver <see cref="UpdateUserPermissions"/> pro mesmo cuidado).</summary>
    public static string? DeleteUser(string rootFolder, string userId)
    {
        var (ok, error, index, idx) = ReadIndexAndFind(rootFolder, userId);
        if (!ok || index is null || idx < 0)
        {
            return error ?? "usuário não encontrado.";
        }

        if (index.Users[idx].IsAdmin && index.Users.Count(u => u.IsAdmin) <= 1)
        {
            return "este é o único Administrador cadastrado — crie outro Admin antes de remover este.";
        }

        index.Users.RemoveAt(idx);
        return WriteIndex(rootFolder, index);
    }

    /// <summary>
    /// Confere usuário/senha pro login da dashboard — mensagem de erro deliberadamente genérica
    /// ("usuário ou senha incorretos"), nunca revela se o usuário existe ou não, prática básica de
    /// segurança de login.
    /// </summary>
    public static (bool Ok, string? Error, DashboardUserRecord? User) VerifyCredentials(string rootFolder, string username, string password)
    {
        const string genericError = "usuário ou senha incorretos.";

        if (string.IsNullOrWhiteSpace(rootFolder))
        {
            return (false, "pasta raiz da Playlist não configurada.", null);
        }

        var (ok, error, index) = ReadIndex(rootFolder);
        if (!ok)
        {
            return (false, error, null);
        }

        var match = index!.Users.FirstOrDefault(u => string.Equals(u.Username, (username ?? string.Empty).Trim(), StringComparison.OrdinalIgnoreCase));
        if (match is null || string.IsNullOrEmpty(password) || !VerifyPassword(password, match.PasswordHash, match.PasswordSalt))
        {
            return (false, genericError, null);
        }

        return (true, null, ToRecord(match));
    }

    private static DashboardUserRecord ToRecord(UserEntryDto dto) =>
        new(dto.Id, dto.Username, dto.IsAdmin, new List<string>(dto.AllowedProgramIds ?? new List<string>()));

    private static (bool Ok, string? Error, UsersIndexDto? Index, int UserIndex) ReadIndexAndFind(string rootFolder, string userId)
    {
        var (ok, error, index) = ReadIndex(rootFolder);
        if (!ok)
        {
            return (false, error, null, -1);
        }

        var idx = index!.Users.FindIndex(u => string.Equals(u.Id, userId, StringComparison.OrdinalIgnoreCase));
        return idx < 0 ? (false, "usuário não encontrado.", index, -1) : (true, null, index, idx);
    }

    private static (bool Ok, string? Error, UsersIndexDto? Index) ReadIndex(string rootFolder)
    {
        var path = Path.Combine(rootFolder, UsersFileName);
        try
        {
            if (!File.Exists(path))
            {
                return (true, null, new UsersIndexDto(new List<UserEntryDto>()));
            }

            var json = File.ReadAllText(path);
            var value = JsonSerializer.Deserialize<UsersIndexDto>(json, JsonOptions);

            // Mesma cautela já aplicada em PlaylistFileStore.ReadManifest: uma lista ausente/nula no
            // JSON desserializa como null em tempo de execução, mesmo com o tipo não-anulável em
            // tempo de compilação — nunca confia nisso sem checar.
            if (value is null)
            {
                return (true, null, new UsersIndexDto(new List<UserEntryDto>()));
            }

            if (value.Users is null)
            {
                value = value with { Users = new List<UserEntryDto>() };
            }

            return (true, null, value);
        }
        catch (Exception ex)
        {
            return (false, $"erro lendo {UsersFileName}: {ex.Message}", null);
        }
    }

    private static string? WriteIndex(string rootFolder, UsersIndexDto index)
    {
        try
        {
            var json = JsonSerializer.Serialize(index, JsonOptions);
            File.WriteAllText(Path.Combine(rootFolder, UsersFileName), json);
            return null;
        }
        catch (Exception ex)
        {
            return $"erro gravando {UsersFileName}: {ex.Message}";
        }
    }

    private static (string Hash, string Salt) HashPassword(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltSizeBytes);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, HashSizeBytes);
        return (Convert.ToBase64String(hash), Convert.ToBase64String(salt));
    }

    private static bool VerifyPassword(string password, string expectedHash, string salt)
    {
        byte[] saltBytes;
        byte[] expectedBytes;
        try
        {
            saltBytes = Convert.FromBase64String(salt);
            expectedBytes = Convert.FromBase64String(expectedHash);
        }
        catch (FormatException)
        {
            return false;
        }

        var computedHash = Rfc2898DeriveBytes.Pbkdf2(password, saltBytes, Iterations, HashAlgorithmName.SHA256, HashSizeBytes);
        return CryptographicOperations.FixedTimeEquals(computedHash, expectedBytes);
    }

    private sealed record UserEntryDto(
        string Id,
        string Username,
        string PasswordHash,
        string PasswordSalt,
        bool IsAdmin,
        List<string> AllowedProgramIds);

    private sealed record UsersIndexDto(List<UserEntryDto> Users);
}
