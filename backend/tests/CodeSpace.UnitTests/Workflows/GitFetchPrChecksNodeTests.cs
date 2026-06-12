using CodeSpace.Core.Services.Workflows.Nodes.Builtin;
using CodeSpace.Messages.Dtos.Providers;
using CodeSpace.Messages.Enums;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

/// <summary>
/// The PURE gate rollup for <c>git.fetch_pr_checks</c> (<see cref="GitFetchPrChecksNode.SummarizeChecks"/>) —
/// the logic a workflow gates merges on. Exhaustively pins the state machine: "pending" wins over "failure"
/// wins over "success"; failure ∪ cancelled both block; skipped / neutral never block; an EMPTY set passes
/// vacuously (a PR with no required checks is mergeable). The real provider fetch is covered by the
/// PullRequest integration suite; here we pin the branch-ready derivation in isolation.
/// </summary>
[Trait("Category", "Unit")]
public sealed class GitFetchPrChecksNodeTests
{
    private static RemotePullRequestCheck Check(PullRequestCheckStatus status) => new() { Name = $"check-{status}", Status = status };

    private static IReadOnlyList<RemotePullRequestCheck> Checks(params PullRequestCheckStatus[] statuses) =>
        statuses.Select(Check).ToList();

    [Fact]
    public void Empty_check_set_passes_vacuously()
    {
        var s = GitFetchPrChecksNode.SummarizeChecks(Checks());

        s.Total.ShouldBe(0);
        s.State.ShouldBe("success");
        s.AllPassed.ShouldBeTrue("a PR with no required checks is mergeable — nothing is pending or failing");
    }

    [Fact]
    public void All_success_is_green()
    {
        var s = GitFetchPrChecksNode.SummarizeChecks(Checks(PullRequestCheckStatus.Success, PullRequestCheckStatus.Success));

        s.Total.ShouldBe(2);
        s.Passing.ShouldBe(2);
        s.State.ShouldBe("success");
        s.AllPassed.ShouldBeTrue();
    }

    [Theory]
    [InlineData(PullRequestCheckStatus.Failure)]
    [InlineData(PullRequestCheckStatus.Cancelled)]
    public void A_failed_or_cancelled_check_makes_it_a_failure(PullRequestCheckStatus blocking)
    {
        var s = GitFetchPrChecksNode.SummarizeChecks(Checks(PullRequestCheckStatus.Success, blocking));

        s.Failing.ShouldBe(1);
        s.State.ShouldBe("failure");
        s.AllPassed.ShouldBeFalse();
    }

    [Fact]
    public void A_pending_check_makes_it_pending()
    {
        var s = GitFetchPrChecksNode.SummarizeChecks(Checks(PullRequestCheckStatus.Success, PullRequestCheckStatus.Pending));

        s.Pending.ShouldBe(1);
        s.State.ShouldBe("pending");
        s.AllPassed.ShouldBeFalse("a still-running check means the gate isn't green yet");
    }

    [Fact]
    public void Pending_takes_precedence_over_failure()
    {
        // Both a failing AND a still-running check: report "pending" (the result isn't final yet), not "failure".
        var s = GitFetchPrChecksNode.SummarizeChecks(Checks(PullRequestCheckStatus.Failure, PullRequestCheckStatus.Pending));

        s.State.ShouldBe("pending");
        s.Failing.ShouldBe(1);
        s.Pending.ShouldBe(1);
        s.AllPassed.ShouldBeFalse();
    }

    [Fact]
    public void Skipped_and_neutral_do_not_block()
    {
        var s = GitFetchPrChecksNode.SummarizeChecks(Checks(PullRequestCheckStatus.Success, PullRequestCheckStatus.Skipped, PullRequestCheckStatus.Neutral));

        s.Total.ShouldBe(3);
        s.Passing.ShouldBe(1);
        s.Failing.ShouldBe(0);
        s.Pending.ShouldBe(0);
        s.State.ShouldBe("success");
        s.AllPassed.ShouldBeTrue("skipped / neutral are completed-but-not-failing, so they never block the gate");
    }

    [Fact]
    public void Counts_are_accurate_across_a_mixed_set()
    {
        var s = GitFetchPrChecksNode.SummarizeChecks(Checks(
            PullRequestCheckStatus.Success, PullRequestCheckStatus.Success, PullRequestCheckStatus.Success,
            PullRequestCheckStatus.Failure,
            PullRequestCheckStatus.Pending, PullRequestCheckStatus.Pending,
            PullRequestCheckStatus.Skipped));

        s.Total.ShouldBe(7);
        s.Passing.ShouldBe(3);
        s.Failing.ShouldBe(1);
        s.Pending.ShouldBe(2);
        s.State.ShouldBe("pending");
        s.AllPassed.ShouldBeFalse();
    }
}
