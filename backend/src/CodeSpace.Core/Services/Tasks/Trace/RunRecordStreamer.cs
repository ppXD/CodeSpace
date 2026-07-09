using System.Runtime.CompilerServices;
using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Services.Identity;
using CodeSpace.Core.Services.Workflows;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Tasks.Trace;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CodeSpace.Core.Services.Tasks.Trace;

/// <summary>
/// Tails a run's ledger by polling <c>workflow_run_record</c> for rows beyond the cursor on a short interval, with a
/// FRESH short-lived DbContext PER POLL (never the request-scoped context — a long-lived tail must not pin one
/// connection / change-tracker for its whole lifetime). Team-prechecks the run ONCE via <see cref="IWorkflowService"/>
/// (the same tenancy boundary the snapshot reader uses — a record is the team's iff its run is); a foreign / absent run
/// yields nothing. Drains a backlog immediately (a full batch re-polls without delay); waits the interval only once
/// caught up. Ends at a terminal <c>run.*</c> record or on cancellation. The 2s Room poll stays the fallback, so nothing
/// breaks when this stream is unavailable.
/// </summary>
public sealed class RunRecordStreamer : IRunRecordStreamer, IScopedDependency
{
    private const int PollIntervalMs = 400;
    private const int BatchCap = 500;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IWorkflowService _workflows;
    private readonly ICurrentTeam _currentTeam;

    public RunRecordStreamer(IServiceScopeFactory scopeFactory, IWorkflowService workflows, ICurrentTeam currentTeam)
    {
        _scopeFactory = scopeFactory;
        _workflows = workflows;
        _currentTeam = currentTeam;
    }

    public async IAsyncEnumerable<RunRecordView> TailAsync(Guid runId, long afterSequence, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (_currentTeam.Id is not { } teamId) yield break;   // not team-scoped ⇒ nothing to tail

        var run = await _workflows.GetRunAsync(runId, teamId, cancellationToken).ConfigureAwait(false);
        if (run == null) yield break;   // foreign / absent ⇒ empty (no existence leak)

        var cursor = afterSequence;
        var terminal = false;

        while (!terminal && !cancellationToken.IsCancellationRequested)
        {
            var batch = await LoadAfterAsync(runId, cursor, cancellationToken).ConfigureAwait(false);

            foreach (var record in batch)
            {
                cursor = record.Sequence;
                terminal = IsTerminal(record.RecordType);
                yield return record;
                if (terminal) break;
            }

            if (!terminal && batch.Count < BatchCap) await Task.Delay(PollIntervalMs, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>One poll — a FRESH scope + DbContext so the tail never pins the request context for its whole lifetime.</summary>
    private async Task<IReadOnlyList<RunRecordView>> LoadAfterAsync(Guid runId, long cursor, CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CodeSpaceDbContext>();

        return await db.WorkflowRunRecord.AsNoTracking()
            .Where(r => r.RunId == runId && r.Sequence > cursor)
            .OrderBy(r => r.Sequence)
            .Take(BatchCap)
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

    private static bool IsTerminal(string recordType) =>
        recordType is WorkflowRunRecordTypes.RunCompleted or WorkflowRunRecordTypes.RunFailed or WorkflowRunRecordTypes.RunCancelled;
}
