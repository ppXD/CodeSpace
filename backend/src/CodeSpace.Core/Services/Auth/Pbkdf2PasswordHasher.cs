using System.Security.Cryptography;
using CodeSpace.Core.DependencyInjection;

namespace CodeSpace.Core.Services.Auth;

/// <summary>
/// PBKDF2 (RFC 2898) with SHA-256, 100 000 iterations, 16-byte random salt, 32-byte digest.
/// Storage format: <c>pbkdf2$sha256$&lt;iters&gt;$&lt;saltBase64&gt;$&lt;digestBase64&gt;</c> — version
/// prefix lets us rotate to bcrypt/argon2 later without touching the column.
/// </summary>
public sealed class Pbkdf2PasswordHasher : IPasswordHasher, ISingletonDependency
{
    private const string Algorithm = "pbkdf2";
    private const string HashName = "sha256";
    private const int DefaultIterations = 100_000;
    private const int SaltBytes = 16;
    private const int DigestBytes = 32;

    public string Hash(string password)
    {
        if (string.IsNullOrEmpty(password)) throw new ArgumentException("password must be non-empty", nameof(password));

        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var digest = Pbkdf2(password, salt, DefaultIterations);

        return Format(DefaultIterations, salt, digest);
    }

    public bool Verify(string password, string hash)
    {
        if (string.IsNullOrEmpty(password) || string.IsNullOrEmpty(hash)) return false;

        var parts = hash.Split('$');
        if (parts.Length != 5) return false;
        if (parts[0] != Algorithm || parts[1] != HashName) return false;
        if (!int.TryParse(parts[2], out var iterations) || iterations < 1000) return false;

        byte[] salt, expected;
        try { salt = Convert.FromBase64String(parts[3]); expected = Convert.FromBase64String(parts[4]); }
        catch (FormatException) { return false; }

        var actual = Pbkdf2(password, salt, iterations);
        // Fixed-time compare to avoid timing oracles on the digest.
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    private static byte[] Pbkdf2(string password, byte[] salt, int iterations)
        => Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, DigestBytes);

    private static string Format(int iterations, byte[] salt, byte[] digest)
        => $"{Algorithm}${HashName}${iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(digest)}";
}
