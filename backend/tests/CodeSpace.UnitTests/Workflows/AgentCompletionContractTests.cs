using CodeSpace.Core.Services.Agents;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Enums;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

[Trait("Category", "Unit")]
public class AgentCompletionContractTests
{
    private static readonly Guid Decision = Guid.NewGuid();

    [Fact]
    public void Succeeded_with_a_pending_decision_is_regraded_to_needs_review_preserving_the_work()
    {
        // The core invariant: a would-be success with an unanswered decision becomes NeedsReview(NeedsDecision)
        // carrying the decision id — the captured work (summary / changed files / patch) is preserved untouched,
        // only the verdict changes, so a reviewer still sees what the agent did.
        var result = new AgentRunResult
        {
            Status = AgentRunStatus.Succeeded, ExitReason = "completed", Summary = "did the work",
            ChangedFiles = new[] { "a.ts" }, Patch = "+one\n", ProducedBranch = "agent/x",
        };

        var graded = AgentCompletionContract.ApplyPendingDecision(result, Decision);

        graded.Status.ShouldBe(AgentRunStatus.NeedsReview);
        graded.CompletionDisposition.ShouldBe(CompletionDisposition.NeedsDecision);
        graded.PendingDecisionId.ShouldBe(Decision);
        graded.ExitReason.ShouldBe("needs-decision");
        graded.Summary.ShouldBe("did the work");
        graded.ChangedFiles.ShouldBe(new[] { "a.ts" });
        graded.Patch.ShouldBe("+one\n");
        graded.ProducedBranch.ShouldBe("agent/x");
    }

    [Fact]
    public void Succeeded_with_no_pending_decision_passes_through_unchanged()
    {
        var result = new AgentRunResult { Status = AgentRunStatus.Succeeded, ExitReason = "completed" };

        var graded = AgentCompletionContract.ApplyPendingDecision(result, null);

        graded.ShouldBeSameAs(result, "no pending decision → the result is returned reference-unchanged (no allocation on the clean path)");
        graded.CompletionDisposition.ShouldBe(CompletionDisposition.Completed);
        graded.PendingDecisionId.ShouldBeNull();
    }

    [Theory]
    [InlineData(AgentRunStatus.Failed)]
    [InlineData(AgentRunStatus.Cancelled)]
    [InlineData(AgentRunStatus.TimedOut)]
    public void A_non_succeeded_terminal_is_never_regraded_even_with_a_pending_decision(AgentRunStatus status)
    {
        // The gate re-grades ONLY a would-be success — a failure / cancel / timeout's status is already the final
        // word, so a leftover pending decision must not flip it to NeedsReview.
        var result = new AgentRunResult { Status = status, ExitReason = "x", Error = "boom" };

        var graded = AgentCompletionContract.ApplyPendingDecision(result, Decision);

        graded.ShouldBeSameAs(result, "a non-Succeeded terminal passes through reference-unchanged");
        graded.Status.ShouldBe(status);
        graded.CompletionDisposition.ShouldBe(CompletionDisposition.Completed);
        graded.PendingDecisionId.ShouldBeNull();
    }
}
