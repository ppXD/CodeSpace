using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Budget;
using CodeSpace.Messages.Mediation;

namespace CodeSpace.Messages.Queries.Budget;

/// <summary>The current team's effective cost cap, including a deployment fallback standing in for a missing row. Null when neither applies. Membership only — reads carry no permission.</summary>
public sealed record GetTeamCostCapQuery : IQuery<TeamCostCap?>, IRequireTeamMembership;
