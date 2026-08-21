using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Mediation;
using CodeSpace.Messages.Tasks.Trace;

namespace CodeSpace.Messages.Queries.Tasks;

/// <summary>Read one bounded UTF-8 byte range of one exact Workflow Run ledger record's canonical JSONB text.</summary>
public sealed record ReadRunRecordPayloadRangeQuery : IQuery<RunRecordPayloadRangeRead?>, IRequireTeamMembership
{
    public required Guid RunId { get; init; }
    public required Guid RecordId { get; init; }
    public long OffsetBytes { get; init; }
    public int LimitBytes { get; init; } = 64 * 1024;
}
