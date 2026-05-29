using CodeSpace.Messages.Enums;

namespace CodeSpace.Messages.Credentials;

/// <summary>
/// Inputs for creating a credential — consolidated into one record so the service method stays
/// within the parameter cap (and so adding fields like <see cref="Ownership"/> is non-breaking).
/// </summary>
public sealed record AddCredentialInput
{
    public required Guid ProviderInstanceId { get; init; }

    /// <summary>The owning user for a Personal credential; ignored (forced null) for TeamService.</summary>
    public Guid? OwnerUserId { get; init; }

    public required string DisplayName { get; init; }
    public required CredentialPayload Payload { get; init; }

    public CredentialOwnership Ownership { get; init; } = CredentialOwnership.Personal;
}
