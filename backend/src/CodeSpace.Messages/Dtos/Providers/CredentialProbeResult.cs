namespace CodeSpace.Messages.Dtos.Providers;

public sealed record CredentialProbeResult
{
    public required bool IsValid { get; init; }
    public string? Error { get; init; }
    public IReadOnlyList<string>? GrantedScopes { get; init; }
    public string? AuthenticatedUserExternalId { get; init; }
    public string? AuthenticatedUserName { get; init; }
}
