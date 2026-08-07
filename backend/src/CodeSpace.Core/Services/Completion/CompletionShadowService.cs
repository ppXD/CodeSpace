using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Agents.Eval;
using CodeSpace.Core.Services.Agents.Publish;
using CodeSpace.Messages.Contracts;
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
    private readonly ILogger<CompletionShadowService> _logger;

    public CompletionShadowService(CodeSpaceDbContext db, ICompletionAssessmentComposer composer, IPublishManifestStore manifests, ICompletionContractStore contracts, ICompletionHandoffProbe handoff, ILogger<CompletionShadowService> logger)
    {
        _db = db;
        _composer = composer;
        _manifests = manifests;
        _contracts = contracts;
        _handoff = handoff;
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
        // Bounded by TIME rather than by assessment count. Widening the first query instead would make every sweep
        // re-examine the newest batchSize terminal runs forever while older ones were never reached again — the
        // exact starvation this split avoids. A precise staleness predicate (one indexed comparison instead of a
        // window) needs a monotonic per-run ledger revision, which is P2's work and not worth pre-empting here.
        var revisitAfter = DateTimeOffset.UtcNow - RevisitWindow;

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
                        && r.CompletedAt != null && r.CompletedAt >= revisitAfter
                        && _db.CompletionAssessmentRecord.Any(a => a.WorkflowRunId == r.Id))
            .OrderByDescending(r => r.CompletedAt)
            .Take(batchSize)
            .Select(r => new { r.Id, r.TeamId, r.Status })
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        var appended = 0;

        foreach (var run in unassessed.Concat(revisit))
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

    /// <summary>
    /// How long after a run terminalizes its assessment stays open to revision. Evidence does arrive late — a
    /// reconciler settling a manifest, a grade folding after the terminal — and a window is what makes revisiting
    /// affordable without re-examining every terminal run this deployment has ever produced. A committed value, not
    /// a toggle: changing how long the protocol keeps looking is a code change with a diff, never an operator knob.
    /// </summary>
    public static readonly TimeSpan RevisitWindow = TimeSpan.FromHours(24);

    private async Task<bool> RecordAsync(Guid runId, Guid teamId, WorkflowRunStatus status, CancellationToken cancellationToken)
    {
        // The gate that makes revisiting affordable: six counts over the ledgers the composer reads, compared with
        // the state the last assessment LEFT BEHIND. Unchanged ⇒ nothing can have moved the verdict, so the compose
        // is skipped entirely. A pre-watermark row carries none and re-assesses once.
        var before = JsonSerializer.Serialize(await CompletionLedgerWatermarks.CaptureAsync(_db, runId, teamId, cancellationToken).ConfigureAwait(false), AgentJson.Options);

        var previous = await _db.CompletionAssessmentRecord.AsNoTracking()
            .Where(a => a.WorkflowRunId == runId)
            .OrderByDescending(a => a.CreatedDate)
            .Select(a => new { a.AssessmentJson, a.LedgerWatermarkJson })
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);

        if (previous is { LedgerWatermarkJson: not null } && previous.LedgerWatermarkJson == before) return false;

        var composed = await _composer.ComposeAsync(runId, teamId, cancellationToken).ConfigureAwait(false);

        if (composed is null) return false;

        var assessmentJson = JsonSerializer.Serialize(composed.Assessment, AgentJson.Options);

        // The ledger moved but the VERDICT may not have — a manifest rewritten to the same state, a decision that
        // changed nothing. The watermark decides whether to LOOK; this decides whether there is anything to say.
        if (previous?.AssessmentJson == assessmentJson) return false;

        var manifests = await _manifests.ListForWorkflowRunAsync(runId, teamId, cancellationToken).ConfigureAwait(false);
        var degradedStop = (await UnattendedDeliveryScorecardService.DegradedStopRunIdsAsync(_db, new[] { runId }, teamId, cancellationToken).ConfigureAwait(false)).Contains(runId);

        // P3b-4 (INACTIVE): decide what the sealed six-state terminal WOULD be — handoff reachability is the
        // predicate's last conjunct, probed over the run's own delivered targets. Recorded, never enforced.
        var receipts = await _contracts.ListReceiptsAsync(runId, teamId, cancellationToken).ConfigureAwait(false);
        var handoffReachable = await _handoff.IsHandoffReachableAsync(runId, teamId, receipts, cancellationToken).ConfigureAwait(false);
        var wouldBe = TerminalDecider.Decide(composed.Assessment, handoffReachable);

        // P1 (fail-close mirror): the authority refuses a CleanSuccess built over integrity violations, so the
        // recorded would-be decision must apply the SAME predicate — parity evidence that says "would have been
        // CleanSuccess" for a run Enforced would in fact park is evidence about a rule that doesn't exist.
        if (wouldBe == TerminalDecision.CleanSuccess)
        {
            var requirements = await _contracts.ListRequirementsAsync(runId, teamId, cancellationToken).ConfigureAwait(false);

            if (CompletionIntegrity.Violations(composed.Rejections, composed.ContractErrors, requirements) is { Count: > 0 }) wouldBe = TerminalDecision.Park;
        }

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
            // Captured AFTER composing, on purpose. ComposeAsync is NOT read-only — it write-throughs the receipts
            // it derives from the tape, so storing the pre-compose snapshot would leave every later sweep seeing a
            // difference that was its own doing, and the gate would never skip anything.
            LedgerWatermarkJson = JsonSerializer.Serialize(await CompletionLedgerWatermarks.CaptureAsync(_db, runId, teamId, cancellationToken).ConfigureAwait(false), AgentJson.Options),
            RejectionCount = composed.Rejections.Count,
            ContractErrorCount = composed.ContractErrors.Count,
        });

        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }
}
