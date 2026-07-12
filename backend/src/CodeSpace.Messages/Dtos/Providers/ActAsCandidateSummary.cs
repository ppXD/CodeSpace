namespace CodeSpace.Messages.Dtos.Providers;

/// <summary>
/// A teammate who can be the AUTHOR of an attributable git write on a repository — one who has a live
/// linked GitHub/GitLab identity on that repo's provider instance, so acting AS them will resolve at
/// write time (never throws ActorIdentityRequiredException). Populates the actAsUserId picker so it
/// only offers usable authors. <see cref="UserId"/> is the value the field stores.
/// </summary>
public sealed record ActAsCandidateSummary
{
    public required Guid UserId { get; init; }
    public required string Name { get; init; }
    public required string Email { get; init; }
    /// <summary>The provider-side handle the write is attributed to (e.g. "alice").</summary>
    public required string ProviderUsername { get; init; }
    /// <summary>The provider-side stable id (e.g. GitHub "12345").</summary>
    public required string ProviderUserId { get; init; }
    /// <summary>The provider avatar, when known.</summary>
    public string? AvatarUrl { get; init; }
}
