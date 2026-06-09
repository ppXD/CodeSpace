using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CodeSpace.Core.Services.Agents;

/// <summary>
/// Recovers agent runs orphaned by a crashed worker / killed pod / rolling update — the "no-stuck-run"
/// guarantee for agents, mirroring the workflow engine's <c>StuckRunReconcilerService</c>. A run whose
/// worker vanished sits in <see cref="AgentRunStatus.Running"/> forever without this sweep; here it's
/// flipped to <see cref="AgentRunStatus.Failed"/> with an "abandoned" reason so the operator sees what
/// happened and can re-run.
///
/// <para>Liveness uses TWO signals (stronger than the workflow's ledger-only heuristic): the dedicated
/// <see cref="AgentRun.HeartbeatAt"/> ping AND live event activity. A run is abandoned only when BOTH
/// are quiet past the window — so a streaming agent that's still emitting events is never wrongly
/// killed even if its worker skipped a heartbeat.</para>
///
/// <para>Every transition is an atomic CAS (<c>WHERE status = Running</c>), so it's idempotent and safe
/// to run from multiple replicas, and it never tramples a worker that's completing the run right now.</para>
/// </summary>
public interface IAgentRunReconcilerService
{
    Task<AgentRunReconcileSummary> ReconcileAsync(CancellationToken cancellationToken);
}

public sealed class AgentRunReconcilerService : IAgentRunReconcilerService, IScopedDependency
{
    /// <summary>Operators tune reclaim aggressiveness via this env var (a TimeSpan, e.g. "00:05:00"); default 5 min. Pinned by a test (Rule 8).</summary>
    public const string LivenessWindowEnvVar = "CODESPACE_AGENT_RUN_LIVENESS_WINDOW";

    private static readonly TimeSpan DefaultLivenessWindow = TimeSpan.FromMinutes(5);

    /// <summary>A Running run with no heartbeat AND no event activity within this window is treated as abandoned. The worker should heartbeat well inside it.</summary>
    private static TimeSpan LivenessWindow =>
        TimeSpan.TryParse(System.Environment.GetEnvironmentVariable(LivenessWindowEnvVar), out var window) ? window : DefaultLivenessWindow;

    /// <summary>Per-sweep cap so a backlog can't run a single tick forever.</summary>
    public const int BatchSize = 50;

    /// <summary>Operator-facing reason stamped on a reconciled run + appended to its log.</summary>
    public const string AbandonedError =
        "Agent run marked abandoned by the reconciler — the worker crashed or hung with no heartbeat or " +
        "event activity past the liveness window. Re-run the agent to retry; an interrupted run's " +
        "in-progress work is not resumed.";

    private readonly CodeSpaceDbContext _db;
    private readonly ILogger<AgentRunReconcilerService> _logger;

    public AgentRunReconcilerService(CodeSpaceDbContext db, ILogger<AgentRunReconcilerService> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<AgentRunReconcileSummary> ReconcileAsync(CancellationToken cancellationToken)
    {
        var marked = await MarkAbandonedRunningAsync(cancellationToken).ConfigureAwait(false);

        if (marked > 0)
            _logger.LogInformation("AgentRunReconciler: marked {Abandoned} abandoned agent run(s) failed", marked);

        return new AgentRunReconcileSummary { MarkedAbandonedFromRunning = marked };
    }

    private async Task<int> MarkAbandonedRunningAsync(CancellationToken cancellationToken)
    {
        var livenessThreshold = DateTimeOffset.UtcNow - LivenessWindow;

        var candidates = await _db.AgentRun.AsNoTracking()
            .Where(r => r.Status == AgentRunStatus.Running
                        && (r.HeartbeatAt == null || r.HeartbeatAt < livenessThreshold)
                        && !_db.AgentRunEvent.Any(e => e.AgentRunId == r.Id && e.OccurredAt >= livenessThreshold))
            .OrderBy(r => r.HeartbeatAt)
            .Take(BatchSize)
            .Select(r => r.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var marked = 0;
        foreach (var runId in candidates)
        {
            // Atomic CAS Running → Failed, pinned to status=Running so a worker completing the run
            // right now (its own Succeeded/Failed) wins the race and isn't overwritten.
            var transitioned = await _db.AgentRun
                .Where(r => r.Id == runId && r.Status == AgentRunStatus.Running)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(r => r.Status, AgentRunStatus.Failed)
                    .SetProperty(r => r.Error, AbandonedError)
                    .SetProperty(r => r.CompletedAt, (DateTimeOffset?)DateTimeOffset.UtcNow), cancellationToken)
                .ConfigureAwait(false);

            if (transitioned == 0) continue;

            marked++;

            await TryAppendAbandonedEventAsync(runId, cancellationToken).ConfigureAwait(false);
        }

        return marked;
    }

    /// <summary>Append an Error event so the live log / replay timeline shows the abandonment. Best-effort — a logging failure doesn't undo the recovery.</summary>
    private async Task TryAppendAbandonedEventAsync(Guid runId, CancellationToken cancellationToken)
    {
        try
        {
            _db.AgentRunEvent.Add(new AgentRunEvent { Id = Guid.NewGuid(), AgentRunId = runId, Kind = AgentEventKind.Error, Text = AbandonedError });
            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "AgentRunReconciler: failed to append the abandoned-run event for {RunId}", runId);
        }
    }
}

/// <summary>Diagnostic summary of one reconcile sweep. Returned for log surfacing + the recurring-job result.</summary>
public sealed record AgentRunReconcileSummary
{
    public int MarkedAbandonedFromRunning { get; init; }

    public int Total => MarkedAbandonedFromRunning;
}
