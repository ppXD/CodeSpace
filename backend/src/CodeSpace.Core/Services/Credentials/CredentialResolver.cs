using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Messages.Credentials;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Exceptions;

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
        EnsureConnected(credential);

        var json = _encryptor.Decrypt(credential.EncryptedPayload);
        return _serializer.Deserialize(credential.AuthType, json);
    }

    /// <summary>A disconnect revokes the row and blanks its payload, so there is nothing to decrypt — say so, instead of letting Data Protection call the empty payload one it never protected.</summary>
    private static void EnsureConnected(Credential credential)
    {
        if (credential.Status == CredentialStatus.Revoked)
            throw new CredentialDisconnectedException(credential.Id, credential.DisplayName);
    }
}
