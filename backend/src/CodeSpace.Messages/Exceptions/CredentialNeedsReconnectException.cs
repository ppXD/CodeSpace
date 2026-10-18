using CodeSpace.Messages.Failures;

namespace CodeSpace.Messages.Exceptions;

/// <summary>
/// Thrown when something tries to authenticate through a credential whose provider has refused it for good: the OAuth grant
/// behind it answered a refresh with <c>invalid_grant</c> (the stored pair is dead, or the provider's OAuth client changed
/// under it), or a static token was rejected by the provider or has expired. The credential row is marked
/// <c>Error</c> with the reason, so the next use fails here without asking the provider again. The remedy is a person's: the
/// owner reconnects the provider (or adds the token again), or a team Admin re-links what is bound through it to a live
/// credential. A retry as-is reads the same dead credential, which is why this is not a transient provider failure.
/// </summary>
public sealed class CredentialNeedsReconnectException : Exception, IFailure
{
    public FailureKind Kind => FailureKind.PreconditionRequired;

    public string Code => FailureCodes.CredentialNeedsReconnect;

    public IReadOnlyDictionary<string, object?>? Details => new Dictionary<string, object?> { ["credentialId"] = CredentialId, ["credentialName"] = DisplayName };

    public Guid CredentialId { get; }

    public string DisplayName { get; }

    /// <summary>What the provider said or what was recorded when the credential was marked, e.g. <c>invalid_grant</c> — never a secret.</summary>
    public string Reason { get; }

    public CredentialNeedsReconnectException(Guid credentialId, string displayName, string reason, Exception? inner = null)
        : base($"The credential '{displayName}' ({credentialId}) cannot authenticate: its provider refused it ({reason}). It needs to be reconnected — its owner reconnects the provider (or adds the token again), or a team Admin re-links the repository to a live credential of the same provider. Until then nothing bound through it can authenticate.", inner)
    {
        CredentialId = credentialId;
        DisplayName = displayName;
        Reason = reason;
    }
}
