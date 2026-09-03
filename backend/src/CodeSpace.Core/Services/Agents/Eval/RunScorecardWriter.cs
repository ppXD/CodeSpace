using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents.Cost;
using CodeSpace.Core.Services.Agents.HumanTouch;
using CodeSpace.Core.Services.Agents.Publish;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;

namespace CodeSpace.Core.Services.Agents.Eval;

/// <summary>
/// Projects ONE terminal run onto its durable <c>run_scorecard</c> row, using the SAME facts and the SAME pure
/// <see cref="UnattendedDeliveryScorer"/> the live team-wide scorecard uses — there is no second scorer, and the
/// headline bit is never recomputed here. Thin (Rule 16): the service owns only the per-run reads + the upsert.
///
/// <para>Every fact already had a per-run or one-element-batch reader: the metric@1 solve bit off the run's LATEST
/// <see cref="CompletionAssessmentRecord"/>, delivery off <see cref="IPublishManifestStore"/> OR'd with the
/// repo-less typed-artifact predicate, touches off <see cref="IHumanTouchReader"/>, agent spend off
/// <see cref="ITeamCostService"/>, and the brain-plane spend + brain model off the run's own
/// <c>interaction.completed</c> ledger. Nothing new is inferred; the row is a projection, not a re-judgement.</para>
/// </summary>
public sealed class RunScorecardWriter : IRunScorecardWriter, IScopedDependency
{
    /// <summary>
    /// The <c>LlmCallScope</c> kind the supervisor's own decision calls are recorded under — the label whose model
    /// IS the run's brain. It mirrors the literal pushed in <c>SupervisorTurnService.cs</c> (the
    /// <c>"supervisor.decision"</c> scope around the decider call); the kind vocabulary is deliberately open there,
    /// so this is a read-side agreement rather than a shared enum, and a unit test pins the string.
    /// </summary>
    public const string SupervisorDecisionCallKind = "supervisor.decision";

    private readonly CodeSpaceDbContext _db;
    private readonly IPublishManifestStore _manifests;
    private readonly IHumanTouchReader _humanTouches;
    private readonly ITeamCostService _cost;

    public RunScorecardWriter(CodeSpaceDbContext db, IPublishManifestStore manifests, IHumanTouchReader humanTouches, ITeamCostService cost)
    {
        _db = db;
        _manifests = manifests;
        _humanTouches = humanTouches;
        _cost = cost;
    }

    public async Task<bool> WriteAsync(Guid workflowRunId, Guid teamId, CancellationToken cancellationToken)
    {
        if (await CandidateRunAsync(workflowRunId, teamId, cancellationToken).ConfigureAwait(false) is not { } run) return false;

        var score = UnattendedDeliveryScorer.Score(await ProjectAsync(run, teamId, cancellationToken).ConfigureAwait(false));
        var brain = await ReadBrainPlaneAsync(workflowRunId, cancellationToken).ConfigureAwait(false);
        var arms = await RunLessonArms.ReadAsync(_db, [workflowRunId], teamId, cancellationToken).ConfigureAwait(false);

        return await UpsertAsync(run, teamId, score, brain, arms.GetValueOrDefault(workflowRunId), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The run, IF it is a candidate: terminal and CONTRACT-ERA. A pre-protocol run (no
    /// <c>CompletionPolicyVersion</c>) is deliberately never scored — the same era-aware denominator the live
    /// rollup applies, so old tape is never re-derived into a verdict here either.
    /// </summary>
    private async Task<CandidateRun?> CandidateRunAsync(Guid workflowRunId, Guid teamId, CancellationToken cancellationToken) =>
        await _db.WorkflowRun.AsNoTracking()
            .Where(r => r.Id == workflowRunId && r.TeamId == teamId
                        && r.CompletionPolicyVersion != null
                        && (r.Status == WorkflowRunStatus.Success || r.Status == WorkflowRunStatus.Failure || r.Status == WorkflowRunStatus.Cancelled))
            .Select(r => new CandidateRun(r.Id, r.Status, r.ProjectionKind, r.CompletedAt, r.LastModifiedDate))
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);

    /// <summary>Gather the four scorer inputs for this ONE run — the same four the team-wide service batches.</summary>
    private async Task<UnattendedDeliveryRunOutcome> ProjectAsync(CandidateRun run, Guid teamId, CancellationToken cancellationToken)
    {
        var solved = await IsMetricSolvedAsync(run.Id, teamId, cancellationToken).ConfigureAwait(false);
        var manifests = await _manifests.ListForWorkflowRunAsync(run.Id, teamId, cancellationToken).ConfigureAwait(false);
        var typedDelivered = await UnattendedDeliveryScorecardService.TypedDeliveredRunIdsAsync(_db, [run.Id], teamId, cancellationToken).ConfigureAwait(false);
        var touches = await _humanTouches.CountByWorkflowRunAsync([run.Id], teamId, cancellationToken).ConfigureAwait(false);
        var cost = await _cost.ComputeRunAsync(teamId, run.Id, cancellationToken).ConfigureAwait(false);

        return new UnattendedDeliveryRunOutcome
        {
            WorkflowRunId = run.Id,
            Solved = solved,
            Delivered = IsDelivered(manifests) || typedDelivered.Contains(run.Id),
            HumanTouches = touches.GetValueOrDefault(run.Id),
            CostUsd = cost.EstimatedCostUsd,
        };
    }

    /// <summary>The run's LATEST shadow assessment reads metric@1 Solved — the primary solve bit since the P0-A consumer switch. An unassessed run reads false (never solved), exactly as the live rollup counts it.</summary>
    private async Task<bool> IsMetricSolvedAsync(Guid workflowRunId, Guid teamId, CancellationToken cancellationToken)
    {
        var latest = await _db.CompletionAssessmentRecord.AsNoTracking()
            .Where(a => a.TeamId == teamId && a.WorkflowRunId == workflowRunId)
            .OrderByDescending(a => a.CreatedDate)
            .Select(a => a.MetricOutcome)
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);

        return latest == nameof(Messages.Contracts.OutcomeDisposition.Solved);
    }

    /// <summary>The git half of delivery — a pushed manifest or an opened PR. Mirrors the live service's own predicate; the repo-less typed half is OR'd in by the caller.</summary>
    private static bool IsDelivered(IReadOnlyList<PublishManifest> manifests) =>
        manifests.Any(m => m.PublishStateValue == PublishState.Pushed || m.PullRequestNumber != null);

    /// <summary>
    /// The run's BRAIN-plane facts, folded from its own <c>interaction.completed</c> ledger through the SAME
    /// <see cref="InteractionSpend"/> pricer the supervisor's budget recitation uses: total priced USD (null when
    /// nothing was priceable — fail-open, never a silent $0) and the model its DECISION calls were authored by.
    /// </summary>
    private async Task<BrainPlaneFacts> ReadBrainPlaneAsync(Guid workflowRunId, CancellationToken cancellationToken)
    {
        var records = await _db.WorkflowRunRecord.AsNoTracking()
            .Where(r => r.RunId == workflowRunId && r.RecordType == WorkflowRunRecordTypes.InteractionCompleted)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        if (records.Count == 0) return new BrainPlaneFacts(null, null);

        var priced = records.Select(InteractionSpend.From).ToList();
        var summary = BrainPlaneSpendSummary.From(priced);
        var brainModel = priced.FirstOrDefault(r => r.Kind == SupervisorDecisionCallKind && !string.IsNullOrWhiteSpace(r.Model))?.Model;

        return new BrainPlaneFacts(summary.ByKind.Count == 0 ? null : summary.TotalUsd, brainModel);
    }

    /// <summary>
    /// Upsert the run's ONE row. A concurrent writer that inserted first loses nothing — the unique index rejects
    /// the duplicate and this call reports "not written" rather than appending a second opinion on the same run.
    /// </summary>
    private async Task<bool> UpsertAsync(CandidateRun run, Guid teamId, UnattendedDeliveryRunScore score, BrainPlaneFacts brain, string? lessonArm, CancellationToken cancellationToken)
    {
        var existing = await _db.RunScorecard.SingleOrDefaultAsync(s => s.WorkflowRunId == run.Id, cancellationToken).ConfigureAwait(false);

        var row = RunScorecardProjection.Apply(existing ?? new RunScorecard { Id = Guid.NewGuid(), TeamId = teamId, WorkflowRunId = run.Id }, new RunScorecardFacts
        {
            CompletedAt = run.CompletedAt ?? run.LastModifiedDate,
            ProjectionKind = run.ProjectionKind,
            Score = score,
            BrainPlaneUsd = brain.TotalUsd,
            BrainModel = brain.Model,
            LessonArm = lessonArm,
        });

        if (existing is null) _db.RunScorecard.Add(row);

        try
        {
            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (DbUpdateException)
        {
            _db.Entry(row).State = EntityState.Detached;
            return false;
        }
    }

    /// <summary>
    /// The run facts the row copies verbatim — its terminal stamp (falling back to last-modified for a bypass
    /// terminal that recorded none) and its projection kind.
    ///
    /// <para>A REFERENCE type on purpose. As a <c>record struct</c> this was silently unusable as a candidacy
    /// signal: <c>SingleOrDefaultAsync</c> over a value-type projection returns <c>default</c>, not null, so the
    /// <c>is not { }</c> guard never fired and a non-terminal, pre-protocol, or other team's run projected an
    /// all-zero row. The integration tier caught it; a reference type makes "no candidate" actually representable.</para>
    /// </summary>
    private sealed record CandidateRun(Guid Id, WorkflowRunStatus Status, string? ProjectionKind, DateTimeOffset? CompletedAt, DateTimeOffset LastModifiedDate);

    /// <summary>The two brain-plane columns: priced total (null = nothing priceable) and the decision model.</summary>
    private readonly record struct BrainPlaneFacts(decimal? TotalUsd, string? Model);
}
