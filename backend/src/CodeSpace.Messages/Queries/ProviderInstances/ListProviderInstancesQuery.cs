using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Dtos.ProviderInstances;
using CodeSpace.Messages.Mediation;

namespace CodeSpace.Messages.Queries.ProviderInstances;

public sealed record ListProviderInstancesQuery : IQuery<IReadOnlyList<ProviderInstanceSummary>>, IRequireTeamMembership
{
}
