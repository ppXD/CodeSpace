using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Dtos.Providers;
using CodeSpace.Messages.Mediation;

namespace CodeSpace.Messages.Queries.Repositories;

/// <summary>
/// Latest commit on a path/ref — the Code tab's header bar. <see cref="Path"/> null/empty ⇒ repo root;
/// <see cref="Ref"/> null/empty ⇒ default branch. Null result when the path has no history.
/// </summary>
public sealed record GetRepositoryLatestCommitQuery : IQuery<RemoteCommitSummary?>, IRequireRepositoryAccess
{
    public required Guid RepositoryId { get; init; }
    public string? Path { get; init; }
    public string? Ref { get; init; }
}
