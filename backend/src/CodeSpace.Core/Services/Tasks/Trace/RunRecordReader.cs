using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Services.Workflows;
using CodeSpace.Messages.Tasks.Trace;
using Microsoft.EntityFrameworkCore;

namespace CodeSpace.Core.Services.Tasks.Trace;

/// <summary>
/// The Trace reader — team-prechecks the run (via <see cref="IWorkflowService.GetRunAsync"/>, fail-closed) then dumps
/// its <c>workflow_run_record</c> ledger UNFILTERED in Sequence order. The records carry no TeamId of their own, so the
/// run precheck IS the tenancy boundary (a record is the team's iff its run is) — the same model the narrative
/// run-record source uses (read by RunId after the projector's precheck). READ-ONLY — no schema, no engine mutation.
/// </summary>
public sealed class RunRecordReader : IRunRecordReader, IScopedDependency
{
    private readonly IWorkflowService _workflows;
    private readonly CodeSpaceDbContext _db;

    public RunRecordReader(IWorkflowService workflows, CodeSpaceDbContext db)
    {
        _workflows = workflows;
        _db = db;
    }

    public async Task<RunRecordsResponse?> ReadAsync(Guid runId, Guid teamId, CancellationToken cancellationToken)
    {
        var run = await _workflows.GetRunAsync(runId, teamId, cancellationToken).ConfigureAwait(false);

        if (run == null) return null;

        var records = await LoadRecordsAsync(runId, cancellationToken).ConfigureAwait(false);

        return new RunRecordsResponse { RunId = runId, RunStatus = run.Status.ToString(), Records = records };
    }

    /// <summary>Every ledger row for the run, in per-run Sequence order — the raw audit tape. By RunId only; the team boundary is the run precheck above (a record is the team's iff its run is).</summary>
    private async Task<IReadOnlyList<RunRecordView>> LoadRecordsAsync(Guid runId, CancellationToken cancellationToken) =>
        await _db.WorkflowRunRecord.AsNoTracking()
            .Where(r => r.RunId == runId)
            .OrderBy(r => r.Sequence)
            .Select(r => new RunRecordView
            {
                Sequence = r.Sequence,
                RecordType = r.RecordType,
                NodeId = r.NodeId,
                IterationKey = r.IterationKey,
                OccurredAt = r.OccurredAt,
                PayloadJson = r.PayloadJson,
                CorrelationId = r.CorrelationId,
                ParentRecordId = r.ParentRecordId,
            })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
}
