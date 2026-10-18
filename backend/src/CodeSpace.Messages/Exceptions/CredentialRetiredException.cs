using CodeSpace.Messages.Failures;

namespace CodeSpace.Messages.Exceptions;

/// <summary>
/// Thrown when a credential is asked to take a new authorization in place after it was retired — soft-deleted when its
/// person left the team or its connection was removed, or held by someone who no longer stands in the team and so about to
/// be. A retired credential keeps no token and nothing is allowed to bring it back: it would be a live token of someone
/// without standing, which the departed-token sweep then retires again together with whatever it had just taken in. The
/// remedy is a new connection.
/// </summary>
public sealed class CredentialRetiredException : Exception, IFailure
{
    public FailureKind Kind => FailureKind.Conflict;

    public string Code => FailureCodes.CredentialRetired;

    public IReadOnlyDictionary<string, object?>? Details => new Dictionary<string, object?> { ["credentialId"] = CredentialId };

    public Guid CredentialId { get; }

    public CredentialRetiredException(Guid credentialId, string displayName, string reason)
        : base($"The credential '{displayName}' cannot be re-authorized: {reason} Connect the provider again to create a new credential.")
    {
        CredentialId = credentialId;
    }

    /// <summary>The row is soft-deleted: the retirement itself, as opposed to one that is about to happen.</summary>
    public static CredentialRetiredException SoftDeleted(Guid credentialId, string displayName) =>
        new(credentialId, displayName, "its connection was removed or its person left the team, and a retired credential stays retired.");
}
