using CodeSpace.Messages.Enums;

namespace CodeSpace.Messages.Dtos.Repositories;

public sealed record RepositoryDetail
{
    public required Guid Id { get; init; }
    public required Guid TeamId { get; init; }
    public required Guid ProviderInstanceId { get; init; }
    public Guid? CredentialId { get; init; }
    public required string ExternalId { get; init; }
    public required string NamespacePath { get; init; }
    public required string Name { get; init; }
    public required string FullPath { get; init; }
    public required string DefaultBranch { get; init; }
    public required RepositoryVisibility Visibility { get; init; }
    public string? Description { get; init; }
    public required string WebUrl { get; init; }
    public string? CloneUrlHttps { get; init; }
    public string? CloneUrlSsh { get; init; }
    public bool Archived { get; init; }
    public DateTimeOffset? LastSyncedDate { get; init; }
    public DateTimeOffset? LastEventDate { get; init; }
    public required RepositoryStatus Status { get; init; }
    public string? LastError { get; init; }
    public required DateTimeOffset CreatedDate { get; init; }
    public required int ActiveWebhooksCount { get; init; }
}
