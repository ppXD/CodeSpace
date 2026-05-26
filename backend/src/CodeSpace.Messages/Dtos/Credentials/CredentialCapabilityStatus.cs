namespace CodeSpace.Messages.Dtos.Credentials;

/// <summary>
/// Per-capability availability snapshot for one credential. Drives the UI's per-row badges
/// ("✓ Read · ⚠ Webhooks needs `admin:repo_hook`"). The frontend renders one of these for
/// every capability the provider declares — there is no separate "is this provider's webhook
/// capability supported by us" check; that's already covered by the provider module.
/// </summary>
public sealed record CredentialCapabilityStatus
{
    public required string Capability { get; init; }
    public required bool IsAvailable { get; init; }
    public IReadOnlyList<string> MissingScopes { get; init; } = Array.Empty<string>();
}

public sealed record CredentialCapabilitiesResponse
{
    public required Guid CredentialId { get; init; }
    public required IReadOnlyList<string> GrantedScopes { get; init; }
    public required IReadOnlyList<CredentialCapabilityStatus> Capabilities { get; init; }
}
