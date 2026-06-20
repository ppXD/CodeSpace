using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Dtos.Providers;
using CodeSpace.Messages.Mediation;

namespace CodeSpace.Messages.Queries.Repositories;

/// <summary>User comments on an issue (Conversation), oldest-first. Membership enforced via <see cref="IRequireRepositoryAccess"/>.</summary>
public sealed record ListIssueCommentsQuery : IQuery<IReadOnlyList<RemoteIssueComment>>, IRequireRepositoryAccess
{
    public required Guid RepositoryId { get; init; }
    public required int Number { get; init; }
}
