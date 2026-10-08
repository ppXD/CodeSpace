using CodeSpace.Core.Services.Agents;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Failures;
using Shouldly;

namespace CodeSpace.UnitTests.Agents;

/// <summary>
/// 🟢 Unit: the mark a terminal path that never ran the configured output review puts on its result. Two paths land a
/// result with no review at all — the reconciler's spool recovery and the worker's drain on shutdown — and both carry
/// work (a pushed branch, a captured patch) a door could take. Under a configured review the result is marked
/// unreviewed whatever its status, and a would-be success is held for a human; with no review configured it is left
/// exactly as it landed.
/// </summary>
[Trait("Category", "Unit")]
public sealed class AgentOutputReviewHoldTests
{
    private const string Reason = "the worker went away before the review ran";

    [Theory]
    [InlineData(ReviewMode.Gate, AgentRunStatus.Succeeded, AgentRunStatus.NeedsReview, AgentRunExecutor.OutputUnreviewedExitReason)]
    [InlineData(ReviewMode.Improve, AgentRunStatus.Succeeded, AgentRunStatus.NeedsReview, AgentRunExecutor.OutputUnreviewedExitReason)]
    [InlineData(ReviewMode.Gate, AgentRunStatus.Failed, AgentRunStatus.Failed, FailureCodes.ModelCredentialLeaseLost)]
    public void A_result_its_configured_review_never_saw_is_marked_and_a_success_is_held(ReviewMode mode, AgentRunStatus landed, AgentRunStatus expected, string expectedExitReason)
    {
        var result = Landed(landed);

        var marked = AgentOutputReviewHold.NeverReviewed(result, mode, Reason);

        marked.Status.ShouldBe(expected, "a failure stays the run's own verdict; only a would-be success is re-graded");
        marked.ExitReason.ShouldBe(expectedExitReason);
        marked.OutputReview.ShouldBe(OutputReviewState.Unreviewed);
        marked.UnreviewedReason.ShouldBe(Reason);
        AgentOutputReviewHold.Withholds(marked).ShouldBeTrue("no door may take what the review never saw");
    }

    [Theory]
    [InlineData(AgentRunStatus.Succeeded)]
    [InlineData(AgentRunStatus.Failed)]
    public void A_result_no_review_was_configured_for_is_left_as_it_landed(AgentRunStatus landed)
    {
        var result = Landed(landed);

        AgentOutputReviewHold.NeverReviewed(result, ReviewMode.None, Reason).ShouldBeSameAs(result);
    }

    private static AgentRunResult Landed(AgentRunStatus status) => new()
    {
        Status = status,
        ExitReason = status == AgentRunStatus.Succeeded ? "completed" : FailureCodes.ModelCredentialLeaseLost,
        ChangedFiles = new[] { "src/Auth.cs" },
        Patch = "+ grant admin",
        BaseSha = "base1",
    };
}
