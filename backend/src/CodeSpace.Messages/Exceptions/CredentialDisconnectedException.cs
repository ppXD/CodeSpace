using CodeSpace.Messages.Failures;

namespace CodeSpace.Messages.Exceptions;

/// <summary>
/// Thrown when something tries to authenticate through a credential that was disconnected. A disconnect revokes the
/// row and blanks its encrypted payload, so there is nothing left to decrypt — and Data Protection would report that
/// empty payload as one "not protected with this protection provider", which sends a reader hunting for a key-ring
/// fault that does not exist. The remedy is a live credential, by the steps a person can take today: a team Admin
/// re-links the repository to an active credential of the same provider, or, for a personal connection, its owner
/// reconnects the provider and what was bound through it follows. (Re-linking every repository on the credential at once
/// is an API with no screen yet, so the message does not send anyone looking for it.)
/// </summary>
public sealed class CredentialDisconnectedException : Exception, IFailure
{
    public FailureKind Kind => FailureKind.PreconditionRequired;

    public string Code => FailureCodes.CredentialDisconnected;

    public IReadOnlyDictionary<string, object?>? Details => new Dictionary<string, object?> { ["credentialId"] = CredentialId };

    public Guid CredentialId { get; }

    public CredentialDisconnectedException(Guid credentialId, string displayName)
        : base($"The credential '{displayName}' was disconnected, so nothing bound through it can authenticate. A team Admin can re-link the repository to an active credential of the same provider; if it was a personal connection, its owner reconnecting the provider moves it too. Then retry.")
    {
        CredentialId = credentialId;
    }
}
