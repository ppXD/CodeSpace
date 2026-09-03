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
/// Loads a team's recent TERMINAL workflow runs, projects each to an <see cref="UnattendedDeliveryRunOutcome"/> —
/// the solve bit from the run's latest METRIC@1 verdict (the P0-A consumer switch: durable shadow assessment rows,
/// first-authorized-attempt receipts only, no status fallback), delivered from <see cref="IPublishManifestStore"/>,
/// human touches from <see cref="IHumanTouchReader"/>, cost from <see cref="ITeamCostService"/> — and hands them to
/// the pure <see cref="UnattendedDeliveryScorer"/>. Thin (Rule 16) — the service owns only the team-scoped queries +
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
    private readonly Completion.ICompletionCohortEligibility _cohort;

    public UnattendedDeliveryScorecardService(CodeSpaceDbContext db, IPublishManifestStore manifests, IHumanTouchReader humanTouches, ITeamCostService cost, Completion.ICompletionCohortEligibility cohort)
    {
        _db = db;
        _manifests = manifests;
        _humanTouches = humanTouches;
        _cost = cost;
        _cohort = cohort;
    }

    public async Task<UnattendedDeliveryScorecard> ComputeAsync(Guid teamId, DateTimeOffset? since, CancellationToken cancellationToken)
    {
        var population = await RecentTerminalRunsAsync(teamId, since, cancellationToken).ConfigureAwait(false);
        var suspendedRuns = await SuspendedRunCountAsync(teamId, since, cancellationToken).ConfigureAwait(false);

        // Era-aware denominator (option c): rates are over CONTRACT-ERA runs only — a pre-protocol run is counted
        // visibly (LegacyRuns) but never scored; old tape is never re-derived into a verdict.
        var legacyRuns = population.Count(r => r.CompletionPolicyVersion is null);
        var runs = population.Where(r => r.CompletionPolicyVersion is not null).ToList();

        if (runs.Count == 0) return Empty() with { Rollup = Empty().Rollup with { LegacyRuns = legacyRuns, SuspendedRuns = suspendedRuns } };

        var runIds = runs.Select(r => r.Id).ToList();
        var latestAssessments = await LatestAssessmentsAsync(teamId, runIds, cancellationToken).ConfigureAwait(false);

        var manifestsByRun = await _manifests.ListForWorkflowRunsAsync(runIds, teamId, cancellationToken).ConfigureAwait(false);
        var typedDeliveredRuns = await TypedDeliveredRunIdsAsync(_db, runIds, teamId, cancellationToken).ConfigureAwait(false);
        var touchesByRun = await _humanTouches.CountByWorkflowRunAsync(runIds, teamId, cancellationToken).ConfigureAwait(false);
        var costsByRun = await _cost.ComputeRunsAsync(teamId, runIds, cancellationToken).ConfigureAwait(false);
        var degradedStopRuns = await DegradedStopRunIdsAsync(_db, runIds, teamId, cancellationToken).ConfigureAwait(false);

        var metricSolvedRuns = latestAssessments
            .Where(kv => kv.Value.MetricOutcome == nameof(Messages.Contracts.OutcomeDisposition.Solved))
            .Select(kv => kv.Key)
            .ToHashSet();

        var outcomes = runs
            .Select(r => ProjectRun(r.Id, metricSolvedRuns.Contains(r.Id), manifestsByRun.GetValueOrDefault(r.Id, EmptyManifests), typedDeliveredRuns.Contains(r.Id), touchesByRun.GetValueOrDefault(r.Id), costsByRun.GetValueOrDefault(r.Id)?.EstimatedCostUsd))
            .ToList();

        var card = UnattendedDeliveryScorer.Compute(outcomes);

        var legacySolvedRuns = runs.Count(r => IsSolved(manifestsByRun.GetValueOrDefault(r.Id, EmptyManifests), r.Status, degradedStopRuns.Contains(r.Id)));
        var unassessedRuns = runs.Count(r => !latestAssessments.TryGetValue(r.Id, out var latest) || latest.MetricOutcome is null);

        // The would-be CleanSuccess population, split by the gates the SHADOW does not apply: every one of these
        // rows cleared the two evidence gates, and only some are in a cohort the authority could terminalize at all.
        var wouldBeCleanSuccess = latestAssessments.Values
            .Where(a => a.WouldBeTerminalDecision == nameof(Messages.Contracts.TerminalDecision.CleanSuccess))
            .ToList();

        var byLessonArm = await SliceByLessonArmAsync(teamId, runIds, card.Runs, cancellationToken).ConfigureAwait(false);

        return card with
        {
            Rollup = card.Rollup with
            {
                ByLessonArm = byLessonArm,
                LegacyRuns = legacyRuns,
                SuspendedRuns = suspendedRuns,
                AssessedRuns = latestAssessments.Count,
                AssessmentSolvedRuns = latestAssessments.Count(kv => kv.Value.Outcome == nameof(Messages.Contracts.OutcomeDisposition.Solved)),
                EvidenceEligibleCleanSuccessRuns = wouldBeCleanSuccess.Count,
                CohortEligibleCleanSuccessRuns = wouldBeCleanSuccess.Count(a => _cohort.IsCohortEligible(a.RunMode, a.CapabilityKey)),
                LegacySolvedRuns = legacySolvedRuns,
                UnassessedRuns = unassessedRuns,
            },
        };
    }

    /// <summary>
    /// Each windowed run's LATEST shadow assessment row — the metric@1 verdict (the primary solve bit since the
    /// P0-A consumer switch), the operational Outcome, the would-be terminal (the parity columns), and the run's
    /// recorded mode + capability key, which are what the would-be terminal's three unapplied structural gates
    /// re-derive from. A run missing here has not been swept yet; it reads unassessed and never solved until its
    /// row lands.
    /// </summary>
    private async Task<IReadOnlyDictionary<Guid, LatestAssessment>> LatestAssessmentsAsync(Guid teamId, IReadOnlyList<Guid> runIds, CancellationToken cancellationToken)
    {
        var rows = await _db.CompletionAssessmentRecord.AsNoTracking()
            .Where(a => a.TeamId == teamId && runIds.Contains(a.WorkflowRunId))
            .OrderBy(a => a.CreatedDate)
            .Select(a => new { a.WorkflowRunId, a.Outcome, a.WouldBeTerminalDecision, a.MetricOutcome, a.RunMode, a.CapabilityKey, a.CreatedDate })
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        return rows
            .GroupBy(a => a.WorkflowRunId)
            .Select(g => g.Last())
            .ToDictionary(a => a.WorkflowRunId, a => new LatestAssessment(a.Outcome, a.WouldBeTerminalDecision, a.MetricOutcome, a.RunMode, a.CapabilityKey));
    }

    private readonly record struct LatestAssessment(string Outcome, string? WouldBeTerminalDecision, string? MetricOutcome, string? RunMode, string? CapabilityKey);

    /// <summary>
    /// A4: the window divided by the Arc-D lesson A/B arm — the fold that turns "the arm is recorded on every
    /// supervisor decision" into "the arm is MEASURED against the north-star". Nothing sliced a rate by it before,
    /// so injection's effect had never been measured at all.
    ///
    /// <para>ONLY the ARM is read from a durable source; every scored BIT comes from the live score computed just
    /// above. That split is load-bearing: a persisted row can be stale (its run's manifest settled after the row
    /// was written, and the backfill has not revisited it), so preferring the row's own <c>solved</c>/<c>delivered</c>
    /// bits made <c>SolvedRuns</c> and <c>sum(ByLessonArm.SolvedRuns)</c> disagree on the same page — two numbers
    /// measured over the same runs by two different clocks. Taking the arm alone keeps the slice a partition OF the
    /// rollup: same runs, same bits, same totals, just grouped.</para>
    ///
    /// <para>The arm prefers the row and falls back to a batched read of the decision ledger's frozen value, so an
    /// empty table degrades to a purely live slice rather than an empty one. Supervisor-lane runs only — see
    /// <see cref="ArmedRunScore.LessonArm"/>.</para>
    /// </summary>
    private async Task<IReadOnlyList<LessonArmSlice>> SliceByLessonArmAsync(Guid teamId, IReadOnlyList<Guid> runIds, IReadOnlyList<UnattendedDeliveryRunScore> liveScores, CancellationToken cancellationToken)
    {
        var persistedArms = await _db.RunScorecard.AsNoTracking()
            .Where(s => s.TeamId == teamId && runIds.Contains(s.WorkflowRunId) && s.LessonArm != null)
            .Select(s => new { s.WorkflowRunId, s.LessonArm })
            .ToDictionaryAsync(s => s.WorkflowRunId, s => s.LessonArm!, cancellationToken).ConfigureAwait(false);

        var ledgerArms = await RunLessonArms.ReadAsync(_db, runIds, teamId, cancellationToken).ConfigureAwait(false);

        var rows = liveScores
            .Select(score => new ArmedRunScore
            {
                LessonArm = persistedArms.GetValueOrDefault(score.WorkflowRunId) ?? ledgerArms.GetValueOrDefault(score.WorkflowRunId),
                Solved = score.Solved,
                Delivered = score.Delivered,
                UnattendedSolvedWithDelivery = score.UnattendedSolvedWithDelivery,
            })
            .ToList();

        return LessonArmSlicer.Slice(rows);
    }

    /// <summary>
    /// The team's recent TERMINAL runs (most-recent first by CreatedDate), capped at <see cref="RecentRunCap"/> and
    /// windowed by <paramref name="since"/> on CreatedDate. Every terminal <c>WorkflowRun</c> counts — single-agent
    /// snapshot runs and supervisor-orchestrated authored runs alike — so the north-star is measured over the
    /// FULL delivery population, never just the supervisor lane. An in-flight run is not yet in the population
    /// (it has not yet had the chance to deliver). Carries each run's own terminal <see cref="WorkflowRunStatus"/>
    /// alongside its id — the LEGACY parity column (<see cref="IsSolved"/>) still reads it.
    /// </summary>
    private async Task<IReadOnlyList<TerminalRun>> RecentTerminalRunsAsync(Guid teamId, DateTimeOffset? since, CancellationToken cancellationToken)
    {
        var query = _db.WorkflowRun.AsNoTracking()
            .Where(r => r.TeamId == teamId && (r.Status == WorkflowRunStatus.Success || r.Status == WorkflowRunStatus.Failure || r.Status == WorkflowRunStatus.Cancelled));

        if (since is { } from) query = query.Where(r => r.CreatedDate >= from);

        return await query
            .OrderByDescending(r => r.CreatedDate)
            .Take(RecentRunCap)
            .Select(r => new TerminalRun(r.Id, r.Status, r.CompletionPolicyVersion))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Currently-suspended runs created in the window — the parked population the terminal denominator cannot see.</summary>
    private async Task<int> SuspendedRunCountAsync(Guid teamId, DateTimeOffset? since, CancellationToken cancellationToken)
    {
        var query = _db.WorkflowRun.AsNoTracking().Where(r => r.TeamId == teamId && r.Status == WorkflowRunStatus.Suspended);

        if (since is { } from) query = query.Where(r => r.CreatedDate >= from);

        return await query.CountAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Project one run's metric@1 solve bit + manifests + typed-delivery bit + human-touch count + cost into the pure scorer's input noun.</summary>
    private static UnattendedDeliveryRunOutcome ProjectRun(Guid runId, bool metricSolved, IReadOnlyList<PublishManifest> manifests, bool typedDelivered, int humanTouches, decimal? costUsd) => new()
    {
        WorkflowRunId = runId,
        Solved = metricSolved,
        Delivered = IsDelivered(manifests) || typedDelivered,
        HumanTouches = humanTouches,
        CostUsd = costUsd,
    };

    /// <summary>
    /// THE LEGACY LADDER — no longer the primary solve bit (the P0-A consumer switch cut the primary rates over to
    /// the metric@1 projection); retained verbatim as the standing parity comparison: the shadow snapshot's
    /// <c>LegacyIsSolved</c> column and the rollup's <c>LegacySolvedRuns</c> both read it, so the legacy-vs-metric
    /// delta stays a live query instead of a memory. Its shape: an objective oracle verdict overrides the run's own
    /// terminal status when one exists (Failed → never solved; Waived → never solved; Passed → solved), and with no
    /// graded manifest it falls back to engine Success minus degraded stops — exactly the status-fallback inference
    /// the metric plane structurally removed.
    /// </summary>
    public static bool IsSolved(IReadOnlyList<PublishManifest> manifests, WorkflowRunStatus terminalStatus, bool degradedStop)
    {
        if (manifests.Any(m => m.AcceptanceState == PublishAcceptanceState.Failed)) return false;

        // B2 (FATAL-1): a WAIVED artifact means a human authorized forgoing verification — no objective claim in
        // either direction, so it must neither solve (a fully-waived Success run counted Solved with zero waive
        // trace before this arm) nor be silently absorbed by a passing sibling. Checked BEFORE Passed, mirroring
        // CompletionReducer's severity order (Failed > Waived > Passed): any waived work ⇒ the run's completion is
        // not fully verified ⇒ never Solved via the oracle leg or the status fallback.
        if (manifests.Any(m => m.AcceptanceState == PublishAcceptanceState.Waived)) return false;

        if (manifests.Any(m => m.AcceptanceState == PublishAcceptanceState.Passed)) return true;

        // P2b-prep (metric-shift, its own pinned PR): a DEGRADED supervisor stop (forced bound / model give-up)
        // lands engine Success by design — the status fallback must not read it Solved. An oracle verdict above
        // still overrides in both directions; this only removes the no-oracle inflation the genericity audit and
        // the external review both named as THE north-star inflation.
        return terminalStatus == WorkflowRunStatus.Success && !degradedStop;
    }

    /// <summary>
    /// The runs whose LAST supervisor stop classifies degraded (forced / give-up) — the shared discriminator the
    /// scorecard's fallback and the shadow snapshot both read (one implementation, never forked). One batched
    /// query; a run with no stop decision (single-agent, plan-map, failed-outright supervisor) is absent — its
    /// honest terminal status stands.
    /// </summary>
    public static async Task<HashSet<Guid>> DegradedStopRunIdsAsync(CodeSpaceDbContext db, IReadOnlyList<Guid> runIds, Guid teamId, CancellationToken cancellationToken)
    {
        if (runIds.Count == 0) return new HashSet<Guid>();

        var rows = await db.SupervisorDecisionRecord.AsNoTracking()
            .Where(d => d.TeamId == teamId && runIds.Contains(d.SupervisorRunId) && d.DecisionKind == SupervisorDecisionKinds.Stop)
            .Select(d => new { d.SupervisorRunId, d.Sequence, d.PayloadJson, d.OutcomeJson })
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        return rows
            .GroupBy(d => d.SupervisorRunId)
            .Where(g =>
            {
                var last = g.OrderByDescending(d => d.Sequence).First();
                return Supervisor.SupervisorOutcome.ClassifyStop(last.PayloadJson, last.OutcomeJson).Kind != SupervisorStopKind.Succeeded;
            })
            .Select(g => g.Key)
            .ToHashSet();
    }

    /// <summary>At least one manifest actually left the sandbox — pushed to a remote branch, or (a stronger signal) has an opened PR/MR. The TYPED half (DC-4): a repo-less run delivers by durable CAPTURE — its current (unsuperseded) artifact-manifest rows ARE the arrival, there being no external remote — OR'd in by the caller so this git predicate stays pure over its own ledger.</summary>
    private static bool IsDelivered(IReadOnlyList<PublishManifest> manifests) =>
        manifests.Any(m => m.PublishStateValue == PublishState.Pushed || m.PullRequestNumber != null);

    /// <summary>The runs whose attempts captured at least one CURRENT typed artifact — the repo-less lane's delivery fact (a superseded row alone is history, not an arrival). Static + db-taking (mirroring <see cref="DegradedStopRunIdsAsync"/>) so the durable per-run writer reads the SAME predicate instead of forking a second one.</summary>
    public static async Task<HashSet<Guid>> TypedDeliveredRunIdsAsync(CodeSpaceDbContext db, IReadOnlyList<Guid> runIds, Guid teamId, CancellationToken cancellationToken) =>
        (await db.ArtifactManifest.AsNoTracking()
            .Where(m => m.TeamId == teamId && m.WorkflowRunId != null && runIds.Contains(m.WorkflowRunId.Value) && m.SupersededByManifestId == null)
            .Select(m => m.WorkflowRunId!.Value)
            .Distinct()
            .ToListAsync(cancellationToken).ConfigureAwait(false))
        .ToHashSet();

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
    private readonly record struct TerminalRun(Guid Id, WorkflowRunStatus Status, int? CompletionPolicyVersion);
}
