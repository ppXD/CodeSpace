namespace CodeSpace.Core.Persistence.Entities;

public class User : IEntity<Guid>, IAuditable
{
    public Guid Id { get; set; }

    public string Email { get; set; } = default!;
    public string Name { get; set; } = default!;
    public string? AvatarUrl { get; set; }
    public string? PasswordHash { get; set; }

    /// <summary>
    /// When true, the user can sign in but must rotate the password before any other API
    /// call succeeds. Set by migration 0007 on the bootstrap admin; cleared by a
    /// successful ChangePasswordCommand.
    /// </summary>
    public bool PasswordMustChange { get; set; }

    /// <summary>
    /// True for a non-human identity — the per-team "CodeSpace" bot that authors workflow-posted
    /// messages (interactive review cards, standup digests, …). A bot has no password and never
    /// signs in; it exists so a workflow can post into chat with a stable, attributable author even
    /// when the run has no human actor (e.g. a PR-triggered run). One bot per team — see IChatBotService.
    /// </summary>
    public bool IsBot { get; set; }

    /// <summary>
    /// Set while the account is switched off. Reversible, and deliberately not <c>DeletedDate</c>:
    /// the rows this account authored stay attributable to a real user, which a soft delete would
    /// take away.
    /// </summary>
    public DateTimeOffset? DeactivatedAt { get; set; }

    /// <summary>
    /// Rotated whenever every existing session must stop working — a password change, a reset, a
    /// deactivation. The value rides in the JWT and is compared on every request, so one write
    /// invalidates every token minted before it, with no revocation list to store or sweep.
    /// </summary>
    public Guid SecurityStamp { get; set; } = Guid.NewGuid();

    /// <summary>SHA-256 of the outstanding reset token, or null. Never the token.</summary>
    public string? PasswordResetTokenHash { get; set; }

    public DateTimeOffset? PasswordResetExpiresAt { get; set; }

    public DateTimeOffset? LastLoginDate { get; set; }

    public DateTimeOffset CreatedDate { get; set; }
    public Guid CreatedBy { get; set; }
    public DateTimeOffset LastModifiedDate { get; set; }
    public Guid LastModifiedBy { get; set; }
    public DateTimeOffset? DeletedDate { get; set; }
}
