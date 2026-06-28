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

    /// <summary>The run's ledger records in ledger order (Sequence). The run is already team-checked by the projector, so reading by RunId is in-scope. AsNoTracking — pure read.</summary>
    private async Task<List<WorkflowRunRecord>> LoadRecordsAsync(Guid runId, CancellationToken cancellationToken) =>
        await _db.WorkflowRunRecord.AsNoTracking()
            .Where(r => r.RunId == runId)
            .OrderBy(r => r.Sequence)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
}
