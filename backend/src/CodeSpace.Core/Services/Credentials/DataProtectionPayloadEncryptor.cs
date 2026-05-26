using CodeSpace.Core.DependencyInjection;
using Microsoft.AspNetCore.DataProtection;

namespace CodeSpace.Core.Services.Credentials;

public sealed class DataProtectionPayloadEncryptor : IPayloadEncryptor, ISingletonDependency
{
    private const string Purpose = "CodeSpace.Credentials.v1";

    private readonly IDataProtector _protector;

    public DataProtectionPayloadEncryptor(IDataProtectionProvider provider)
    {
        _protector = provider.CreateProtector(Purpose);
    }

    public string Encrypt(string plaintext) => _protector.Protect(plaintext);
    public string Decrypt(string ciphertext) => _protector.Unprotect(ciphertext);
}
