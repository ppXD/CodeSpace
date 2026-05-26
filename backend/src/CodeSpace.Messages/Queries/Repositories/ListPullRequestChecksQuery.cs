using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Dtos.Providers;
using CodeSpace.Messages.Mediation;

namespace CodeSpace.Messages.Queries.Repositories;

/// <summary>
/// Live-fetch the CI/check list for a PR's HEAD commit. Each provider has its own
/// state machine (GitHub: check_run status+conclusion; GitLab: pipeline-job status) —
/// the response normalises both onto <see cref="RemotePullRequestCheck"/> so the SPA
/// renders one row per check with a single status icon.
/// </summary>
public sealed record ListPullRequestChecksQuery : IQuery<IReadOnlyList<RemotePullRequestCheck>>, IRequireRepositoryAccess
{
    public required Guid RepositoryId { get; init; }

    /// <summary>Per-repo number (`#42` on GitHub, `!42` on GitLab) — not the global ExternalId.</summary>
    public required int Number { get; init; }
}
