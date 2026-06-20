using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Dtos.Providers;
using CodeSpace.Messages.Mediation;

namespace CodeSpace.Messages.Queries.Repositories;

/// <summary>Single issue with body + sidebar fields for the in-app detail view. Membership enforced via <see cref="IRequireRepositoryAccess"/>.</summary>
public sealed record GetIssueQuery : IQuery<RemoteIssue>, IRequireRepositoryAccess
{
    public required Guid RepositoryId { get; init; }
    public required int Number { get; init; }
}
