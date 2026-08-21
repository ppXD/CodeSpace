using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Mediation;
using CodeSpace.Messages.Tasks.Trace;

namespace CodeSpace.Messages.Queries.Tasks;

/// <summary>
/// Bounded raw-ledger read. No cursor means the newest tail; BeforeSequence walks older history; AfterSequence reads
/// newly committed rows. The controller and reader both enforce the mutually-exclusive, positive/bounded contract.
/// </summary>
public sealed record GetRunRecordPageQuery : IQuery<RunRecordPageResponse?>, IRequireTeamMembership
{
    public required Guid RunId { get; init; }
    public long? BeforeSequence { get; init; }
    public long? AfterSequence { get; init; }
    public int Limit { get; init; } = RunRecordPageLimits.DefaultLimit;
}
