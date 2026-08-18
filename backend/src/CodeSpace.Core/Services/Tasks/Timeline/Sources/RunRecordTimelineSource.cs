using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Messages.Tasks.Timeline;
using Microsoft.EntityFrameworkCore;

namespace CodeSpace.Core.Services.Tasks.Timeline.Sources;

/// <summary>
/// The run/node LIFECYCLE timeline source — it reads the append-only <c>workflow_run_record</c> ledger for the run
/// (already tenancy-checked by the projector) and projects the NARRATIVE-worthy lifecycle records (run + node
/// started/completed/failed/suspended/skipped, retries) into timeline events via <see cref="RunRecordTimelineMap"/>.
/// Trace-level noise (release/scope/variables snapshots, log lines, iteration + external-call detail) is dropped —
/// this is the human story line, not the audit. Universal: every run, of any shape, has these records. READ-ONLY.
/// </summary>
public sealed class RunRecordTimelineSource : IRunTimelineSource, IScopedDependency
{
    private readonly CodeSpaceDbContext _db;

    public RunRecordTimelineSource(CodeSpaceDbContext db) { _db = db; }

    public string SourceKey => RunRecordTimelineMap.Key;

    public async Task<IReadOnlyList<RunTimelineEvent>> ContributeAsync(RunTimelineContext context, CancellationToken cancellationToken)
    {
        var records = await LoadRecordsAsync(context.RunId, cancellationToken).ConfigureAwait(false);

        // Project (not a bare per-record map) so the durable-RESUME mechanics fold: only the first RunStarted is a
        // milestone, every later RunStarted + all RunReplayed become foldable Detail (see RunRecordTimelineMap.Project).
        return RunRecordTimelineMap.Project(records);
    }

    private async Task<List<WorkflowRunRecord>> LoadRecordsAsync(Guid runId, CancellationToken cancellationToken) =>
        await NarrativeRecordsQuery(_db, runId).ToListAsync(cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// The run's NARRATIVE ledger records in ledger order (Sequence). The record-type predicate is pushed into SQL from
    /// <see cref="RunRecordTimelineMap.NarrativeRecordTypes"/> — the map's OWN derived set, so the filter and the switch
    /// cannot disagree — and it is index-compatible with <c>idx_wrr_run_type(run_id, record_type)</c>. Trace-level bulk
    /// (a streamed run's per-second <c>interaction.delta</c> rows, log lines, scope / release snapshots, external-call
    /// detail) therefore never crosses the wire; the drop used to happen in C# AFTER every row and its
    /// <c>payload_json</c> had been materialized, once per turn, per 2s poll, per viewer. Only the columns
    /// <see cref="RunRecordTimelineMap.ToEvent"/> actually reads are selected — id / run_id / correlation_id /
    /// parent_record_id stay in the database. Internal (not private) so the pushed-down SQL is pinned directly.
    /// The run is already team-checked by the projector, so reading by RunId is in-scope. AsNoTracking — pure read.
    /// </summary>
    internal static IQueryable<WorkflowRunRecord> NarrativeRecordsQuery(CodeSpaceDbContext db, Guid runId) =>
        db.WorkflowRunRecord.AsNoTracking()
            .Where(r => r.RunId == runId && RunRecordTimelineMap.NarrativeRecordTypes.Contains(r.RecordType))
            .OrderBy(r => r.Sequence)
            .Select(r => new WorkflowRunRecord
            {
                Sequence = r.Sequence,
                RecordType = r.RecordType,
                NodeId = r.NodeId,
                IterationKey = r.IterationKey,
                OccurredAt = r.OccurredAt,
                PayloadJson = r.PayloadJson,
            });
}
