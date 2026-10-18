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

    /// <summary>
    /// A disconnect revokes the row and blanks its payload, so there is nothing to decrypt — say so, instead of letting Data
    /// Protection call the empty payload one it never protected. A credential its provider refused for good (marked
    /// <c>Error</c>, with the reason) is not asked again: there is a payload, and presenting it only earns the same refusal —
    /// a refused refresh token, a 401 on every call — so it fails here, naming the credential and what the provider said.
    ///
    /// <para>For an hour, and for an OAuth credential only (<see cref="IsDueToBeAskedAgain"/>): past that a use is let through to
    /// the refresher, which asks the provider once more — a still-dead grant is refused again and re-marked, which dates the
    /// verdict afresh, so it costs one refused call an hour; one the provider accepts again (an OAuth client put back, an
    /// account unblocked) is refreshed and the pair it issues lifts the verdict, instead of failing runs until the sweep
    /// next looks. A static token has nothing to refresh, and the sweep re-asks it on the same schedule.</para>
    /// </summary>
    private static void EnsureConnected(Credential credential)
    {
        if (credential.Status == CredentialStatus.Revoked)
            throw new CredentialDisconnectedException(credential.Id, credential.DisplayName);

        if (credential.Status == CredentialStatus.Error && !IsDueToBeAskedAgain(credential))
            throw new CredentialNeedsReconnectException(credential.Id, credential.DisplayName, credential.LastError ?? "its provider refused it");
    }

    /// <summary>An OAuth credential whose verdict was last written, or last confirmed, longer ago than <see cref="CredentialHealthService.ErrorRecheckAfter"/> — the window the sweep re-asks an Error credential after.</summary>
    private static bool IsDueToBeAskedAgain(Credential credential) =>
        credential.AuthType == AuthType.OAuth && DateTimeOffset.UtcNow - credential.LastModifiedDate >= CredentialHealthService.ErrorRecheckAfter;
}
