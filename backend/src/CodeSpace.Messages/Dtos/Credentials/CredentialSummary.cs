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

    /// <summary>How many live repositories are bound through the credential — what a person reconnecting, replacing or disconnecting it needs to know is at stake. Zero from a server that predates it.</summary>
    public int BoundRepositoryCount { get; init; }
}
