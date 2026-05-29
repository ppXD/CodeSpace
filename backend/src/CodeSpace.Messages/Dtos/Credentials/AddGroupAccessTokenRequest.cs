namespace CodeSpace.Messages.Dtos.Credentials;

/// <summary>
/// Wire shape for adding a GitLab Group Access Token as a TEAM-SERVICE credential — the group token
/// is owned by the group, not a person, so a repo bound through it survives anyone leaving. Mirrors
/// <see cref="AddPatCredentialRequest"/> (the Rule 17 exception: a flat <c>token</c> string maps to a
/// structured payload in the controller). No owner field — team-service credentials have no person.
/// </summary>
public sealed record AddGroupAccessTokenRequest
{
    public required Guid ProviderInstanceId { get; init; }
    public required string DisplayName { get; init; }
    public required string Token { get; init; }
}
