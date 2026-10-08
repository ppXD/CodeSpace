using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Enums;

namespace CodeSpace.Core.Services.Agents;

/// <summary>
/// The one definition of "this unit's OUTPUT review did not approve it", over both shapes a unit's result reaches a
/// door in: the durable <see cref="AgentRunResult"/> the executor wrote (the plan-map lane's integrator reads it off
/// the agent-run row) and the <see cref="SupervisorAgentResult"/> compact the supervisor tape folds from it (the
/// merge, resolver, ledger-direct and publish-gate doors). Two predicates would drift, and the lanes would disagree
/// about the same unit, so both read this one — and every terminal path that marks a result unreviewed (the executor's
/// review, the reconciler's spool recovery, the worker's drain on shutdown) marks it through this one too.
///
/// <para>Withheld when the review's own state says it did not approve (<see cref="OutputReviewState.Flagged"/>,
/// <see cref="OutputReviewState.Unreviewed"/>), or — for a result written before that state existed — when it
/// carries the critique or the reason it never examined the result. A result no review was configured for carries
/// none of the three and is never withheld by this predicate.</para>
/// </summary>
public static class AgentOutputReviewHold
{
    /// <inheritdoc cref="Withholds(SupervisorAgentResult)"/>
    public static bool Withholds(AgentRunResult result) => Withholds(result.OutputReview, result.ReviewFeedback, result.UnreviewedReason);

    /// <summary>Whether the unit's configured output review did not approve it, so no door may take its work.</summary>
    public static bool Withholds(SupervisorAgentResult result) => Withholds(result.OutputReview, result.ReviewFeedback, result.UnreviewedReason);

    /// <summary>
    /// The stricter question a door that can read the unit's TASK asks — the plan-map integrator, whose door is the
    /// manifest row the executor writes before the review. Under a <paramref name="configured"/> review only an approval
    /// lets the work through, so a result that says nothing is withheld too: none at all (the worker died after the
    /// push), or a fresh Failed one an executor fault wrote over the review's verdict. The one stateless result that
    /// passes is a Succeeded one, the shape an approving review wrote before the state existed. With no review
    /// configured this is <see cref="Withholds(AgentRunResult)"/>.
    /// </summary>
    public static bool Withholds(AgentRunResult? result, ReviewMode configured)
    {
        if (configured == ReviewMode.None) return result is not null && Withholds(result);

        return result is null || Withholds(result) || result.OutputReview is null && result.Status != AgentRunStatus.Succeeded;
    }

    /// <summary>The result, marked as one its configured output review never examined — its status untouched, for the path that owns it (a failure stays a failure; the A1 gate re-grades an open decision).</summary>
    public static AgentRunResult Unreviewed(AgentRunResult result, string reason) => result with { OutputReview = OutputReviewState.Unreviewed, UnreviewedReason = reason };

    /// <summary>A would-be success its configured output review never examined, HELD for a human: NeedsReview under <see cref="AgentRunExecutor.OutputUnreviewedExitReason"/>, and marked <see cref="Unreviewed"/>.</summary>
    public static AgentRunResult Held(AgentRunResult result, string reason) =>
        Unreviewed(result, reason) with { Status = AgentRunStatus.NeedsReview, CompletionDisposition = CompletionDisposition.NeedsReview, ExitReason = AgentRunExecutor.OutputUnreviewedExitReason };

    /// <summary>
    /// A result landed by a path that never runs the review — the reconciler's spool recovery, the worker's drain on
    /// shutdown — under the review <paramref name="configured"/> for it: untouched when none was, else marked
    /// <see cref="Unreviewed"/> whatever its status, and HELD when it would have been a success.
    /// </summary>
    public static AgentRunResult NeverReviewed(AgentRunResult result, ReviewMode configured, string reason)
    {
        if (configured == ReviewMode.None) return result;

        return result.Status == AgentRunStatus.Succeeded ? Held(result, reason) : Unreviewed(result, reason);
    }

    private static bool Withholds(OutputReviewState? state, string? reviewFeedback, string? unreviewedReason) =>
        state is OutputReviewState.Flagged or OutputReviewState.Unreviewed || !string.IsNullOrWhiteSpace(reviewFeedback) || !string.IsNullOrWhiteSpace(unreviewedReason);
}
