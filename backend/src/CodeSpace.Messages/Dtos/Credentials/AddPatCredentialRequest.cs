namespace CodeSpace.Messages.Dtos.Credentials;

public sealed record AddPatCredentialRequest
{
    public required Guid ProviderInstanceId { get; init; }
    public Guid? OwnerUserId { get; init; }
    public required string DisplayName { get; init; }
    public required string Token { get; init; }
}
