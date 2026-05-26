using CodeSpace.Messages.Enums;

namespace CodeSpace.Messages.Dtos.Repositories;

public sealed record RepositorySummary
{
    public required Guid Id { get; init; }
    public required Guid TeamId { get; init; }
    public required Guid ProviderInstanceId { get; init; }
    public Guid? CredentialId { get; init; }
    public required string FullPath { get; init; }
    public required string Name { get; init; }
    public required string DefaultBranch { get; init; }
    public required RepositoryVisibility Visibility { get; init; }
    public required RepositoryStatus Status { get; init; }

    /// <summary>
    /// Surfaced on the list response so the UI can show "Needs new credential" or other
    /// actionable hints inline with the row, without falling back to a per-row detail
    /// fetch. Populated when Status != Active.
    /// </summary>
    public string? LastError { get; init; }

    public required string WebUrl { get; init; }
    public DateTimeOffset? LastEventDate { get; init; }
    public required DateTimeOffset CreatedDate { get; init; }
}
