using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Dtos.Providers;
using CodeSpace.Messages.Mediation;

namespace CodeSpace.Messages.Queries.Repositories;

/// <summary>Activity-timeline events on an issue, oldest-first. Membership enforced via <see cref="IRequireRepositoryAccess"/>.</summary>
public sealed record ListIssueEventsQuery : IQuery<IReadOnlyList<RemoteIssueEvent>>, IRequireRepositoryAccess
{
    public required Guid RepositoryId { get; init; }
    public required int Number { get; init; }
}
