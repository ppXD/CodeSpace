using CodeSpace.Core.DependencyInjection;
using Microsoft.AspNetCore.DataProtection;

namespace CodeSpace.Core.Services.Credentials;

public sealed class DataProtectionPayloadEncryptor : IPayloadEncryptor, ISingletonDependency
{
    /// <summary>
    /// Stable Data Protection purpose for every persisted credential envelope. Renaming this value makes existing
    /// payloads undecryptable; a new format must use an explicitly versioned migration instead.
    /// </summary>
    internal const string ProtectorPurpose = "CodeSpace.Credentials.v1";

    private readonly IDataProtector _protector;

    public DataProtectionPayloadEncryptor(IDataProtectionProvider provider)
    {
        _protector = provider.CreateProtector(ProtectorPurpose);
    }

    public string Encrypt(string plaintext) => _protector.Protect(plaintext);
    public string Decrypt(string ciphertext) => _protector.Unprotect(ciphertext);
}
