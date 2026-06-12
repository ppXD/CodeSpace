using System.Text.Json;
using CodeSpace.Core.Services.PullRequests;
using CodeSpace.Core.Services.Workflows.Nodes;
using CodeSpace.Core.Services.Workflows.Nodes.Builtin;
using CodeSpace.Core.Services.Workflows.Runtime;
using CodeSpace.Messages.Dtos.Providers;
using CodeSpace.Messages.Enums;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

/// <summary>
/// <c>git.list_prs</c> — the read-only discovery node. Drives the real node against a hand-rolled
/// <see cref="IPullRequestService"/> stub that records the exact (state, page, perPage) it was called with
/// and returns canned PRs, so the input parsing (optional state filter + pagination defaults) and the
/// output shape (pullRequests[] + count) are pinned across scenarios.
/// </summary>
[Trait("Category", "Unit")]
public class GitListPullRequestsNodeTests
{
    private const string Repo = "11111111-1111-1111-1111-111111111111";

    /// <summary>Records the list arguments + returns canned results; every other member throws (this node only lists).</summary>
    private sealed class StubPrService : IPullRequestService
    {
        public Guid RepoId;
        public PullRequestState? State;
        public int Page;
        public int PerPage;
        public int Calls;
        public IReadOnlyList<RemotePullRequest> Result = Array.Empty<RemotePullRequest>();

        public Task<IReadOnlyList<RemotePullRequest>> ListAsync(Guid repositoryId, PullRequestState? state, int page, int perPage, CancellationToken cancellationToken)
        {
            RepoId = repositoryId; State = state; Page = page; PerPage = perPage; Calls++;
            return Task.FromResult(Result);
        }

        public Task<RemotePullRequest> GetAsync(Guid r, int n, CancellationToken c) => throw new NotImplementedException();
        public Task<IReadOnlyList<RemotePullRequestCommit>> ListCommitsAsync(Guid r, int n, CancellationToken c) => throw new NotImplementedException();
        public Task<IReadOnlyList<RemotePullRequestFile>> ListFilesAsync(Guid r, int n, CancellationToken c) => throw new NotImplementedException();
        public Task<RemotePullRequestCounts> GetCountsAsync(Guid r, CancellationToken c) => throw new NotImplementedException();
        public Task<IReadOnlyList<RemotePullRequestCheck>> ListChecksAsync(Guid r, int n, CancellationToken c) => throw new NotImplementedException();
        public Task<RemotePullRequestComment> PostCommentAsync(Guid r, int n, string b, CancellationToken c) => throw new NotImplementedException();
        public Task<RemotePullRequestReview> SubmitReviewAsync(Guid r, int n, PullRequestReviewVerdict v, string? b, Guid? a, CancellationToken c) => throw new NotImplementedException();
        public Task<RemotePullRequest> OpenPullRequestAsync(Guid r, OpenPullRequestInput i, Guid? a, CancellationToken c) => throw new NotImplementedException();
        public Task<RemotePullRequestMergeResult> MergePullRequestAsync(Guid r, int n, MergePullRequestInput i, Guid? a, CancellationToken c) => throw new NotImplementedException();
    }

    private static RemotePullRequest Pr(int number) => new()
    {
        ExternalId = $"pr-{number}", Number = number, Title = $"PR {number}", State = PullRequestState.Open,
        SourceBranch = "feature", TargetBranch = "main", CommentsCount = 0,
        CreatedDate = DateTimeOffset.UnixEpoch, UpdatedDate = DateTimeOffset.UnixEpoch, WebUrl = $"https://example.test/pr/{number}",
    };

    [Fact]
    public async Task Lists_pull_requests_and_outputs_the_array_and_count()
    {
        var stub = new StubPrService { Result = new[] { Pr(1), Pr(2), Pr(3) } };

        var result = await new GitListPullRequestsNode(stub).RunAsync(Context(Repo), CancellationToken.None);

        result.Status.ShouldBe(NodeStatus.Success);
        stub.Calls.ShouldBe(1);
        stub.RepoId.ShouldBe(Guid.Parse(Repo));
        result.Outputs["count"].GetInt32().ShouldBe(3);
        result.Outputs["pullRequests"].GetArrayLength().ShouldBe(3);
        // DTOs serialize with default (PascalCase) property names — the same convention git.fetch_pr_diff uses
        // for its files[] output (consumed by the AI-review template), so downstream binding is consistent.
        result.Outputs["pullRequests"][0].GetProperty("Number").GetInt32().ShouldBe(1);
    }

    [Fact]
    public async Task Defaults_to_all_states_and_page_one_thirty_when_omitted()
    {
        var stub = new StubPrService();

        await new GitListPullRequestsNode(stub).RunAsync(Context(Repo), CancellationToken.None);

        stub.State.ShouldBeNull("an omitted state lists all states");
        stub.Page.ShouldBe(1);
        stub.PerPage.ShouldBe(30);
    }

    [Theory]
    [InlineData("Open", PullRequestState.Open)]
    [InlineData("open", PullRequestState.Open)]   // case-insensitive
    [InlineData("Merged", PullRequestState.Merged)]
    [InlineData("closed", PullRequestState.Closed)]
    [InlineData("Draft", PullRequestState.Draft)]
    public async Task Parses_the_state_filter_tolerantly_of_casing(string raw, PullRequestState expected)
    {
        var stub = new StubPrService();

        var result = await new GitListPullRequestsNode(stub).RunAsync(Context(Repo, state: raw), CancellationToken.None);

        result.Status.ShouldBe(NodeStatus.Success);
        stub.State.ShouldBe(expected);
    }

    [Fact]
    public async Task Empty_state_lists_all_states()
    {
        var stub = new StubPrService();

        var result = await new GitListPullRequestsNode(stub).RunAsync(Context(Repo, state: ""), CancellationToken.None);

        result.Status.ShouldBe(NodeStatus.Success);
        stub.Calls.ShouldBe(1);
        stub.State.ShouldBeNull();
    }

    [Fact]
    public async Task Fails_clearly_for_an_unknown_state_rather_than_silently_listing_all()
    {
        var stub = new StubPrService();

        var result = await new GitListPullRequestsNode(stub).RunAsync(Context(Repo, state: "reopened"), CancellationToken.None);

        result.Status.ShouldBe(NodeStatus.Failure);
        result.Error.ShouldContain("state");
        stub.Calls.ShouldBe(0, "a bad state must not silently fall through to a list-all call");
    }

    [Fact]
    public async Task Passes_explicit_pagination_through()
    {
        var stub = new StubPrService();

        await new GitListPullRequestsNode(stub).RunAsync(ContextFrom(new()
        {
            ["repositoryId"] = JsonSerializer.SerializeToElement(Repo),
            ["page"] = JsonSerializer.SerializeToElement(4),
            ["perPage"] = JsonSerializer.SerializeToElement(50),
        }), CancellationToken.None);

        stub.Page.ShouldBe(4);
        stub.PerPage.ShouldBe(50);
    }

    [Fact]
    public async Task Falls_back_to_defaults_for_non_positive_pagination()
    {
        var stub = new StubPrService();

        await new GitListPullRequestsNode(stub).RunAsync(ContextFrom(new()
        {
            ["repositoryId"] = JsonSerializer.SerializeToElement(Repo),
            ["page"] = JsonSerializer.SerializeToElement(0),
            ["perPage"] = JsonSerializer.SerializeToElement(-5),
        }), CancellationToken.None);

        stub.Page.ShouldBe(1);
        stub.PerPage.ShouldBe(30);
    }

    [Fact]
    public async Task Fails_when_repository_id_is_missing()
    {
        var result = await new GitListPullRequestsNode(new StubPrService()).RunAsync(ContextFrom(new()), CancellationToken.None);

        result.Status.ShouldBe(NodeStatus.Failure);
        result.Error.ShouldContain("repositoryId");
    }

    [Fact]
    public async Task Fails_when_repository_id_is_not_a_uuid()
    {
        var result = await new GitListPullRequestsNode(new StubPrService()).RunAsync(ContextFrom(new()
        {
            ["repositoryId"] = JsonSerializer.SerializeToElement("not-a-uuid"),
        }), CancellationToken.None);

        result.Status.ShouldBe(NodeStatus.Failure);
        result.Error.ShouldContain("repositoryId");
    }

    private static NodeRunContext Context(string repositoryId, string? state = null)
    {
        var inputs = new Dictionary<string, JsonElement> { ["repositoryId"] = JsonSerializer.SerializeToElement(repositoryId) };
        if (state != null) inputs["state"] = JsonSerializer.SerializeToElement(state);
        return ContextFrom(inputs);
    }

    private static NodeRunContext ContextFrom(Dictionary<string, JsonElement> inputs) => new()
    {
        Inputs = inputs,
        Config = new Dictionary<string, JsonElement>(),
        RawInputs = JsonDocument.Parse("{}").RootElement,
        RawConfig = JsonDocument.Parse("{}").RootElement,
        Scope = new NodeRunScope { Trigger = new Dictionary<string, JsonElement>() },
        Logger = NullLogger.Instance,
        Observability = NodeObservability.NoOp,
    };
}
