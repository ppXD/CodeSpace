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

[Trait("Category", "Unit")]
public class GitPrReviewNodeTests
{
    /// <summary>Hand-rolled stub (no mocking lib) — records the review call, returns a canned result; reads throw.</summary>
    private sealed class StubPrService : IPullRequestService
    {
        public Guid RepoId;
        public Guid TeamId;
        public int Number;
        public PullRequestReviewVerdict Verdict;
        public string? Body;
        public string? ExpectedHeadSha;
        public Guid? ActorUserId;
        public int Calls;
        public Exception? ThrowOnReview;

        public Task<RemotePullRequestReview> SubmitReviewAsync(Guid repositoryId, Guid teamId, int number, SubmitPullRequestReviewInput input, Guid? actorUserId, CancellationToken cancellationToken)
        {
            RepoId = repositoryId; TeamId = teamId; Number = number; Verdict = input.Verdict; Body = input.Body; ExpectedHeadSha = input.ExpectedHeadSha; ActorUserId = actorUserId; Calls++;
            if (ThrowOnReview != null) throw ThrowOnReview;
            return Task.FromResult(new RemotePullRequestReview { Verdict = input.Verdict, ExternalId = "rev-1", WebUrl = "https://example.test/review/1" });
        }

        public Task<IReadOnlyList<RemotePullRequest>> ListAsync(Guid r, Guid t, PullRequestState? s, int p, int pp, CancellationToken c) => throw new NotImplementedException();
        public Task<RemotePullRequest> GetAsync(Guid r, Guid t, int n, CancellationToken c) => throw new NotImplementedException();
        public Task<IReadOnlyList<RemotePullRequestCommit>> ListCommitsAsync(Guid r, Guid t, int n, CancellationToken c) => throw new NotImplementedException();
        public Task<IReadOnlyList<RemotePullRequestFile>> ListFilesAsync(Guid r, Guid t, int n, CancellationToken c) => throw new NotImplementedException();
        public Task<RemotePullRequestCounts> GetCountsAsync(Guid r, Guid t, CancellationToken c) => throw new NotImplementedException();
        public Task<IReadOnlyList<RemotePullRequestCheck>> ListChecksAsync(Guid r, Guid t, int n, CancellationToken c) => throw new NotImplementedException();
        public Task<RemotePullRequestComment> PostCommentAsync(Guid r, Guid t, int n, string b, CancellationToken c) => throw new NotImplementedException();
        public Task<RemotePullRequest> OpenPullRequestAsync(Guid r, Guid t, OpenPullRequestInput i, Guid? a, CancellationToken c) => throw new NotImplementedException();
        public Task<RemotePullRequestMergeResult> MergePullRequestAsync(Guid r, Guid t, int n, MergePullRequestInput i, Guid? a, CancellationToken c) => throw new NotImplementedException();
    }

    private const string Repo = "11111111-1111-1111-1111-111111111111";
    private const string Team = "22222222-2222-2222-2222-222222222222";

    [Fact]
    public async Task Submits_the_parsed_verdict_and_body_and_outputs_the_verdict_and_url()
    {
        var stub = new StubPrService();

        var result = await new GitPrReviewNode(stub).RunAsync(BuildContext(Repo, 42, "approve", "ship it"), CancellationToken.None);

        result.Status.ShouldBe(NodeStatus.Success);
        stub.Calls.ShouldBe(1);
        stub.RepoId.ShouldBe(Guid.Parse(Repo));
        stub.Number.ShouldBe(42);
        stub.Verdict.ShouldBe(PullRequestReviewVerdict.Approve);
        stub.Body.ShouldBe("ship it");
        stub.ActorUserId.ShouldBeNull("no actAsUserId wired → null → service uses the connection credential");

        result.Outputs["verdict"].GetString().ShouldBe("Approve");
        result.Outputs["url"].GetString().ShouldBe("https://example.test/review/1");
    }

    [Fact]
    public async Task Passes_actAsUserId_through_when_wired()
    {
        var stub = new StubPrService();
        var actor = Guid.NewGuid();
        var inputs = new Dictionary<string, JsonElement>
        {
            ["repositoryId"] = JsonSerializer.SerializeToElement(Repo),
            ["number"] = JsonSerializer.SerializeToElement(3),
            ["verdict"] = JsonSerializer.SerializeToElement("approve"),
            ["actAsUserId"] = JsonSerializer.SerializeToElement(actor.ToString()),
        };

        var result = await new GitPrReviewNode(stub).RunAsync(ContextFromInputs(inputs), CancellationToken.None);

        result.Status.ShouldBe(NodeStatus.Success);
        stub.ActorUserId.ShouldBe(actor, "a wired actAsUserId must reach the service so it acts as that user's identity");
    }

    [Fact]
    public async Task Threads_the_run_team_from_sys_scope_into_the_service_call()
    {
        var stub = new StubPrService();

        var result = await new GitPrReviewNode(stub).RunAsync(BuildContext(Repo, 42, "approve", "ship it"), CancellationToken.None);

        result.Status.ShouldBe(NodeStatus.Success);
        stub.TeamId.ShouldBe(Guid.Parse(Team), "the run's team flows from {{sys.team_id}} so the service fail-closes the repo load to it");
    }

    [Fact]
    public async Task Fails_closed_when_sys_scope_has_no_team()
    {
        var stub = new StubPrService();
        var inputs = new Dictionary<string, JsonElement>
        {
            ["repositoryId"] = JsonSerializer.SerializeToElement(Repo),
            ["number"] = JsonSerializer.SerializeToElement(42),
            ["verdict"] = JsonSerializer.SerializeToElement("approve"),
        };

        var result = await new GitPrReviewNode(stub).RunAsync(ContextWithSys(inputs, new()), CancellationToken.None);

        result.Status.ShouldBe(NodeStatus.Failure);
        result.Error.ShouldContain("team context");
        stub.Calls.ShouldBe(0, "without a team the node must short-circuit before touching the service");
    }

    [Fact]
    public async Task Approve_without_a_body_delegates_a_null_body()
    {
        var stub = new StubPrService();

        var result = await new GitPrReviewNode(stub).RunAsync(BuildContext(Repo, 9, "approve", body: null), CancellationToken.None);

        result.Status.ShouldBe(NodeStatus.Success);
        stub.Verdict.ShouldBe(PullRequestReviewVerdict.Approve);
        stub.Body.ShouldBeNull();
    }

    [Theory]
    [InlineData("request_changes", PullRequestReviewVerdict.RequestChanges)]
    [InlineData("RequestChanges", PullRequestReviewVerdict.RequestChanges)]
    [InlineData("comment", PullRequestReviewVerdict.Comment)]
    [InlineData("Approve", PullRequestReviewVerdict.Approve)]
    public async Task Parses_the_verdict_tolerantly_of_snake_case_and_casing(string raw, PullRequestReviewVerdict expected)
    {
        var stub = new StubPrService();

        var result = await new GitPrReviewNode(stub).RunAsync(BuildContext(Repo, 7, raw, "x"), CancellationToken.None);

        result.Status.ShouldBe(NodeStatus.Success);
        stub.Verdict.ShouldBe(expected);
    }

    [Fact]
    public async Task Fails_for_an_unknown_verdict()
    {
        var result = await new GitPrReviewNode(new StubPrService()).RunAsync(BuildContext(Repo, 1, "merge", null), CancellationToken.None);

        result.Status.ShouldBe(NodeStatus.Failure);
        result.Error.ShouldContain("verdict");
    }

    [Fact]
    public async Task Provider_insufficient_scope_fails_with_an_actionable_scope_message()
    {
        var stub = new StubPrService { ThrowOnReview = new ProviderInsufficientScopeException(ProviderKind.GitLab, "IPullRequestReviewCapability", new[] { "api" }, Array.Empty<string>(), "hint") };

        var result = await new GitPrReviewNode(stub).RunAsync(BuildContext(Repo, 42, "approve", null), CancellationToken.None);

        result.Status.ShouldBe(NodeStatus.Failure);
        result.Error.ShouldContain("api");
        result.Error.ShouldContain("scope");
        result.Error!.ShouldNotContain("HTTP", customMessage: "a scope gap must read as a scope message, not a raw SDK string");
    }

    [Fact]
    public async Task Provider_403_fails_with_a_permission_message_not_a_scope_message()
    {
        var stub = new StubPrService { ThrowOnReview = new ProviderApiException(ProviderKind.GitLab, 403, "SubmitReviewAsync", "403 Forbidden", new Exception()) };

        var result = await new GitPrReviewNode(stub).RunAsync(BuildContext(Repo, 42, "approve", null), CancellationToken.None);

        result.Status.ShouldBe(NodeStatus.Failure);
        result.Error.ShouldContain("permission");
        result.Error!.ShouldNotContain("scope", customMessage: "a 403 is a permission problem — must not be mislabelled a scope gap");
    }

    [Fact]
    public async Task Provider_404_fails_with_a_not_found_message()
    {
        var stub = new StubPrService { ThrowOnReview = new ProviderApiException(ProviderKind.GitHub, 404, "SubmitReviewAsync", "Not Found", new Exception()) };

        var result = await new GitPrReviewNode(stub).RunAsync(BuildContext(Repo, 7, "approve", null), CancellationToken.None);

        result.Status.ShouldBe(NodeStatus.Failure);
        result.Error.ShouldContain("couldn't find");
        result.Error.ShouldContain("#7");
    }

    [Fact]
    public async Task Provider_422_fails_with_a_self_approval_message()
    {
        var stub = new StubPrService { ThrowOnReview = new ProviderApiException(ProviderKind.GitHub, 422, "SubmitReviewAsync", "Unprocessable Entity", new Exception()) };

        var result = await new GitPrReviewNode(stub).RunAsync(BuildContext(Repo, 99, "approve", null), CancellationToken.None);

        result.Status.ShouldBe(NodeStatus.Failure);
        result.Error.ShouldContain("own pull request");
        result.Error.ShouldContain("#99");
    }

    [Fact]
    public async Task Fails_when_repository_id_is_missing()
    {
        var result = await new GitPrReviewNode(new StubPrService()).RunAsync(BuildContext(repositoryId: null, number: 1, verdict: "approve", body: null), CancellationToken.None);

        result.Status.ShouldBe(NodeStatus.Failure);
        result.Error.ShouldContain("repositoryId");
    }

    [Fact]
    public async Task Fails_when_number_is_missing()
    {
        var inputs = new Dictionary<string, JsonElement>
        {
            ["repositoryId"] = JsonSerializer.SerializeToElement(Repo),
            ["verdict"] = JsonSerializer.SerializeToElement("approve"),
        };

        var result = await new GitPrReviewNode(new StubPrService()).RunAsync(ContextFromInputs(inputs), CancellationToken.None);

        result.Status.ShouldBe(NodeStatus.Failure);
        result.Error.ShouldContain("number");
    }

    [Theory]
    [InlineData("0a1b2c3d", "0a1b2c3d")]
    [InlineData("  0a1b2c3d  ", "0a1b2c3d")]
    [InlineData("", null)]   // empty: review whatever the head is, as before
    public async Task Passes_the_expected_head_through_so_the_review_is_submitted_only_against_that_commit(string given, string? expected)
    {
        var stub = new StubPrService();

        await new GitPrReviewNode(stub).RunAsync(PinnedContext(given), CancellationToken.None);

        stub.ExpectedHeadSha.ShouldBe(expected);
    }

    [Fact]
    public void The_expected_head_is_a_declared_input_the_manifest_pins()
    {
        var manifest = new GitPrReviewNode(new StubPrService()).Manifest;

        manifest.InputSchema.GetProperty("properties").TryGetProperty("expectedHeadSha", out _).ShouldBeTrue("a workflow can bind the reviewed head too");
        manifest.RepositoryInput.ShouldNotBeNull().HeadShaInputKey.ShouldBe("expectedHeadSha", "an agent's approved review is pinned to the head its card showed");
    }

    [Theory]
    [InlineData(ProviderKind.GitLab, 409, "Couldn't submit the review to PR #42: GitLab reports its head is no longer 0a1b2c3d, the commit this review was pinned to, so nothing was submitted. Read the new commits before asking again.")]
    [InlineData(ProviderKind.GitHub, 422, "Couldn't submit the review to PR #42: GitHub rejected it — its head may no longer include 0a1b2c3d, the commit this review was pinned to, or this is your own pull request. Nothing was submitted.")]
    public async Task A_pinned_review_the_provider_refused_on_its_head_says_so_and_that_nothing_was_submitted(ProviderKind provider, int status, string error)
    {
        var stub = new StubPrService { ThrowOnReview = new ProviderApiException(provider, status, "SubmitReviewAsync", "refused", new Exception()) };

        var result = await new GitPrReviewNode(stub).RunAsync(PinnedContext("0a1b2c3d"), CancellationToken.None);

        result.Status.ShouldBe(NodeStatus.Failure);
        result.Error.ShouldBe(error);
    }

    [Fact]
    public async Task A_pull_request_whose_head_moved_before_the_review_was_sent_says_so_and_that_nothing_was_submitted()
    {
        var stub = new StubPrService { ThrowOnReview = new PullRequestMovedException(42, "head", "0a1b2c3d", "ffff0000") };

        var result = await new GitPrReviewNode(stub).RunAsync(PinnedContext("0a1b2c3d"), CancellationToken.None);

        result.Status.ShouldBe(NodeStatus.Failure);
        result.Error.ShouldBe("Couldn't submit the review to PR #42: its head is now ffff0000, not 0a1b2c3d, the one it was pinned to, so nothing was submitted. Read what changed before asking again.");
    }

    private static NodeRunContext PinnedContext(string expectedHeadSha) => ContextFromInputs(new()
    {
        ["repositoryId"] = JsonSerializer.SerializeToElement(Repo),
        ["number"] = JsonSerializer.SerializeToElement(42),
        ["verdict"] = JsonSerializer.SerializeToElement("approve"),
        ["expectedHeadSha"] = JsonSerializer.SerializeToElement(expectedHeadSha),
    });

    private static NodeRunContext BuildContext(string? repositoryId, int number, string verdict, string? body)
    {
        var inputs = new Dictionary<string, JsonElement>
        {
            ["number"] = JsonSerializer.SerializeToElement(number),
            ["verdict"] = JsonSerializer.SerializeToElement(verdict),
        };
        if (repositoryId != null) inputs["repositoryId"] = JsonSerializer.SerializeToElement(repositoryId);
        if (body != null) inputs["body"] = JsonSerializer.SerializeToElement(body);

        return ContextFromInputs(inputs);
    }

    // Default context carries the run's team in sys scope (as the engine always does) so the node resolves it.
    private static NodeRunContext ContextFromInputs(Dictionary<string, JsonElement> inputs) =>
        ContextWithSys(inputs, new() { [SystemScopeKeys.TeamId] = JsonSerializer.SerializeToElement(Team) });

    private static NodeRunContext ContextWithSys(Dictionary<string, JsonElement> inputs, Dictionary<string, JsonElement> sys) => new()
    {
        Inputs = inputs,
        Config = new Dictionary<string, JsonElement>(),
        RawInputs = JsonDocument.Parse("{}").RootElement,
        RawConfig = JsonDocument.Parse("{}").RootElement,
        Scope = new NodeRunScope { Trigger = new Dictionary<string, JsonElement>(), Sys = sys },
        Logger = NullLogger.Instance,
        Observability = NodeObservability.NoOp,
    };
}
