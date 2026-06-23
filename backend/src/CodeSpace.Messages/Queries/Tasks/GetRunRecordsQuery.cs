using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Mediation;
using CodeSpace.Messages.Tasks.Trace;

namespace CodeSpace.Messages.Queries.Tasks;

/// <summary>
/// The Trace audit — every raw ledger record for one run, in Sequence order. Team-scoped: the team comes from
/// <c>ICurrentTeam</c> (the X-Team-Id header), never the wire (<see cref="IRequireTeamMembership"/>). A foreign /
/// absent run resolves to <c>null</c> (the handler / controller 404-conflates — existence is never leaked). Read-only.
/// </summary>
public sealed record GetRunRecordsQuery : IQuery<RunRecordsResponse?>, IRequireTeamMembership
{
    public required Guid RunId { get; init; }
}
