using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Agents.Eval;
using CodeSpace.Core.Services.Agents.Publish;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Contracts;
using CodeSpace.Messages.Dtos.Workflows;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace CodeSpace.Core.Services.Completion;

public interface ICompletionShadowService
{
    /// <summary>Compose + append assessments for recent terminal contract-era runs that have none yet (or whose latest differs). Returns how many rows were appended. Shadow NEVER mutates a run's terminal (Lock Clause 1).</summary>
    Task<int> SweepAsync(int batchSize, CancellationToken cancellationToken);
}

/// <summary>
/// P2a-4: the Shadow recorder — finds terminal, contract-era runs missing a durable assessment, composes each
/// (the full P1/P2 chain), snapshots the LEGACY scorecard ladder's verdict beside it, and APPENDS the record.
/// Append-only with change detection: a re-sweep whose composed assessment matches the latest row appends
/// nothing; a differing one (new receipts, a replay) appends history (Lock Clause 2's append law). The
/// degraded-inflation delta — assessment Unsolved while legacy read Solved — becomes a standing query over
/// completion_assessment instead of a one-off audit.
/// </summary>
public sealed class CompletionShadowService : ICompletionShadowService, IScopedDependency
{
    private readonly CodeSpaceDbContext _db;
    private readonly ICompletionAssessmentComposer _composer;
    private readonly IPublishManifestStore _manifests;
    private readonly ICompletionContractStore _contracts;
    private readonly ICompletionHandoffProbe _handoff;
    private readonly IModeProfileRegistry _modes;
    private readonly ILogger<CompletionShadowService> _logger;

    public CompletionShadowService(CodeSpaceDbContext db, ICompletionAssessmentComposer composer, IPublishManifestStore manifests, ICompletionContractStore contracts, ICompletionHandoffProbe handoff, IModeProfileRegistry modes, ILogger<CompletionShadowService> logger)
    {
        _db = db;
        _composer = composer;
        _manifests = manifests;
        _contracts = contracts;
        _handoff = handoff;
        _modes = modes;
        _logger = logger;
    }

    public async Task<int> SweepAsync(int batchSize, CancellationToken cancellationToken)
    {
        // TWO passes, deliberately separate rather than one widened query.
        //
        // The first is the original: terminal runs never assessed. Unchanged — a run appears in it once and then
        // never again, so it can never starve behind a busier neighbour.
        //
        // The second exists because the first was ALL there was, which made the append-on-change logic in
        // RecordAsync unreachable: a run was assessed once, and evidence arriving after its terminal — a reconciler
        // settling a manifest, a grade landing late — could never move the record. A run's ledger keeps moving after
        // it terminalizes, so "assessed once" was being read as "assessed correctly".
        //
        // The second pass is the P2 precise predicate the original window stood in for: ONE indexed comparison —
        // the run's ledger-head version moved past what its latest assessment recorded. Exact in both directions
        // the 24h CompletedAt window was not: no re-examining every recent run each sweep, and no silent horizon
        // (a manifest settling on day 3 used to be out of reach forever). A latest record with a NULL version
        // (pre-slice) compares stale once and converges; a run whose head never moved is not a candidate at all.
        var unassessed = await _db.WorkflowRun.AsNoTracking()
            .Where(r => r.CompletionPolicyVersion != null
                        && (r.Status == WorkflowRunStatus.Success || r.Status == WorkflowRunStatus.Failure || r.Status == WorkflowRunStatus.Cancelled)
                        && !_db.CompletionAssessmentRecord.Any(a => a.WorkflowRunId == r.Id))
            .OrderByDescending(r => r.CreatedDate)
            .Take(batchSize)
            .Select(r => new { r.Id, r.TeamId, r.Status })
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        var revisit = await _db.WorkflowRun.AsNoTracking()
            .Where(r => r.CompletionPolicyVersion != null
                        && (r.Status == WorkflowRunStatus.Success || r.Status == WorkflowRunStatus.Failure || r.Status == WorkflowRunStatus.Cancelled)
                        && _db.CompletionAssessmentRecord.Any(a => a.WorkflowRunId == r.Id)
                        && _db.CompletionLedgerHead.Any(h => h.WorkflowRunId == r.Id
                            && h.Version > (_db.CompletionAssessmentRecord.Where(a => a.WorkflowRunId == r.Id).Max(a => a.LedgerVersion) ?? -1)))
            .OrderByDescending(r => r.CompletedAt)
            .Take(batchSize)
            .Select(r => new { r.Id, r.TeamId, r.Status })
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        // The THIRD pass is the P0-A backfill, and one-shot BY CONSTRUCTION: a run whose rows all predate the
        // metric projection (none carries metric_jsonb) re-assesses once even though its ledger head never moved —
        // without it, a settled terminal run would stay metric-less forever and permanently deflate the
        // scorecard's assessed population. The re-assessment writes the metric columns, which removes the run
        // from this predicate for good; RecordAsync's own gates tolerate the unmoved watermark for exactly this
        // metric-less case.
        var metricless = await _db.WorkflowRun.AsNoTracking()
            .Where(r => r.CompletionPolicyVersion != null
                        && (r.Status == WorkflowRunStatus.Success || r.Status == WorkflowRunStatus.Failure || r.Status == WorkflowRunStatus.Cancelled)
                        && _db.CompletionAssessmentRecord.Any(a => a.WorkflowRunId == r.Id)
                        && !_db.CompletionAssessmentRecord.Any(a => a.WorkflowRunId == r.Id && a.MetricJson != null))
            .OrderByDescending(r => r.CompletedAt)
            .Take(batchSize)
            .Select(r => new { r.Id, r.TeamId, r.Status })
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        var appended = 0;

        foreach (var run in unassessed.Concat(revisit).Concat(metricless).DistinctBy(r => r.Id))
        {
            try
            {
                if (await RecordAsync(run.Id, run.TeamId, run.Status, cancellationToken).ConfigureAwait(false)) appended++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Shadow assessment failed for run {RunId}; the sweep continues — the run stays a candidate", run.Id);
            }
        }

        return appended;
    }

    private async Task<bool> RecordAsync(Guid runId, Guid teamId, WorkflowRunStatus status, CancellationToken cancellationToken)
    {
        // The gate that makes revisiting affordable: six counts over the ledgers the composer reads, compared with
        // the state the last assessment LEFT BEHIND. Unchanged ⇒ nothing can have moved the verdict, so the compose
        // is skipped entirely. A pre-watermark row carries none and re-assesses once.
        var before = JsonSerializer.Serialize(await CompletionLedgerWatermarks.CaptureAsync(_db, runId, teamId, cancellationToken).ConfigureAwait(false), AgentJson.Options);

        var previous = await _db.CompletionAssessmentRecord.AsNoTracking()
            .Where(a => a.WorkflowRunId == runId)
            .OrderByDescending(a => a.CreatedDate)
            .Select(a => new { a.AssessmentJson, a.LedgerWatermarkJson, a.MetricJson })
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);

        // A pre-projection row (no metric verdict) re-assesses ONCE even under an unmoved watermark — the same
        // backfill discipline the pre-watermark rows got: without this, a terminal run whose ledger never moves
        // again would stay metric-less forever, permanently deflating the scorecard's assessed population.
        if (previous is { LedgerWatermarkJson: not null, MetricJson: not null } && previous.LedgerWatermarkJson == before) return false;

        var composed = await _composer.ComposeAsync(runId, teamId, cancellationToken).ConfigureAwait(false);

        if (composed is null) return false;

        var assessmentJson = JsonSerializer.Serialize(composed.Assessment, AgentJson.Options);
        var metricJson = JsonSerializer.Serialize(composed.MetricAt1, AgentJson.Options);

        // The ledger moved but the VERDICT may not have — a manifest rewritten to the same state, a decision that
        // changed nothing. The watermark decides whether to LOOK; this decides whether there is anything to say.
        // BOTH projections are compared: a late @1-attempt receipt can move the metric while the operational
        // assessment (whose active attempt superseded that receipt) stands still — a metric-only change appends.
        if (previous?.AssessmentJson == assessmentJson && previous.MetricJson == metricJson) return false;

        var manifests = await _manifests.ListForWorkflowRunAsync(runId, teamId, cancellationToken).ConfigureAwait(false);
        var degradedStop = (await UnattendedDeliveryScorecardService.DegradedStopRunIdsAsync(_db, new[] { runId }, teamId, cancellationToken).ConfigureAwait(false)).Contains(runId);

        // P3b-4 (INACTIVE): decide what the sealed six-state terminal WOULD be — handoff reachability is the
        // predicate's last conjunct, probed over the run's own delivered targets. Recorded, never enforced.
        var receipts = await _contracts.ListReceiptsAsync(runId, teamId, cancellationToken).ConfigureAwait(false);
        var handoffReachable = await _handoff.IsHandoffReachableAsync(runId, teamId, receipts, cancellationToken).ConfigureAwait(false);
        var wouldBe = TerminalDecider.Decide(composed.Assessment, handoffReachable);
        var structural = await ReadStructuralInputsAsync(runId, teamId, cancellationToken).ConfigureAwait(false);

        // P1 + P4 (fail-close mirror): the authority refuses a CleanSuccess built over integrity violations, and
        // one missing a Required upstream stage, so the recorded would-be applies the SAME two predicates — parity
        // evidence that says "would have been CleanSuccess" for a run Enforced would in fact park is evidence
        // about a rule that doesn't exist.
        //
        // These two are ALL that is mirrored, on purpose. The authority's three STRUCTURAL gates — capability
        // registered, mode registered, mode holding Enforceable standing — stay OFF the recorded decision: baking
        // them in would stamp Unsupported on every non-supervisor run and erase the cohort-graduation signal this
        // column exists to produce. What makes their absence checkable rather than a silent overstatement is the
        // structural triple recorded beside it (mode, capability, readiness-at-compose): a reader re-derives all
        // three gates from those columns — see CompletionCohortEligibility, which is what the scorecard's
        // cohort-eligible count is.
        if (wouldBe == TerminalDecision.CleanSuccess && !EvidenceGatesPass(composed, structural)) wouldBe = TerminalDecision.Park;

        // Captured AFTER composing, on purpose (the A2 discipline): ComposeAsync write-throughs receipts, so a
        // pre-compose snapshot would leave every later sweep seeing a difference that was its own doing.
        var after = await CompletionLedgerWatermarks.CaptureAsync(_db, runId, teamId, cancellationToken).ConfigureAwait(false);

        _db.CompletionAssessmentRecord.Add(new CompletionAssessmentRecord
        {
            Id = Guid.NewGuid(),
            TeamId = teamId,
            WorkflowRunId = runId,
            EnforcementMode = composed.Mode.ToString(),
            Basis = composed.Assessment.Basis.ToString(),
            Outcome = composed.Assessment.Outcome.ToString(),
            Verification = composed.Assessment.Verification.ToString(),
            AssessmentJson = assessmentJson,
            // The legacy ladder's verdict AT COMPOSE TIME — the delta query's other half.
            LegacyIsSolved = UnattendedDeliveryScorecardService.IsSolved(manifests, status, degradedStop),
            WouldBeTerminalDecision = wouldBe.ToString(),
            LedgerWatermarkJson = JsonSerializer.Serialize(after, AgentJson.Options),
            LedgerVersion = after.LedgerVersion,
            MetricOutcome = composed.MetricAt1.Outcome.ToString(),
            MetricJson = metricJson,
            RunMode = structural.Mode,
            CapabilityKey = structural.CapabilityKey,
            ReadinessAtCompose = structural.Profile?.Readiness.ToString(),
            ResultsCoverageComplete = structural.ResultsCoverageComplete,
            RejectionCount = composed.Rejections.Count,
            ContractErrorCount = composed.ContractErrors.Count,
        });

        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary>The two EVIDENCE-dependent refusals the authority applies to a CleanSuccess, in its order: integrity violations first, then the mode profile's Required-but-unevidenced upstream stages. False ⇒ the recorded would-be parks.</summary>
    private static bool EvidenceGatesPass(ComposedAssessment composed, StructuralInputs structural)
    {
        if (CompletionIntegrity.Violations(composed.Rejections, composed.ContractErrors, structural.Requirements) is { Count: > 0 }) return false;

        return structural.Profile is not { } profile || UpstreamStageTrace.MissingRequired(profile, composed.ExercisedUpstreamStages).Count == 0;
    }

    /// <summary>
    /// Everything the row records BESIDE the composed projections, read once per appended row. Three are the inputs
    /// the authority's gates are functions of: the run's operating mode, the profile that mode resolves to (null ⇒
    /// UNREGISTERED — the authority parks such a run Unsupported), and the capability its staked obligations select.
    /// The fourth, the reduce-coverage fact the run's own outputs carry, gates nothing — it is recorded evidence.
    /// </summary>
    private async Task<StructuralInputs> ReadStructuralInputsAsync(Guid runId, Guid teamId, CancellationToken cancellationToken)
    {
        var requirements = await _contracts.ListRequirementsAsync(runId, teamId, cancellationToken).ConfigureAwait(false);
        var mode = await RunModeReader.DeriveAsync(_db, runId, teamId, cancellationToken).ConfigureAwait(false);
        var outputsJson = await _db.WorkflowRun.AsNoTracking().Where(r => r.Id == runId && r.TeamId == teamId).Select(r => r.OutputsJson).SingleAsync(cancellationToken).ConfigureAwait(false);

        return new StructuralInputs(mode, _modes.Resolve(mode), CompletionCapability.Derive(requirements), requirements, ReadResultsCoverageComplete(outputsJson));
    }

    /// <summary>The run row's own <c>resultsCoverage.complete</c> fact — whether the reduce its answer was synthesized over read ALL of its branches. Null when the run carries no such output (every run but a budget-declaring plan-map) or when the object cannot be read as a coverage record. Deserialized through the record itself so the wire names can never drift from a hand-read string.</summary>
    private static bool? ReadResultsCoverageComplete(string outputsJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(outputsJson);

            if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty(WorkflowOutputKeys.MapResultsCoverage, out var coverage)) return null;

            return coverage.Deserialize<MapResultsCoverage>(Workflows.WorkflowJson.Options)?.Complete;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private sealed record StructuralInputs(string Mode, ModeProfile? Profile, string CapabilityKey, IReadOnlyList<RequirementEnvelope> Requirements, bool? ResultsCoverageComplete);
}
