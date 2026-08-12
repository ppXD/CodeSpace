namespace CodeSpace.Messages.Dtos.Users;

/// <summary>An account as the instance-admin screen lists it. Carries no secret and no token.</summary>
public sealed record AccountSummary
{
    public required Guid Id { get; init; }
    public required string Name { get; init; }
    public required string Email { get; init; }
    public required bool IsDeactivated { get; init; }
    public required bool PasswordMustChange { get; init; }
    public DateTimeOffset? LastLoginDate { get; init; }
}

/// <summary>The one and only time a reset link is readable. Only its digest is stored.</summary>
public sealed record PasswordResetLink
{
    public required string ResetUrl { get; init; }
    public required DateTimeOffset ExpiresAt { get; init; }
}
