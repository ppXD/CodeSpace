using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Dtos.Providers;
using CodeSpace.Messages.Mediation;

namespace CodeSpace.Messages.Queries.Repositories;

/// <summary>
/// Live-fetch a single PR/MR by its per-repo number. Returns the full detail shape —
/// body + diff stats — populated for the in-app PR detail view.
/// </summary>
public sealed record GetPullRequestQuery : IQuery<RemotePullRequest>, IRequireRepositoryAccess
{
    public required Guid RepositoryId { get; init; }

    /// <summary>Per-repo number (`#42` on GitHub, `!42` on GitLab) — not the global ExternalId.</summary>
    public required int Number { get; init; }
}
