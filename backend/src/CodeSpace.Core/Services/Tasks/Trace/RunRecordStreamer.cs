using System.Runtime.CompilerServices;
using CodeSpace.Core.Authorization;
using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Services.Identity;
using CodeSpace.Core.Services.Workflows.Display;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Tasks.Trace;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CodeSpace.Core.Services.Tasks.Trace;

/// <summary>
/// Tails a run's ledger by polling <c>workflow_run_record</c> for rows beyond the cursor on a short interval, with a
/// FRESH short-lived DbContext PER POLL (never the request-scoped context — a long-lived tail must not pin one
/// connection / change-tracker for its whole lifetime). Team-prechecks the run ONCE via the narrow identity/status bundle
/// (the same tenancy boundary the snapshot reader uses — a record is the team's iff its run is); a foreign / absent run
/// yields nothing. Drains a backlog immediately (a full batch re-polls without delay); waits the interval only once
/// caught up. Ends at a terminal <c>run.*</c> record or on cancellation. The 2s Room poll stays the fallback, so nothing
/// breaks when this stream is unavailable. Since migration 0154, sequence is assigned behind a transaction-scoped
/// run gate, so advancing this run-local cursor cannot skip a lower value that becomes visible later.
/// <para>The run precheck trusts <c>X-Team-Id</c> as sent, so this is reached only through <c>StreamRunRecordsQuery</c>,
/// whose membership marker vets the caller's standing on that team before the tail is requested.</para>
/// <para>That vetting runs once, and a run waiting on a decision stays non-terminal for days. So every poll that found
/// rows re-asks <see cref="ITeamStandingCheck"/> before handing them out, and the tail ends without them once the caller
/// was removed, the team deleted, the session ended, or the token aged out. An empty poll asks only whether the token is
/// still accepted, which reads no database, so an idle tail also ends when the token does.</para>
/// </summary>
public sealed class RunRecordStreamer : IRunRecordStreamer, IScopedDependency
{
    private const int PollIntervalMs = 400;
    private const int BatchCap = 500;

    private readonly IWorkflowRunObservationIdentityBundle _identity;
    private readonly ICurrentTeam _currentTeam;
    private readonly IServiceScopeFactory _scopeFactory;

    public RunRecordStreamer(IWorkflowRunObservationIdentityBundle identity, ICurrentTeam currentTeam, IServiceScopeFactory scopeFactory)
    {
        _identity = identity;
        _currentTeam = currentTeam;
        _scopeFactory = scopeFactory;
    }

    public async IAsyncEnumerable<RunRecordView> TailAsync(Guid runId, long afterSequence, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (_currentTeam.Id is not { } teamId) yield break;   // not team-scoped ⇒ nothing to tail

        var run = await _identity.GetAsync(teamId, runId, cancellationToken).ConfigureAwait(false);
        if (run == null) yield break;   // foreign / absent ⇒ empty (no existence leak)

        var cursor = afterSequence;
        var terminal = false;

        while (!terminal && !cancellationToken.IsCancellationRequested)
        {
            var batch = await LoadAfterAsync(teamId, runId, cursor, cancellationToken).ConfigureAwait(false);

            if (batch == null) yield break;   // the caller lost standing since connect ⇒ end WITHOUT the rows just read

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

    /// <summary>
    /// One poll — a FRESH scope + DbContext so the tail never pins the request context for its whole lifetime. Rows are
    /// handed out only once the caller's standing has been re-asked in that same fresh scope, where nothing memoized at
    /// connect survives. The ask comes after the read, so every row handed out was committed before a moment the caller
    /// still held standing. An empty poll has nothing to hand out, so it asks only the database-free half — is the token
    /// still accepted — and an idle tail costs one read as before yet still ends at expiry. Returns null when standing
    /// has lapsed.
    /// </summary>
    private async Task<IReadOnlyList<RunRecordView>?> LoadAfterAsync(Guid teamId, Guid runId, long cursor, CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var standing = scope.ServiceProvider.GetRequiredService<ITeamStandingCheck>();

        var batch = await ReadBatchAsync(scope.ServiceProvider.GetRequiredService<CodeSpaceDbContext>(), runId, cursor, cancellationToken).ConfigureAwait(false);

        if (batch.Count == 0) return standing.WithinTokenLifetime() ? batch : null;

        var holds = await standing.HoldsAsync(teamId, cancellationToken).ConfigureAwait(false);

        return holds ? batch : null;
    }

    private static async Task<IReadOnlyList<RunRecordView>> ReadBatchAsync(CodeSpaceDbContext db, Guid runId, long cursor, CancellationToken cancellationToken)
    {
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
