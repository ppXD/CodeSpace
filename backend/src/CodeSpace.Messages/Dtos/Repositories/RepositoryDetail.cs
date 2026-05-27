using CodeSpace.Messages.Dtos.Projects;
using CodeSpace.Messages.Enums;

namespace CodeSpace.Messages.Dtos.Repositories;

public sealed record RepositoryDetail
{
    public required Guid Id { get; init; }
    public required Guid TeamId { get; init; }
    public required Guid ProviderInstanceId { get; init; }
    public Guid? CredentialId { get; init; }

    /// <summary>
    /// Phase 3.1 — every Project this repository is actively linked to. A repository may
    /// belong to multiple projects (shared library across squads, monorepo carving).
    /// Empty when no project links exist (transient state allowed by the post-3.1 schema).
    /// </summary>
    public required IReadOnlyList<ProjectRef> Projects { get; init; }

    /// <summary>
    /// Legacy "primary" project — first active link by ascending CreatedDate. Nullable
    /// because a repo may have zero project links after the 0026 schema change. Kept on
    /// the DTO for backward compat with the SPA's repo-detail breadcrumb; new code should
    /// iterate <see cref="Projects"/> directly.
    /// </summary>
    public Guid? ProjectId { get; init; }
    public string? ProjectSlug { get; init; }
    public string? ProjectName { get; init; }
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
