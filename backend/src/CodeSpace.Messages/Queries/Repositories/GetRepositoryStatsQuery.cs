using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Dtos.Providers;
using CodeSpace.Messages.Mediation;

namespace CodeSpace.Messages.Queries.Repositories;

/// <summary>
/// Live headline stats for the Code tab's right rail (stars / forks / counts / storage). Best-effort:
/// numbers the provider can't supply come back null. Membership enforced via <see cref="IRequireRepositoryAccess"/>.
/// </summary>
public sealed record GetRepositoryStatsQuery : IQuery<RemoteRepositoryStats>, IRequireRepositoryAccess
{
    public required Guid RepositoryId { get; init; }
}
