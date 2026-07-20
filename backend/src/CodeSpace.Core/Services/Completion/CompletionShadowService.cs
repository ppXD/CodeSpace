using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Agents.Eval;
using CodeSpace.Core.Services.Agents.Publish;
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
        var candidates = await _db.WorkflowRun.AsNoTracking()
            .Where(r => r.CompletionPolicyVersion != null
                        && (r.Status == WorkflowRunStatus.Success || r.Status == WorkflowRunStatus.Failure || r.Status == WorkflowRunStatus.Cancelled)
                        && !_db.CompletionAssessmentRecord.Any(a => a.WorkflowRunId == r.Id))
            .OrderByDescending(r => r.CreatedDate)
            .Take(batchSize)
            .Select(r => new { r.Id, r.TeamId, r.Status })
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        var appended = 0;

        foreach (var run in candidates)
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
        var composed = await _composer.ComposeAsync(runId, teamId, cancellationToken).ConfigureAwait(false);

        if (composed is null) return false;

        var assessmentJson = JsonSerializer.Serialize(composed.Assessment, AgentJson.Options);

        var latest = await _db.CompletionAssessmentRecord.AsNoTracking()
            .Where(a => a.WorkflowRunId == runId)
            .OrderByDescending(a => a.CreatedDate)
            .Select(a => a.AssessmentJson)
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);

        if (latest == assessmentJson) return false;   // unchanged — append-only with change detection

        var manifests = await _manifests.ListForWorkflowRunAsync(runId, teamId, cancellationToken).ConfigureAwait(false);
        var degradedStop = (await UnattendedDeliveryScorecardService.DegradedStopRunIdsAsync(_db, new[] { runId }, teamId, cancellationToken).ConfigureAwait(false)).Contains(runId);

        // P3b-4 (INACTIVE): decide what the sealed six-state terminal WOULD be — handoff reachability is the
        // predicate's last conjunct, probed over the run's own delivered targets. Recorded, never enforced.
        var receipts = await _contracts.ListReceiptsAsync(runId, teamId, cancellationToken).ConfigureAwait(false);
        var handoffReachable = await _handoff.IsHandoffReachableAsync(runId, teamId, receipts, cancellationToken).ConfigureAwait(false);
        var wouldBe = TerminalDecider.Decide(composed.Assessment, handoffReachable);

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
            RejectionCount = composed.Rejections.Count,
            ContractErrorCount = composed.ContractErrors.Count,
        });

        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }
}
