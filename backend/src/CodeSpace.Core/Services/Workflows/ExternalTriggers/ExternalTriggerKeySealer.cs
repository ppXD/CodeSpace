using System.Security.Cryptography;
using System.Text;
using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Services.Variables;

namespace CodeSpace.Core.Services.Workflows.ExternalTriggers;

/// <summary>
/// Stores a SHA-256 digest for anonymous-route key lookup and AES-GCM ciphertext for permission-gated reveal. Data Protection
/// is not used because its key ring is stored unencrypted in the same database (<c>data_protection_keys</c>).
/// </summary>
public sealed class ExternalTriggerKeySealer : ISingletonDependency
{
    private readonly IVariableValueEncryption _encryption;

    public ExternalTriggerKeySealer(IVariableValueEncryption encryption) => _encryption = encryption;

    public SealedExternalTriggerKey Mint()
    {
        var key = "ext_" + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).Replace('+', '-').Replace('/', '_').TrimEnd('=');
        return new SealedExternalTriggerKey(key, Digest(key), Convert.ToBase64String(_encryption.Encrypt(key)));
    }

    public static string Digest(string key) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)));

    public string Open(string ciphertext) => _encryption.Decrypt(Convert.FromBase64String(ciphertext));
}

public sealed record SealedExternalTriggerKey(string Key, string Digest, string Ciphertext);
