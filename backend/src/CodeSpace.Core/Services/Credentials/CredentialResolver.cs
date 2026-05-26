using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Messages.Credentials;

namespace CodeSpace.Core.Services.Credentials;

public sealed class CredentialResolver : ICredentialResolver, IScopedDependency
{
    private readonly IPayloadEncryptor _encryptor;
    private readonly ICredentialPayloadSerializer _serializer;

    public CredentialResolver(IPayloadEncryptor encryptor, ICredentialPayloadSerializer serializer)
    {
        _encryptor = encryptor;
        _serializer = serializer;
    }

    public CredentialPayload Resolve(Credential credential)
    {
        var json = _encryptor.Decrypt(credential.EncryptedPayload);
        return _serializer.Deserialize(credential.AuthType, json);
    }
}
