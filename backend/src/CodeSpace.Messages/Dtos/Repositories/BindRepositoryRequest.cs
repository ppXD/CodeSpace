namespace CodeSpace.Messages.Dtos.Repositories;

public sealed record BindRepositoryRequest
{
    public required Guid TeamId { get; init; }
    public required Guid ProviderInstanceId { get; init; }
    public required Guid CredentialId { get; init; }
    public required string ProjectIdentifier { get; init; }
}
