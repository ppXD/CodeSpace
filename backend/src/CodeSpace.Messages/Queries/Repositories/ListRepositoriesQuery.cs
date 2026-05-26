using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Dtos.Repositories;
using CodeSpace.Messages.Mediation;

namespace CodeSpace.Messages.Queries.Repositories;

public sealed record ListRepositoriesQuery : IQuery<IReadOnlyList<RepositorySummary>>, IRequireTeamMembership
{
    public Guid? ProviderInstanceId { get; init; }
}
