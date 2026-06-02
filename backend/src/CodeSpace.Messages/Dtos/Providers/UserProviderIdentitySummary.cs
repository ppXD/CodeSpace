using CodeSpace.Messages.Enums;

namespace CodeSpace.Messages.Dtos.Providers;

/// <summary>
/// A CodeSpace user's linked provider identity (Model B), as surfaced to that user. Carries the
/// provider-side handle for display/attribution and the linked credential's status so the UI can
/// flag a re-link when the token expires/revokes. Never carries token material.
/// </summary>
public sealed record UserProviderIdentitySummary
{
    public required Guid Id { get; init; }
    public required Guid ProviderInstanceId { get; init; }
    public required ProviderKind Provider { get; init; }
    public required string ProviderUsername { get; init; }
    public required string ProviderUserId { get; init; }
    public string? AvatarUrl { get; init; }
    public required CredentialStatus CredentialStatus { get; init; }
    public required DateTimeOffset CreatedDate { get; init; }
}
