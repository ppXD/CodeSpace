using CodeSpace.Messages.Enums;

namespace CodeSpace.Messages.Dtos.Providers;

public sealed record RemoteRepository
{
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
}
