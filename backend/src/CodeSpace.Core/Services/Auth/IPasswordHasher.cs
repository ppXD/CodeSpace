namespace CodeSpace.Core.Services.Auth;

/// <summary>
/// One-way password hashing for the local-account sign-in path. The hash string is
/// self-describing — algorithm, iteration count, salt and digest are all packed into the
/// stored value so we can rotate algorithms / iteration counts without a migration.
/// </summary>
public interface IPasswordHasher
{
    /// <summary>Hashes a plaintext password. Output is safe to store in a TEXT column.</summary>
    string Hash(string password);

    /// <summary>True iff the plaintext matches the previously-issued hash. Returns false on any malformed input.</summary>
    bool Verify(string password, string hash);
}
