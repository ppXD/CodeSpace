using CodeSpace.Messages.Failures;

namespace CodeSpace.Messages.Exceptions;

/// <summary>
/// Thrown when something tries to authenticate through a credential that was disconnected. A disconnect revokes the
/// row and blanks its encrypted payload, so there is nothing left to decrypt — and Data Protection would report that
/// empty payload as one "not protected with this protection provider", which sends a reader hunting for a key-ring
/// fault that does not exist. The remedy is a live credential: reconnect the provider (repositories bound through the
/// disconnected one follow their owner's reconnect), or re-link to another active credential.
/// </summary>
public sealed class CredentialDisconnectedException : Exception, IFailure
{
    public FailureKind Kind => FailureKind.PreconditionRequired;

    public string Code => FailureCodes.CredentialDisconnected;

    public IReadOnlyDictionary<string, object?>? Details => new Dictionary<string, object?> { ["credentialId"] = CredentialId };

    public Guid CredentialId { get; }

    public CredentialDisconnectedException(Guid credentialId, string displayName)
        : base($"The credential '{displayName}' was disconnected, so nothing bound through it can authenticate. Reconnect the provider, or re-link the repository to an active credential, then retry.")
    {
        CredentialId = credentialId;
    }
}
