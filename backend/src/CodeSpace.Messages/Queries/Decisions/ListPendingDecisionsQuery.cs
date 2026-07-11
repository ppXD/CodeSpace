using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Dtos.Decisions;
using CodeSpace.Messages.Mediation;

namespace CodeSpace.Messages.Queries.Decisions;

/// <summary>
/// The team's cross-grain "Needs decision" queue (Decision substrate D3): every PENDING decision the team owns —
/// an <c>agent.run</c> mid-run <c>decision.request</c> AND a <c>flow.decision</c> node wait, unified — soonest-deadline
/// first. Team-scoped: the team comes from <c>ICurrentTeam</c> (the <c>X-Team-Id</c> header), never the wire
/// (<see cref="IRequireTeamMembership"/>), so a caller only ever sees its own team's decisions.
/// </summary>
public sealed record ListPendingDecisionsQuery : IQuery<IReadOnlyList<PendingDecision>>, IRequireTeamMembership;
