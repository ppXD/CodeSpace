using CodeSpace.Core.Services.Agents;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Enums;

namespace CodeSpace.IntegrationTests.Workflows.Infrastructure;

/// <summary>
/// Every terminal shape a unit takes when its configured OUTPUT review did not approve it, built by the functions
/// production runs to mark it (<see cref="AgentOutputReviewHold"/>, then A1's re-grade for an open decision) — so a
/// door test seeds the row the executor actually writes, not a hand-shaped copy of it. Its pushed branch rides along:
/// the executor pushes before the review runs.
/// </summary>
public static class UnapprovedUnitShapes
{
    public const string Flagged = "output-flagged";
    public const string Unreviewed = "output-unreviewed";
    public const string NeedsDecision = "needs-decision";
    public const string FailedWithPatch = "failed-with-patch";

    /// <summary>
    /// An executor fault AFTER the push — the review's own ledger read, a revise round's harness, the transcript attach —
    /// replaces the result with a fresh Failed one that carries no review state and none of the work, while the manifest
    /// row the executor wrote before the review still names the pushed branch. Only a door that reads that row (the
    /// plan-map integrator) can reach the branch, so it is that door's shape alone: it must read the task's configured
    /// review and take nothing a review did not approve.
    /// </summary>
    public const string ExecutorFault = "executor-fault";

    /// <summary>The shapes as xUnit member data.</summary>
    public static IEnumerable<object[]> All => new[] { Flagged, Unreviewed, NeedsDecision, FailedWithPatch }.Select(shape => new object[] { shape });

    /// <summary>The shapes a door that reads the publish-manifest row must withhold: every one above, and <see cref="ExecutorFault"/>.</summary>
    public static IEnumerable<object[]> ManifestDriven => All.Append(new object[] { ExecutorFault });

    /// <summary>The result the executor writes for a unit its review approved — the sibling every door still takes.</summary>
    public static AgentRunResult Approved(string branch) => Produced(branch) with { OutputReview = OutputReviewState.Approved };

    /// <summary>The result the executor writes for <paramref name="shape"/>, over the work a unit produced on <paramref name="branch"/>.</summary>
    public static AgentRunResult Of(string shape, string branch)
    {
        var produced = Produced(branch);

        return shape switch
        {
            Flagged => produced with { Status = AgentRunStatus.NeedsReview, CompletionDisposition = CompletionDisposition.NeedsReview, ExitReason = "output-flagged", ReviewFeedback = "hard-coded admin backdoor (blocker)", OutputReview = OutputReviewState.Flagged },
            Unreviewed => AgentOutputReviewHold.Held(produced, "The reviewer approved what it was shown, but part of the result was never shown to the reviewer (812345 characters past the review budget), so its approval does not cover it."),
            NeedsDecision => AgentCompletionContract.ApplyPendingDecision(AgentOutputReviewHold.Unreviewed(produced, AgentRunExecutor.DecisionOpenUnreviewedReason), Guid.NewGuid()),
            FailedWithPatch => AgentOutputReviewHold.Unreviewed(produced with { Status = AgentRunStatus.Failed, ExitReason = "error_max_turns", Error = "ran out of turns" }, "The run ended Failed (error_max_turns) before its configured output review ran, so its captured change was never reviewed."),
            ExecutorFault => new AgentRunResult { Status = AgentRunStatus.Failed, ExitReason = AgentRunExecutor.GenericExecutorExitReason, Error = "the review's ledger read faulted" },
            _ => throw new ArgumentOutOfRangeException(nameof(shape), shape, null),
        };
    }

    private static AgentRunResult Produced(string branch) => new() { Status = AgentRunStatus.Succeeded, ExitReason = "completed", Summary = "did it", ChangedFiles = new[] { "src/Auth.cs" }, Patch = "+ grant admin", BaseSha = "base1", ProducedBranch = branch };
}
