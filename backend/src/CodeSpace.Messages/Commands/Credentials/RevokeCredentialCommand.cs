using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Mediation;

namespace CodeSpace.Messages.Commands.Credentials;

/// <summary>
/// Revokes a credential. For OAuth credentials, makes a best-effort call to the provider's
/// revocation endpoint (RFC 7009 for GitLab, GitHub's grant API for GitHub). Regardless of
/// provider outcome, the local credential is marked Revoked and its encrypted payload
/// cleared — once we drop the payload, nothing in CodeSpace can use the token even if the
/// provider call failed.
///
/// <para><see cref="IRequireCredentialManagement"/>, not the bare credential marker: that one proves only that the
/// credential is the caller's team's and that the caller is a member, so on its own any Viewer could disconnect any
/// team credential — the Owner's included — and break every repository bound through it. Now a member disconnects
/// only their own Personal credential; anything else takes <see cref="TeamPermissions.CredentialsManage"/>.</para>
/// </summary>
public sealed record RevokeCredentialCommand : ICommand<RevokeCredentialResult>, IRequireCredentialManagement
{
    /// <summary>Set by the controller from the route segment via `command with { CredentialId = ... }`. Non-required so System.Text.Json doesn't 400-fail when the optional body omits it (URL is authoritative).</summary>
    public Guid CredentialId { get; init; }

    /// <summary>
    /// Where the repositories and group hooks bound through this credential go, in the same unit of work as the
    /// disconnect — so there is no moment they sit on a revoked row. It must be a live credential of the same owner (a
    /// team-service credential's successor is another team-service credential) on the same provider instance, and its
    /// owner must still stand in the team; anything else is refused before the provider is asked to revoke anything.
    ///
    /// <para>Left out, the answer is the one reconnecting would give — but only for the person it would be given to. When
    /// the caller is the credential's owner, it is still connected, and they hold exactly one other live credential on the
    /// same instance, the bindings follow it, because connecting that one first and disconnecting this one second must end
    /// where disconnecting first and connecting second always did. In every other case nothing moves and the repositories
    /// turn Error, exactly as a Disconnect always did: someone else's (an Admin cutting off a teammate's token must not have
    /// the repository carried onto the teammate's other grant, with no way to say otherwise — naming the target is how an
    /// Admin moves it), one already disconnected (a retry is not a new choice), none to follow, or two or more to choose
    /// between — the ambiguity is the person's to resolve, here or with <c>RelinkCredentialBindingsCommand</c> after. A
    /// team-service credential never auto-moves: two team tokens are rarely interchangeable (a group token reaches only its
    /// group). <c>GET /credentials/{id}/usage</c> previews which of these a Disconnect will be.</para>
    /// </summary>
    public Guid? MoveBindingsToCredentialId { get; init; }
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

    /// <summary>Live repositories that followed the disconnect to another credential instead of turning Error. Zero when nothing moved.</summary>
    public int MovedRepositoryCount { get; init; }

    /// <summary>Group hooks that followed with them.</summary>
    public int MovedConnectionWebhookCount { get; init; }

    /// <summary>The credential the bindings now run through; null when nothing moved.</summary>
    public Guid? MovedToCredentialId { get; init; }
}
