using CodeSpace.Core.Persistence.Db;
using Microsoft.EntityFrameworkCore;

namespace CodeSpace.Core.Services.Completion;

/// <summary>
/// P2b-4 (Lock Clause 2): the high-watermarks of every ledger a compose READ — captured at arbitration, re-read
/// at the terminal boundary, compared by value. A moved watermark means the assessment no longer describes the
/// ledgers (a late receipt, a re-staged requirement, a fresh manifest), so the terminal must recompose or park —
/// never stamp a status a stale assessment backed. Adding a ledger to the compose = adding a field here (the
/// record's value equality IS the comparison; a forgotten field is a review-visible gap, not a silent one).
/// </summary>
public sealed record CompletionLedgerWatermarks
{
    public required int RequirementCount { get; init; }
    public required int ReceiptCount { get; init; }
    public required int DecisionCount { get; init; }
    public required int MaxDecisionSequence { get; init; }
    public required int ManifestCount { get; init; }
    public required int AgentRunCount { get; init; }

    /// <summary>
    /// P2 (v4.3): the run's monotonic ledger version — the dimension the COUNTS above cannot carry. An amended
    /// requirement overwrites its row and a manifest state transition updates in place; both leave every count
    /// unchanged, so before this field the equality gates read "nothing moved" and a late amendment never
    /// reached reassessment. Non-required with default 0 so a stored pre-field watermark deserializes to 0 and
    /// compares UNEQUAL to any bumped capture — each legacy record reassesses once and converges.
    /// </summary>
    public long LedgerVersion { get; init; }

    /// <summary>One batched capture of every ledger the composer consumes for <paramref name="workflowRunId"/>.</summary>
    public static async Task<CompletionLedgerWatermarks> CaptureAsync(CodeSpaceDbContext db, Guid workflowRunId, Guid teamId, CancellationToken cancellationToken)
    {
        var requirementCount = await db.CompletionRequirement.AsNoTracking().CountAsync(r => r.WorkflowRunId == workflowRunId && r.TeamId == teamId, cancellationToken).ConfigureAwait(false);
        var receiptCount = await db.CompletionReceipt.AsNoTracking().CountAsync(r => r.WorkflowRunId == workflowRunId && r.TeamId == teamId, cancellationToken).ConfigureAwait(false);
        var decisions = await db.SupervisorDecisionRecord.AsNoTracking().Where(d => d.SupervisorRunId == workflowRunId && d.TeamId == teamId).Select(d => (int?)d.Sequence).ToListAsync(cancellationToken).ConfigureAwait(false);
        var manifestCount = await db.PublishManifest.AsNoTracking().CountAsync(m => m.WorkflowRunId == workflowRunId && m.TeamId == teamId, cancellationToken).ConfigureAwait(false);
        var agentRunCount = await db.AgentRun.AsNoTracking().CountAsync(a => a.WorkflowRunId == workflowRunId && a.TeamId == teamId, cancellationToken).ConfigureAwait(false);
        var ledgerVersion = await db.CompletionLedgerHead.AsNoTracking().Where(h => h.WorkflowRunId == workflowRunId).Select(h => (long?)h.Version).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false) ?? 0;

        return new CompletionLedgerWatermarks
        {
            RequirementCount = requirementCount,
            ReceiptCount = receiptCount,
            DecisionCount = decisions.Count,
            MaxDecisionSequence = decisions.Count == 0 ? 0 : decisions.Max() ?? 0,
            ManifestCount = manifestCount,
            AgentRunCount = agentRunCount,
            LedgerVersion = ledgerVersion,
        };
    }
}
