using CodeSpace.Messages.Enums;

namespace CodeSpace.Messages.Dtos.Credentials;

public sealed record CredentialSummary
{
    public required Guid Id { get; init; }
    public required Guid TeamId { get; init; }
    public required Guid ProviderInstanceId { get; init; }
    public Guid? OwnerUserId { get; init; }

    /// <summary>
    /// The display name of the credential's owner — surfaced here so the Add Repository
    /// picker can show "alice's GitHub · alice" instead of two cards distinguishable only
    /// by user-supplied display name (which can collide after renames). Null only for
    /// team-shared credentials with no owner.
    /// </summary>
    public string? OwnerUserName { get; init; }

    /// <summary>Personal (one user's) vs TeamService (team-owned, no person). The UI labels + sorts
    /// by this, and prefers a TeamService credential when binding repositories.</summary>
    public required CredentialOwnership Ownership { get; init; }

    public required AuthType AuthType { get; init; }
    public required string DisplayName { get; init; }
    public required CredentialStatus Status { get; init; }
    public DateTimeOffset? ExpiresDate { get; init; }
    public DateTimeOffset? LastValidatedDate { get; init; }
    public string? LastError { get; init; }
    public required DateTimeOffset CreatedDate { get; init; }

    /// <summary>What to read off the credential — derived from its status, a static token's expiry and its owner's standing; see <see cref="CredentialHealth"/>.</summary>
    public CredentialHealth Health { get; init; } = CredentialHealth.Active;

    /// <summary>Whole days until a static token expires at its provider; null for an OAuth credential (its access token renews itself) and for a token with no recorded expiry.</summary>
    public int? DaysToExpiry { get; init; }

    /// <summary>
    /// Whether the person asking may put this credential right IN PLACE — reconnect its sign-in, or replace its pasted token — decided by the rules the
    /// endpoints behind those buttons apply, so a client shows the button exactly when the endpoint would accept the request: the credential's own person,
    /// or <c>credentials.manage</c>, and for a sign-in also the reach over its person (an Admin cannot reconnect another Admin's or an Owner's). False when
    /// its person has left the team. Null from a server that predates it: a client then falls back to what it can tell from the role.
    /// </summary>
    public bool? CallerMayRepair { get; init; }

    /// <summary>How many live repositories are bound through the credential — what a person reconnecting, replacing or disconnecting it needs to know is at stake. Zero from a server that predates it.</summary>
    public int BoundRepositoryCount { get; init; }

    /// <summary>
    /// How many things lean on the credential: the live repositories bound through it, the group hooks registered through it that are still in service, and the act-as identities it backs — the three a Disconnect leaves behind or a
    /// removal would strand (<c>GET /credentials/{id}/dependents</c> lists them). A disconnected credential with none is history, which a client may
    /// fold away and offer to remove (<c>RemoveDisconnectedCredentialCommand</c>). Null from a server that predates it, which is also the sign that it has no such removal.
    /// </summary>
    public int? DependentCount { get; init; }

    /// <summary>How many of <see cref="DependentCount"/> are group hooks registered through the credential that are still in service — what a dead credential with no repository left on it is still held by, and what <c>relink-bindings</c> moves with the repositories. Zero from a server that predates it.</summary>
    public int BoundConnectionWebhookCount { get; init; }

    /// <summary>How many of <see cref="DependentCount"/> are live act-as identities the credential backs. <c>relink-bindings</c> does not move them: they end with the credential's own disconnect or are unlinked. Zero from a server that predates it.</summary>
    public int ActAsIdentityCount { get; init; }
}
