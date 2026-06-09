using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Dtos.Providers;
using CodeSpace.Messages.Mediation;

namespace CodeSpace.Messages.Queries.Repositories;

/// <summary>
/// Live-fetch a single file's content for the Code browser's viewer. <see cref="Ref"/> null/empty ⇒ the
/// repo's default branch. Binary or oversized files come back flagged (not inlined) on the DTO.
/// </summary>
public sealed record GetRepositoryFileQuery : IQuery<RemoteFileContent>, IRequireRepositoryAccess
{
    public required Guid RepositoryId { get; init; }

    /// <summary>Repo-root-relative file path.</summary>
    public required string Path { get; init; }

    /// <summary>Branch, tag, or commit SHA. Null/empty uses the repo's default branch.</summary>
    public string? Ref { get; init; }
}
