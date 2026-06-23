using CodeSpace.Messages.Tasks.Trace;

namespace CodeSpace.Core.Services.Tasks.Trace;

/// <summary>
/// Reads a run's RAW append-only event ledger (<c>workflow_run_record</c>) for the Trace tab — the unfiltered audit
/// counterpart to <c>IRunTimelineProjector</c> (which filters to the narrative). Team-scoped + fail-closed: a foreign /
/// absent run returns <c>null</c> (404-conflate, no existence leak). Read-only.
/// </summary>
public interface IRunRecordReader
{
    Task<RunRecordsResponse?> ReadAsync(Guid runId, Guid teamId, CancellationToken cancellationToken);
}
