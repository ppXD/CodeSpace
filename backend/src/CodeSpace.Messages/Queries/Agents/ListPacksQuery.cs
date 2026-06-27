using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Dtos.Agents;
using MediatR;

namespace CodeSpace.Messages.Queries.Agents;

/// <summary>The team's imported packs (the Library's source categories) with their freshness + artifact counts. Team scope comes from the X-Team-Id header.</summary>
public sealed record ListPacksQuery : IRequest<IReadOnlyList<PackSummary>>, IRequireTeamMembership;
