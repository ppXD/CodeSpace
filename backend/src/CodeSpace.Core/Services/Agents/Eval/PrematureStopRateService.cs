using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Services.Supervisor;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Tasks;
using Microsoft.EntityFrameworkCore;

namespace CodeSpace.Core.Services.Agents.Eval;

/// <summary>
/// P4 — the stability north-star: "of the task runs we started (single-agent, plan-map, or supervisor alike), what
/// fraction died prematurely rather than reaching a genuine conclusion." Scoped to task launches
/// (<c>WorkflowRun.ProjectionKind is not null</c> — see <see cref="TaskProjectionKinds"/>) since that is exactly the
/// population the "park, don't die" promise governs; an authored/webhook/schedule workflow run carries no
/// projection kind and is out of this metric's scope.
///
/// <para>DELIBERATELY includes non-terminal runs in the population (never silently drops them the way a narrower
/// "success rate over terminal runs" metric would — see <see cref="UnattendedDeliveryScorecardService"/>'s own,
/// differently-scoped exclusion) — a run stuck in Running/Suspended forever is the single worst outcome this arc
/// exists to prevent, and a metric that quietly excludes it because it "hasn't finished yet" would hide exactly the
/// failure mode most worth catching.</para>
///
/// <para>Classification per lane: the SUPERVISOR lane (which can report a misleadingly-clean <c>WorkflowRunStatus.Success</c>
/// even when the run was actually force-stopped by a bound/governance trip) reads its OWN last <c>stop</c> decision
/// via the SAME <see cref="SupervisorOutcome.ClassifyStop"/> the run's own journal/result-card use — never a second,
/// possibly-drifting classifier. Every OTHER projection kind (single-agent, plan-map) has no such "clean status hides
/// a forced stop" gap (a genuine node failure already propagates to <c>WorkflowRunStatus.Failure</c>), so its own
/// terminal status IS the honest signal.</para>
/// </summary>
public sealed class PrematureStopRateService : IPrematureStopRateService, IScopedDependency
{
    /// <summary>Operator escape hatch (Rule 8): how many hours a non-terminal run may sit in Pending/Enqueued/Running/Suspended before it counts as STUCK (a loud, separate figure — never folded silently into "still in progress"). Default 24h — generous enough that no genuine long-running Deep/Unattended task trips it, tight enough that a truly abandoned run is caught within a day.</summary>
    public const string StuckThresholdHoursEnvVar = "CODESPACE_STUCK_RUN_THRESHOLD_HOURS";

    internal const double DefaultStuckThresholdHours = 24;

    private static readonly IReadOnlySet<WorkflowRunStatus> NonTerminalStatuses = new HashSet<WorkflowRunStatus>
    {
        WorkflowRunStatus.Pending, WorkflowRunStatus.Enqueued, WorkflowRunStatus.Running, WorkflowRunStatus.Suspended,
    };

    private readonly CodeSpaceDbContext _db;

    public PrematureStopRateService(CodeSpaceDbContext db)
    {
        _db = db;
    }

    public async Task<PrematureStopRateReport> ComputeAsync(Guid teamId, DateTimeOffset? since, CancellationToken cancellationToken)
    {
        var query = _db.WorkflowRun.AsNoTracking()
            .Where(r => r.TeamId == teamId && r.ProjectionKind != null);

        if (since is { } from) query = query.Where(r => r.CreatedDate >= from);

        var runs = await query
            .Select(r => new RunRow(r.Id, r.Status, r.ProjectionKind!, r.CreatedDate))
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        if (runs.Count == 0) return Empty();

        var supervisorRunIds = runs
            .Where(r => r.ProjectionKind == TaskProjectionKinds.Supervisor && !NonTerminalStatuses.Contains(r.Status) && r.Status != WorkflowRunStatus.Cancelled)
            .Select(r => r.Id)
            .ToList();

        var lastStopByRun = supervisorRunIds.Count == 0
            ? EmptyStops
            : await LastStopDecisionByRunAsync(supervisorRunIds, teamId, cancellationToken).ConfigureAwait(false);

        var stuckThreshold = StuckThresholdHours();
        var now = DateTimeOffset.UtcNow;

        var buckets = runs.Select(r => Classify(r, lastStopByRun)).ToList();

        var stillInProgress = runs.Where((r, i) => buckets[i] == RunOutcomeBucket.StillInProgress).ToList();
        var stuck = stillInProgress.Count(r => (now - r.CreatedDate).TotalHours >= stuckThreshold);

        return new PrematureStopRateReport
        {
            TotalRuns = runs.Count,
            SucceededRuns = buckets.Count(b => b == RunOutcomeBucket.Succeeded),
            DegradedRuns = buckets.Count(b => b == RunOutcomeBucket.Degraded),
            CancelledRuns = buckets.Count(b => b == RunOutcomeBucket.Cancelled),
            StillInProgressRuns = stillInProgress.Count,
            StuckRuns = stuck,
        };
    }

    /// <summary>Classify ONE run into the shared bucket. Internal + pure (given the pre-fetched stop map) so it is unit-pinned directly.</summary>
    internal static RunOutcomeBucket Classify(RunRow run, IReadOnlyDictionary<Guid, (string? PayloadJson, string? OutcomeJson)> lastStopByRun)
    {
        if (NonTerminalStatuses.Contains(run.Status)) return RunOutcomeBucket.StillInProgress;

        if (run.Status == WorkflowRunStatus.Cancelled) return RunOutcomeBucket.Cancelled;

        if (run.ProjectionKind == TaskProjectionKinds.Supervisor)
        {
            if (lastStopByRun.TryGetValue(run.Id, out var stop))
            {
                var kind = SupervisorOutcome.ClassifyStop(stop.PayloadJson, stop.OutcomeJson).Kind;
                return kind == SupervisorStopKind.Succeeded ? RunOutcomeBucket.Succeeded : RunOutcomeBucket.Degraded;
            }

            // Defensive fallback (mirrors SupervisorEvalScorecard.ClassifyByRunStatus): a terminal supervisor run
            // with NO stop decision at all (the node failed outright, or was cancelled — Cancelled is handled above)
            // reads its own honest WorkflowRunStatus rather than defaulting to a false success.
            return run.Status == WorkflowRunStatus.Success ? RunOutcomeBucket.Succeeded : RunOutcomeBucket.Degraded;
        }

        // Single-agent / plan-map / any future projection: no "clean status hides a forced stop" gap exists —
        // a genuine node failure already propagates to Failure, so the run's own terminal status is the honest signal.
        return run.Status == WorkflowRunStatus.Success ? RunOutcomeBucket.Succeeded : RunOutcomeBucket.Degraded;
    }

    /// <summary>The LAST (highest-Sequence) "stop" decision per supervisor run id, batched in ONE query — never N+1. Small per-call id set (the team's own supervisor runs in the window), so the per-run max-Sequence pick happens in memory.</summary>
    private async Task<IReadOnlyDictionary<Guid, (string? PayloadJson, string? OutcomeJson)>> LastStopDecisionByRunAsync(IReadOnlyList<Guid> supervisorRunIds, Guid teamId, CancellationToken cancellationToken)
    {
        var rows = await _db.SupervisorDecisionRecord.AsNoTracking()
            .Where(d => d.TeamId == teamId && supervisorRunIds.Contains(d.SupervisorRunId) && d.DecisionKind == SupervisorDecisionKinds.Stop)
            .Select(d => new { d.SupervisorRunId, d.Sequence, d.PayloadJson, d.OutcomeJson })
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        return rows
            .GroupBy(d => d.SupervisorRunId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(d => d.Sequence).Select(d => (d.PayloadJson, d.OutcomeJson)).First());
    }

    /// <summary>The operator's configured stuck threshold in hours, or <see cref="DefaultStuckThresholdHours"/> when unset/unparseable/non-positive.</summary>
    private static double StuckThresholdHours() =>
        double.TryParse(Environment.GetEnvironmentVariable(StuckThresholdHoursEnvVar), out var hours) && hours > 0 ? hours : DefaultStuckThresholdHours;

    private static PrematureStopRateReport Empty() => new();

    private static readonly IReadOnlyDictionary<Guid, (string? PayloadJson, string? OutcomeJson)> EmptyStops = new Dictionary<Guid, (string?, string?)>();

    /// <summary>One run's classification inputs — a pure data noun so <see cref="Classify"/> is unit-testable without a DB.</summary>
    internal readonly record struct RunRow(Guid Id, WorkflowRunStatus Status, string ProjectionKind, DateTimeOffset CreatedDate);
}
