using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Dtos.Providers;
using CodeSpace.Messages.Mediation;

namespace CodeSpace.Messages.Queries.Repositories;

/// <summary>
/// Live-fetch one level of a repository's file tree for the Code browser. <see cref="Path"/> null/empty
/// ⇒ repo root; <see cref="Ref"/> null/empty ⇒ the repo's default branch. Non-recursive — the browser
/// lazy-loads each folder as the user drills in.
/// </summary>
public sealed record ListRepositoryTreeQuery : IQuery<IReadOnlyList<RemoteTreeEntry>>, IRequireRepositoryAccess
{
    public required Guid RepositoryId { get; init; }

    /// <summary>Repo-root-relative folder path. Null/empty lists the root.</summary>
    public string? Path { get; init; }

    /// <summary>Branch, tag, or commit SHA. Null/empty uses the repo's default branch.</summary>
    public string? Ref { get; init; }
}
