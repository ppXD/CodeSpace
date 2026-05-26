using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Mediation;

namespace CodeSpace.Messages.Commands.Credentials;

/// <summary>
/// Revokes a credential. For OAuth credentials, makes a best-effort call to the provider's
/// revocation endpoint (RFC 7009 for GitLab, GitHub's grant API for GitHub). Regardless of
/// provider outcome, the local credential is marked Revoked and its encrypted payload
/// cleared — once we drop the payload, nothing in CodeSpace can use the token even if the
/// provider call failed.
/// </summary>
public sealed record RevokeCredentialCommand : ICommand<RevokeCredentialResult>, IRequireCredentialAccess
{
    public required Guid CredentialId { get; init; }
}

public sealed record RevokeCredentialResult
{
    public required Guid CredentialId { get; init; }

    /// <summary>True when the provider acknowledged revocation. False when it failed (still safe — local payload was cleared).</summary>
    public required bool ProviderAcknowledged { get; init; }

    /// <summary>Set when ProviderAcknowledged is false — describes why for operator diagnostics.</summary>
    public string? ProviderError { get; init; }

    /// <summary>
    /// Repositories that were bound through this credential and have just been flipped to
    /// Status=Error with a "re-link or unbind" remediation message. Lets the UI confirm
    /// the impact ("3 repositories now need a new credential") right after disconnect.
    /// Zero when the credential wasn't tied to any active repo — disconnect is silent.
    /// </summary>
    public int AffectedRepositoryCount { get; init; }
}
