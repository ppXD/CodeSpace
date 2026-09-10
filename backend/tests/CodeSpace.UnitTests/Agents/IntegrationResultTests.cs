using CodeSpace.Messages.Agents;
using Shouldly;

namespace CodeSpace.UnitTests.Agents;

/// <summary>
/// 🟢 Unit: the pure honesty invariant on <see cref="IntegrationResult.Build"/> — the single guarded constructor the
/// branch integrator uses. The crown jewel: a result can NEVER be <see cref="IntegrationStatus.Clean"/> while any
/// contribution was <see cref="ContributionDisposition.Unintegrable"/>, so a silently-dropped agent can never hide
/// inside a green integration. Also pins that Clean clears the branch + counts applied correctly, and a non-clean
/// result never carries an integrated branch.
/// </summary>
[Trait("Category", "Unit")]
public sealed class IntegrationResultTests
{
    private static ContributionOutcome Applied(string label) => new() { Label = label, Disposition = ContributionDisposition.Applied };
    private static ContributionOutcome Unintegrable(string label) => new() { Label = label, Disposition = ContributionDisposition.Unintegrable, Reason = "no patch and no branch" };
    private static ContributionOutcome Conflicted(string label) => new() { Label = label, Disposition = ContributionDisposition.Conflicted, FallbackBranch = "codespace/agent/x", Reason = "textual conflict" };

    [Fact]
    public void Clean_with_all_applied_keeps_the_branch_and_counts_them()
    {
        var result = IntegrationResult.Build(IntegrationStatus.Clean, "codespace/integration/r1", new[] { Applied("a"), Applied("b"), Applied("c") });

        result.Status.ShouldBe(IntegrationStatus.Clean);
        result.IntegratedBranch.ShouldBe("codespace/integration/r1");
        result.AppliedCount.ShouldBe(3);
    }

    [Fact]
    public void Clean_is_IMPOSSIBLE_when_any_contribution_is_unintegrable()
    {
        // CROWN JEWEL — the type refuses to emit a Clean result that hides a dropped agent. Even if a caller proposes
        // Clean, the presence of an Unintegrable contribution coerces it to Conflicted with the branch cleared.
        var result = IntegrationResult.Build(IntegrationStatus.Clean, "codespace/integration/r1", new[] { Applied("a"), Unintegrable("b-dropped") });

        result.Status.ShouldBe(IntegrationStatus.Conflicted, "a Clean result with a dropped contribution is a contradiction the type refuses");
        result.IntegratedBranch.ShouldBeNull("no branch is published when the set is not clean");
        result.AppliedCount.ShouldBe(1, "'a' really did apply — only 'b-dropped' failed to integrate, and the count must stay true even when the proposed Clean is coerced to Conflicted");
        result.Outcomes.ShouldContain(o => o.Label == "b-dropped" && o.Disposition == ContributionDisposition.Unintegrable, "the dropped agent is loudly named");
    }

    [Fact]
    public void Conflicted_preserves_the_true_applied_count_and_names_the_failing_contribution()
    {
        // The honesty fix: a Conflicted result must NEVER be forced to AppliedCount=0 regardless of how many
        // contributions actually applied before a later one aborted the set — a reader must be able to tell
        // "everything failed" from "two applied, the third conflicted".
        var result = IntegrationResult.Build(IntegrationStatus.Conflicted, "should-be-dropped", new[] { Applied("a"), Applied("b"), Conflicted("c") }, "a contribution conflicted while integrating");

        result.AppliedCount.ShouldBe(2, "'a' and 'b' applied before 'c' conflicted");
        result.IntegratedBranch.ShouldBeNull();
        result.Outcomes.Single(o => o.Label == "c").Reason.ShouldBe("textual conflict", "the failing contribution is named, in its own words, among the outcomes");
    }

    [Fact]
    public void A_conflicted_contribution_with_a_fallback_branch_still_blocks_clean()
    {
        // A contribution preserved on a fallback branch is still NOT applied, so the all-or-nothing set is non-clean.
        var result = IntegrationResult.Build(IntegrationStatus.Clean, "b", new[] { Applied("a"), Conflicted("b") });

        result.Status.ShouldBe(IntegrationStatus.Conflicted, "an all-or-nothing set is clean only when every contribution applied");
        result.IntegratedBranch.ShouldBeNull();
    }

    [Fact]
    public void A_proposed_conflicted_status_never_carries_a_branch_even_if_one_is_passed()
    {
        var result = IntegrationResult.Build(IntegrationStatus.Conflicted, "should-be-dropped", new[] { Conflicted("a") }, "remote integration branch advanced");

        result.IntegratedBranch.ShouldBeNull();
        result.AppliedCount.ShouldBe(0);
        result.Reason.ShouldBe("remote integration branch advanced");
    }

    [Fact]
    public void Empty_carries_no_branch()
    {
        var result = IntegrationResult.Build(IntegrationStatus.Empty, null, System.Array.Empty<ContributionOutcome>(), "no contributions to integrate");

        result.Status.ShouldBe(IntegrationStatus.Empty);
        result.IntegratedBranch.ShouldBeNull();
        result.AppliedCount.ShouldBe(0);
    }
}
