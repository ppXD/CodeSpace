using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Mediation;
using CodeSpace.Messages.Tasks.Timeline;

namespace CodeSpace.Messages.Queries.Tasks;

/// <summary>
/// The Activity narrative timeline for one run. Team-scoped — the team comes from <c>ICurrentTeam</c> (the
/// X-Team-Id header), never the wire (<see cref="IRequireTeamMembership"/>), so a caller only ever projects its own
/// runs. A foreign / absent run resolves to <c>null</c> (the handler / controller 404-conflates — existence is
/// never leaked). Read-only.
/// </summary>
public sealed record GetRunTimelineQuery : IQuery<RunTimelineResponse?>, IRequireTeamMembership
{
    public required Guid RunId { get; init; }
}
