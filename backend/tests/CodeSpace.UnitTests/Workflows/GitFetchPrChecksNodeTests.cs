using System.Text.Json;
using CodeSpace.Core.Services.PullRequests;
using CodeSpace.Core.Services.Workflows.Nodes;
using CodeSpace.Core.Services.Workflows.Nodes.Builtin;
using CodeSpace.Core.Services.Workflows.Runtime;
using CodeSpace.Messages.Dtos.Providers;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Exceptions;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

/// <summary>
/// The PURE gate rollup for <c>git.fetch_pr_checks</c> (<see cref="GitFetchPrChecksNode.SummarizeChecks"/>) —
/// the logic a workflow gates merges on. Exhaustively pins the state machine: "pending" wins over "failure"
/// wins over "success"; failure ∪ cancelled both block; skipped / neutral never block; an EMPTY set passes
/// vacuously (a PR with no required checks is mergeable). An empty set is only ever the provider's answer — a checks
/// list the provider could not read fails the node, so the gate has no allPassed to branch on. The real provider
/// reads are pinned by the GitLab / GitHub ListChecks suites and the CI-gate flow test.
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

    [Fact]
    public async Task A_checks_list_the_provider_could_not_read_fails_the_node_instead_of_passing_the_gate()
    {
        var unreadable = new ProviderApiException(ProviderKind.GitLab, 429, "ListChecksAsync", "429 Too Many Requests", new HttpRequestException("rate limited"));
        var node = new GitFetchPrChecksNode(new ChecksReadingPrService(() => throw unreadable));

        var thrown = await Should.ThrowAsync<ProviderApiException>(() => node.RunAsync(Context(), CancellationToken.None));

        thrown.ShouldBeSameAs(unreadable, "the engine fails the node on the provider's own error — never an allPassed the gate could read as green");
    }

    [Fact]
    public async Task A_checks_list_the_provider_read_is_summarised_into_the_gate_outputs()
    {
        var node = new GitFetchPrChecksNode(new ChecksReadingPrService(() => Checks(PullRequestCheckStatus.Success, PullRequestCheckStatus.Failure)));

        var result = await node.RunAsync(Context(), CancellationToken.None);

        result.Status.ShouldBe(NodeStatus.Success);
        result.Outputs["state"].GetString().ShouldBe("failure");
        result.Outputs["allPassed"].GetBoolean().ShouldBeFalse();
        result.Outputs["checks"].GetArrayLength().ShouldBe(2);
    }

    private static NodeRunContext Context() => new()
    {
        Inputs = new Dictionary<string, JsonElement> { ["repositoryId"] = JsonSerializer.SerializeToElement(Guid.NewGuid()), ["number"] = JsonSerializer.SerializeToElement(7) },
        Config = new Dictionary<string, JsonElement>(),
        RawInputs = JsonDocument.Parse("{}").RootElement,
        RawConfig = JsonDocument.Parse("{}").RootElement,
        Scope = new NodeRunScope { Trigger = new Dictionary<string, JsonElement>(), Sys = new Dictionary<string, JsonElement> { [SystemScopeKeys.TeamId] = JsonSerializer.SerializeToElement(Guid.NewGuid()) } },
        Logger = NullLogger.Instance,
        Observability = NodeObservability.NoOp,
    };

    /// <summary>Answers the checks read with <c>read</c>; every other member throws (this node only reads checks).</summary>
    private sealed class ChecksReadingPrService(Func<IReadOnlyList<RemotePullRequestCheck>> read) : IPullRequestService
    {
        public Task<IReadOnlyList<RemotePullRequestCheck>> ListChecksAsync(Guid r, Guid t, int n, CancellationToken c) => Task.FromResult(read());

        public Task<IReadOnlyList<RemotePullRequest>> ListAsync(Guid r, Guid t, PullRequestState? s, int p, int pp, CancellationToken c) => throw new NotImplementedException();
        public Task<RemotePullRequest> GetAsync(Guid r, Guid t, int n, CancellationToken c) => throw new NotImplementedException();
        public Task<IReadOnlyList<RemotePullRequestCommit>> ListCommitsAsync(Guid r, Guid t, int n, CancellationToken c) => throw new NotImplementedException();
        public Task<IReadOnlyList<RemotePullRequestFile>> ListFilesAsync(Guid r, Guid t, int n, CancellationToken c) => throw new NotImplementedException();
        public Task<RemotePullRequestCounts> GetCountsAsync(Guid r, Guid t, CancellationToken c) => throw new NotImplementedException();
        public Task<RemotePullRequestComment> PostCommentAsync(Guid r, Guid t, int n, string b, CancellationToken c) => throw new NotImplementedException();
        public Task<RemotePullRequestReview> SubmitReviewAsync(Guid r, Guid t, int n, PullRequestReviewVerdict v, string? b, Guid? a, CancellationToken c) => throw new NotImplementedException();
        public Task<RemotePullRequest> OpenPullRequestAsync(Guid r, Guid t, OpenPullRequestInput i, Guid? a, CancellationToken c) => throw new NotImplementedException();
        public Task<RemotePullRequestMergeResult> MergePullRequestAsync(Guid r, Guid t, int n, MergePullRequestInput i, Guid? a, CancellationToken c) => throw new NotImplementedException();
    }
}
