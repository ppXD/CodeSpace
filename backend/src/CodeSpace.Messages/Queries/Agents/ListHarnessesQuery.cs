using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Dtos.Agents;
using CodeSpace.Messages.Mediation;

namespace CodeSpace.Messages.Queries.Agents;

/// <summary>
/// Every agent harness registered in the engine — feeds the editor's harness picker on the
/// <c>agent.code</c> node and the model suggestions for the chosen harness. Deployment-level and
/// team-agnostic, so it needs only an authenticated caller (no team membership).
/// </summary>
public sealed record ListHarnessesQuery : IQuery<IReadOnlyList<HarnessSummary>>, IRequireAuthenticatedUser
{
}
