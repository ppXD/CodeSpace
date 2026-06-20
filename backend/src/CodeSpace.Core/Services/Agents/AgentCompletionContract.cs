using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Enums;

namespace CodeSpace.Core.Services.Agents;

/// <summary>
/// The completion contract (Slice A1): a run can NEVER land <see cref="AgentRunStatus.Succeeded"/> while a decision it
/// raised is still unanswered — that would bury the unanswered ask under "success". This is the PURE rule both terminal
/// write paths share (<c>AgentRunService.CompleteAsync</c> for the normal completion, the reconciler's spool recovery
/// for the crash path), so the invariant holds on every path that could write a terminal status. The I/O that finds the
/// outstanding decision is the caller's (<c>IToolCallLedgerService.FindBlockingDecisionIdAsync</c>) — keeping this side-
/// effect-free + exhaustively unit-testable.
/// </summary>
public static class AgentCompletionContract
{
    /// <summary>
    /// Re-grade a harness-produced terminal result against the run's outstanding decisions. A
    /// <see cref="AgentRunStatus.Succeeded"/> result with a still-open decision (<paramref name="pendingDecisionId"/>
    /// non-null) becomes <see cref="AgentRunStatus.NeedsReview"/> / <see cref="CompletionDisposition.NeedsDecision"/>
    /// carrying that id (and a <c>needs-decision</c> exit reason). Every other case — a non-Succeeded terminal, or a
    /// Succeeded one with no outstanding decision — passes through UNCHANGED, so the default
    /// <see cref="CompletionDisposition.Completed"/> stands and a clean success stays a clean success.
    /// </summary>
    public static AgentRunResult ApplyPendingDecision(AgentRunResult result, Guid? pendingDecisionId)
    {
        if (result.Status != AgentRunStatus.Succeeded || pendingDecisionId is not { } id) return result;

        return result with
        {
            Status = AgentRunStatus.NeedsReview,
            CompletionDisposition = CompletionDisposition.NeedsDecision,
            PendingDecisionId = id,
            ExitReason = "needs-decision",
        };
    }
}
