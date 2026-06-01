namespace CodeSpace.Core.Persistence.Entities;

/// <summary>
/// A CodeSpace user's OWN identity on a provider instance (Model B). Maps a user to the GitHub/GitLab
/// account they linked, referencing the <see cref="Credential"/> that holds the token — so attributable
/// WRITE operations (approve / request-changes / comment, and future merge / issue) can act AS the
/// human, not as the repository's shared connection credential.
///
/// Distinct from <see cref="Credential"/> (which owns the token + encryption / scope / status): this is
/// the first-class user↔provider mapping that also captures the provider-SIDE profile
/// (<see cref="ProviderUsername"/> / <see cref="ProviderUserId"/> / <see cref="AvatarUrl"/>) for real
/// attribution + display. There is intentionally NO status column here — validity tracks the linked
/// credential's <c>Status</c>, so the two can never drift.
/// </summary>
public class UserProviderIdentity : IEntity<Guid>, IAuditable
{
    public Guid Id { get; set; }

    public Guid UserId { get; set; }
    public Guid ProviderInstanceId { get; set; }
    public Guid CredentialId { get; set; }

    /// <summary>Provider-side stable id (e.g. GitHub user id "12345"). String to stay provider-neutral.</summary>
    public string ProviderUserId { get; set; } = default!;

    /// <summary>Provider-side handle (e.g. "alice") — what the provider attributes the write to.</summary>
    public string ProviderUsername { get; set; } = default!;

    public string? AvatarUrl { get; set; }

    public DateTimeOffset CreatedDate { get; set; }
    public Guid CreatedBy { get; set; }
    public DateTimeOffset LastModifiedDate { get; set; }
    public Guid LastModifiedBy { get; set; }
    public DateTimeOffset? DeletedDate { get; set; }

    public ProviderInstance ProviderInstance { get; set; } = default!;
    public Credential Credential { get; set; } = default!;
}
