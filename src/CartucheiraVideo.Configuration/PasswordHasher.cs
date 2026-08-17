using System.Security.Cryptography;

namespace CartucheiraVideo.Configuration;

/// <summary>
/// Hash de senha via PBKDF2 (<see cref="Rfc2898DeriveBytes"/>) — só BCL, nenhum pacote novo. Usado
/// pra guardar a senha de admin em disco sem nunca gravar o texto puro (nem em memória além do
/// tempo estritamente necessário pra calcular/comparar o hash).
/// </summary>
public static class PasswordHasher
{
    private const int SaltSizeBytes = 16;
    private const int HashSizeBytes = 32;

    // 100k iterações é a recomendação corrente da OWASP pra PBKDF2-SHA256 (2024) — alto o
    // suficiente pra dificultar força bruta offline, baixo o suficiente pra não ser perceptível
    // num login manual (dezenas de milissegundos), o que também respeita a prioridade #1 do
    // projeto (Latência) sem abrir mão de segurança real.
    private const int Iterations = 100_000;

    /// <summary>Gera um hash novo (com salt aleatório novo) pra uma senha em texto puro — usado ao definir/trocar a senha de admin.</summary>
    public static (string Hash, string Salt) Hash(string password)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(password);

        var salt = RandomNumberGenerator.GetBytes(SaltSizeBytes);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, HashSizeBytes);

        return (Convert.ToBase64String(hash), Convert.ToBase64String(salt));
    }

    /// <summary>Confere se uma senha em texto puro bate com um hash/salt já guardados — usado no login. Comparação em tempo constante (<see cref="CryptographicOperations.FixedTimeEquals"/>), pra não vazar informação por timing.</summary>
    public static bool Verify(string password, string expectedHash, string salt)
    {
        if (string.IsNullOrEmpty(password) || string.IsNullOrEmpty(expectedHash) || string.IsNullOrEmpty(salt))
        {
            return false;
        }

        byte[] saltBytes;
        byte[] expectedBytes;
        try
        {
            saltBytes = Convert.FromBase64String(salt);
            expectedBytes = Convert.FromBase64String(expectedHash);
        }
        catch (FormatException)
        {
            // Salt/hash persistidos corrompidos — trata como senha incorreta, não como crash.
            return false;
        }

        var computedHash = Rfc2898DeriveBytes.Pbkdf2(password, saltBytes, Iterations, HashAlgorithmName.SHA256, HashSizeBytes);

        return CryptographicOperations.FixedTimeEquals(computedHash, expectedBytes);
    }
}
