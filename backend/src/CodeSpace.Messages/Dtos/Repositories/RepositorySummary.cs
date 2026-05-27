using CodeSpace.Messages.Dtos.Projects;
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

    /// <summary>
    /// Phase 3.1 — every Project this repository is actively linked to (via
    /// <c>project_repository</c>). May be empty when no project owns the repo yet (the
    /// post-3.1 schema allows this transient state — operator attaches manually after
    /// migration 0026 wipes legacy links). Frontend chooses how to render — chip list,
    /// "primary" pick, etc.
    /// </summary>
    public IReadOnlyList<ProjectRef> Projects { get; init; } = Array.Empty<ProjectRef>();
}
