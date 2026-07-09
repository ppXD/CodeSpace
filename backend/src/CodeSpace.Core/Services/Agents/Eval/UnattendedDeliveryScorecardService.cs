using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents.Cost;
using CodeSpace.Core.Services.Agents.HumanTouch;
using CodeSpace.Core.Services.Agents.Publish;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;

namespace CodeSpace.Core.Services.Agents.Eval;

/// <summary>
/// Loads a team's recent TERMINAL workflow runs, projects each to an <see cref="UnattendedDeliveryRunOutcome"/> by
/// composing THREE already-existing ledgers — <see cref="IPublishManifestStore"/> (solved + delivered),
/// <see cref="IHumanTouchReader"/> (human touches), <see cref="ITeamCostService"/> (cost) — and hands them to the
/// pure <see cref="UnattendedDeliveryScorer"/>. Thin (Rule 16) — the service owns only the team-scoped queries +
/// projection; all the scoring math is the pure scorer's.
///
/// <para>Every terminal <c>WorkflowRun</c> counts, single-agent and supervisor-orchestrated alike — unlike
/// <c>SupervisorScorecardService</c> (which only scores runs with a decision ledger), this is the FULL run
/// population, because solved/delivered/touched are resolved off ledgers every run kind writes to. No writes, no
/// engine logic. The per-run list is capped most-recent-first to bound the payload.</para>
/// </summary>
public sealed class UnattendedDeliveryScorecardService : IUnattendedDeliveryScorecardService, IScopedDependency
{
    /// <summary>Cap on the recent runs scored + returned per call — bounds the payload + query cost.</summary>
    public const int RecentRunCap = 100;

    private readonly CodeSpaceDbContext _db;
    private readonly IPublishManifestStore _manifests;
    private readonly IHumanTouchReader _humanTouches;
    private readonly ITeamCostService _cost;

    public UnattendedDeliveryScorecardService(CodeSpaceDbContext db, IPublishManifestStore manifests, IHumanTouchReader humanTouches, ITeamCostService cost)
    {
        _db = db;
        _manifests = manifests;
        _humanTouches = humanTouches;
        _cost = cost;
    }

    public async Task<UnattendedDeliveryScorecard> ComputeAsync(Guid teamId, DateTimeOffset? since, CancellationToken cancellationToken)
    {
        var runs = await RecentTerminalRunsAsync(teamId, since, cancellationToken).ConfigureAwait(false);

        if (runs.Count == 0) return Empty();

        var runIds = runs.Select(r => r.Id).ToList();

        var manifestsByRun = await _manifests.ListForWorkflowRunsAsync(runIds, teamId, cancellationToken).ConfigureAwait(false);
        var touchesByRun = await _humanTouches.CountByWorkflowRunAsync(runIds, teamId, cancellationToken).ConfigureAwait(false);
        var costsByRun = await _cost.ComputeRunsAsync(teamId, runIds, cancellationToken).ConfigureAwait(false);

        var outcomes = runs
            .Select(r => ProjectRun(r.Id, r.Status, manifestsByRun.GetValueOrDefault(r.Id, EmptyManifests), touchesByRun.GetValueOrDefault(r.Id), costsByRun.GetValueOrDefault(r.Id)?.EstimatedCostUsd))
            .ToList();

        return UnattendedDeliveryScorer.Compute(outcomes);
    }

    /// <summary>
    /// The team's recent TERMINAL runs (most-recent first by CreatedDate), capped at <see cref="RecentRunCap"/> and
    /// windowed by <paramref name="since"/> on CreatedDate. Every terminal <c>WorkflowRun</c> counts — single-agent
    /// snapshot runs and supervisor-orchestrated authored runs alike — so the north-star is measured over the
    /// FULL delivery population, never just the supervisor lane. An in-flight run is not yet in the population
    /// (it has not yet had the chance to deliver). Carries each run's own terminal <see cref="WorkflowRunStatus"/>
    /// alongside its id — <see cref="IsSolved"/> needs it as the honest fallback when no manifest carries an
    /// objective acceptance grade.
    /// </summary>
    private async Task<IReadOnlyList<TerminalRun>> RecentTerminalRunsAsync(Guid teamId, DateTimeOffset? since, CancellationToken cancellationToken)
    {
        var query = _db.WorkflowRun.AsNoTracking()
            .Where(r => r.TeamId == teamId && (r.Status == WorkflowRunStatus.Success || r.Status == WorkflowRunStatus.Failure || r.Status == WorkflowRunStatus.Cancelled));

        if (since is { } from) query = query.Where(r => r.CreatedDate >= from);

        return await query
            .OrderByDescending(r => r.CreatedDate)
            .Take(RecentRunCap)
            .Select(r => new TerminalRun(r.Id, r.Status))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Project one run's manifests + human-touch count + cost into the pure scorer's input noun.</summary>
    private static UnattendedDeliveryRunOutcome ProjectRun(Guid runId, WorkflowRunStatus terminalStatus, IReadOnlyList<PublishManifest> manifests, int humanTouches, decimal? costUsd) => new()
    {
        WorkflowRunId = runId,
        Solved = IsSolved(manifests, terminalStatus),
        Delivered = IsDelivered(manifests),
        HumanTouches = humanTouches,
        CostUsd = costUsd,
    };

    /// <summary>
    /// An objective oracle verdict OVERRIDES the run's own terminal status when one exists: any manifest graded
    /// <see cref="PublishAcceptanceState.Failed"/> → never solved; at least one graded
    /// <see cref="PublishAcceptanceState.Passed"/> (and none Failed) → solved. When NO manifest carries an objective
    /// grade (every one is <see cref="PublishAcceptanceState.NotApplicable"/> — no acceptance check configured, the
    /// COMMON case for a run with no oracle wired), fall back to the run's own HONEST terminal
    /// <paramref name="terminalStatus"/> — mirrors <c>SupervisorEvalScorecard.ClassifyByRunStatus</c>'s identical
    /// precedent. A run with no configured oracle that genuinely reached <see cref="WorkflowRunStatus.Success"/> is
    /// never penalized for having none, but a Failure/Cancelled run is never counted solved just because nothing
    /// graded it either way.
    /// </summary>
    private static bool IsSolved(IReadOnlyList<PublishManifest> manifests, WorkflowRunStatus terminalStatus)
    {
        if (manifests.Any(m => m.AcceptanceState == PublishAcceptanceState.Failed)) return false;

        if (manifests.Any(m => m.AcceptanceState == PublishAcceptanceState.Passed)) return true;

        return terminalStatus == WorkflowRunStatus.Success;
    }

    /// <summary>At least one manifest actually left the sandbox — pushed to a remote branch, or (a stronger signal) has an opened PR/MR.</summary>
    private static bool IsDelivered(IReadOnlyList<PublishManifest> manifests) =>
        manifests.Any(m => m.PublishStateValue == PublishState.Pushed || m.PullRequestNumber != null);

    private static UnattendedDeliveryScorecard Empty() => new()
    {
        Rollup = new UnattendedDeliveryRollup
        {
            TotalRuns = 0,
            SolvedRuns = 0,
            DeliveredRuns = 0,
            UnattendedSolvedWithDeliveryRuns = 0,
            UnattendedSolveWithDeliveryRate = 0,
            SolveRate = 0,
            DeliveryRate = 0,
            AvgHumanTouches = 0,
            TotalCostUsd = null,
            UnknownCostRuns = 0,
        },
        Runs = Array.Empty<UnattendedDeliveryRunScore>(),
    };

    private static readonly IReadOnlyList<PublishManifest> EmptyManifests = Array.Empty<PublishManifest>();

    /// <summary>A terminal run's id + its own honest <see cref="WorkflowRunStatus"/> — the fallback <see cref="IsSolved"/> needs when no manifest carries an objective acceptance grade.</summary>
    private readonly record struct TerminalRun(Guid Id, WorkflowRunStatus Status);
}
