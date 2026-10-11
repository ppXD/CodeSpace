using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Mediation;
using CodeSpace.Messages.Tasks.Trace;

namespace CodeSpace.Messages.Queries.Tasks;

/// <summary>
/// The live tail of one run's raw ledger — every record beyond <see cref="AfterSequence"/> as it lands, ending at a
/// terminal run record. Team-scoped (<see cref="IRequireTeamMembership"/>): the membership behavior runs when this is
/// SENT, and the tail is lazy, so a caller without standing on the <c>X-Team-Id</c> team is refused before anything is
/// enumerated — the SSE action can still answer a real 403 because no header has been written yet. The tail then
/// re-asks the same question before handing out each poll's rows, and ends once the answer turns; it also ends, idle or
/// not, once the caller's token would no longer be accepted at the door. A foreign / absent run under a team the caller
/// does belong to tails nothing (no existence leak).
/// </summary>
public sealed record StreamRunRecordsQuery : IQuery<IAsyncEnumerable<RunRecordView>>, IRequireTeamMembership
{
    public required Guid RunId { get; init; }

    /// <summary>Exclusive cursor — the last Sequence the client already holds (SSE <c>?after=</c> / Last-Event-ID resume).</summary>
    public long AfterSequence { get; init; }
}
