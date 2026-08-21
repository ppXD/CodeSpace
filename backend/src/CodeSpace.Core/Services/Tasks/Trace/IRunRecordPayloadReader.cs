using CodeSpace.Messages.Tasks.Trace;

namespace CodeSpace.Core.Services.Tasks.Trace;

public sealed record RunRecordPayloadRangeRequest(Guid RunId, Guid TeamId, Guid RecordId, long OffsetBytes, int LimitBytes);

/// <summary>Exact team/run/record-scoped bounded access to a record payload; never enumerates sibling payloads.</summary>
public interface IRunRecordPayloadReader
{
    Task<RunRecordPayloadRangeRead?> ReadAsync(RunRecordPayloadRangeRequest request, CancellationToken cancellationToken);
}
