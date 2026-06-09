using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Dtos.Providers;
using CodeSpace.Messages.Mediation;

namespace CodeSpace.Messages.Queries.Repositories;

/// <summary>
/// Per-entry last commit for a folder's children — the file list's last-commit column. Best-effort + capped
/// server-side; paths that fail are absent from the result map. <see cref="Ref"/> null/empty ⇒ default branch.
/// </summary>
public sealed record GetRepositoryTreeCommitsQuery : IQuery<IReadOnlyDictionary<string, RemoteCommitSummary>>, IRequireRepositoryAccess
{
    public required Guid RepositoryId { get; init; }

    /// <summary>Repo-root-relative entry paths to look up. Bound from repeated <c>?paths=</c> query params.</summary>
    public string[] Paths { get; init; } = Array.Empty<string>();

    /// <summary>Branch, tag, or commit SHA. Null/empty uses the repo's default branch.</summary>
    public string? Ref { get; init; }
}
