using System.Text.Json;
using CodeSpace.Core.Services.PullRequests;
using CodeSpace.Core.Services.Workflows.Nodes;
using CodeSpace.Core.Services.Workflows.Nodes.Builtin;
using CodeSpace.Core.Services.Workflows.Runtime;
using CodeSpace.Messages.Dtos.Providers;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Queries.Repositories;
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

    private const string Team = "22222222-2222-2222-2222-222222222222";

    /// <summary>Records the list arguments (including the team) + returns canned results; every other member throws (this node only lists).</summary>
    private sealed class StubPrService : IPullRequestService
    {
        public Guid RepoId;
        public Guid TeamId;
        public PullRequestState? State;
        public int Page;
        public int PerPage;
        public int Calls;
        public IReadOnlyList<RemotePullRequest> Result = Array.Empty<RemotePullRequest>();

        public Task<IReadOnlyList<RemotePullRequest>> ListAsync(Guid repositoryId, Guid teamId, PullRequestState? state, int page, int perPage, CancellationToken cancellationToken)
        {
            RepoId = repositoryId; TeamId = teamId; State = state; Page = page; PerPage = perPage; Calls++;
            return Task.FromResult(Result);
        }

        public Task<RemotePullRequest> GetAsync(Guid r, Guid t, int n, CancellationToken c) => throw new NotImplementedException();
        public Task<IReadOnlyList<RemotePullRequestCommit>> ListCommitsAsync(Guid r, Guid t, int n, CancellationToken c) => throw new NotImplementedException();
        public Task<IReadOnlyList<RemotePullRequestFile>> ListFilesAsync(Guid r, Guid t, int n, CancellationToken c) => throw new NotImplementedException();
        public Task<RemotePullRequestCounts> GetCountsAsync(Guid r, Guid t, CancellationToken c) => throw new NotImplementedException();
        public Task<IReadOnlyList<RemotePullRequestCheck>> ListChecksAsync(Guid r, Guid t, int n, CancellationToken c) => throw new NotImplementedException();
        public Task<RemotePullRequestComment> PostCommentAsync(Guid r, Guid t, int n, string b, CancellationToken c) => throw new NotImplementedException();
        public Task<RemotePullRequestReview> SubmitReviewAsync(Guid r, Guid t, int n, SubmitPullRequestReviewInput i, Guid? a, CancellationToken c) => throw new NotImplementedException();
        public Task<RemotePullRequest> OpenPullRequestAsync(Guid r, Guid t, OpenPullRequestInput i, Guid? a, CancellationToken c) => throw new NotImplementedException();
        public Task<RemotePullRequestMergeResult> MergePullRequestAsync(Guid r, Guid t, int n, MergePullRequestInput i, Guid? a, CancellationToken c) => throw new NotImplementedException();
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
        // Items serialize with WorkflowJson.Options (camelCase + string enums) — consistent with every other
        // node output (git.open_change_set's pullRequests[], plan items) so the {{ref}} picker drills clean paths.
        result.Outputs["pullRequests"][0].GetProperty("number").GetInt32().ShouldBe(1);
        result.Outputs["pullRequests"][0].GetProperty("state").GetString().ShouldBe("Open");
    }

    [Fact]
    public async Task Threads_the_run_team_from_sys_scope_into_the_service_call()
    {
        var stub = new StubPrService { Result = new[] { Pr(1) } };

        var result = await new GitListPullRequestsNode(stub).RunAsync(Context(Repo), CancellationToken.None);

        result.Status.ShouldBe(NodeStatus.Success);
        stub.TeamId.ShouldBe(Guid.Parse(Team), "the run's team flows from {{sys.team_id}} so the service fail-closes the repo load to it");
    }

    [Fact]
    public async Task Fails_closed_when_sys_scope_has_no_team()
    {
        var stub = new StubPrService();

        // No sys.team_id (e.g. a synthetic agent-tool context) → the repo can't be tenant-scoped, so refuse.
        var result = await new GitListPullRequestsNode(stub).RunAsync(ContextWithSys(
            new() { ["repositoryId"] = JsonSerializer.SerializeToElement(Repo) }, new()), CancellationToken.None);

        result.Status.ShouldBe(NodeStatus.Failure);
        result.Error.ShouldContain("team context");
        stub.Calls.ShouldBe(0, "without a team the node must short-circuit before touching the service");
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

    [Theory]
    [InlineData(ListPullRequestsQuery.MaxPage, 4, ListPullRequestsQuery.MaxPage, 4)]   // the last page the list allows is still a page
    [InlineData(2, int.MaxValue, 2, ListPullRequestsQuery.MaxPerPage)]
    [InlineData(ListPullRequestsQuery.MaxPage, 101, ListPullRequestsQuery.MaxPage, ListPullRequestsQuery.MaxPerPage)]
    public async Task Clamps_pagination_to_the_ceilings_the_list_query_declares(int page, int perPage, int expectedPage, int expectedPerPage)
    {
        // An agent names page and perPage itself. Unclamped, one call asked a provider for its whole pull request list in a
        // single answer, or walked every page before a far one.
        var stub = new StubPrService();

        await new GitListPullRequestsNode(stub).RunAsync(ContextFrom(new()
        {
            ["repositoryId"] = JsonSerializer.SerializeToElement(Repo),
            ["page"] = JsonSerializer.SerializeToElement(page),
            ["perPage"] = JsonSerializer.SerializeToElement(perPage),
        }), CancellationToken.None);

        stub.Page.ShouldBe(expectedPage);
        stub.PerPage.ShouldBe(expectedPerPage);
    }

    [Theory]
    [InlineData("1001")]
    [InlineData("1000000")]
    [InlineData("2147483647")]
    [InlineData("2147483648")]    // the first number an Int32 cannot hold: it used to be read as no page at all, and so as page 1
    [InlineData("3000000000")]
    [InlineData("1e12")]
    [InlineData("1e999")]         // past what a double holds as well
    [InlineData("1001.0")]        // a spelling is not a different number
    [InlineData("1.001e3")]
    [InlineData("1001.5")]
    public async Task A_page_past_the_last_one_the_list_allows_is_an_empty_page_and_asks_no_provider(string page)
    {
        // It used to read page 1000 again — the same rows an agent had just seen, under a number that said it was further on,
        // so a loop paging until an empty page never reached one. A number too large for an Int32 fell through to page 1.
        var stub = new StubPrService();

        var result = await new GitListPullRequestsNode(stub).RunAsync(ContextFrom(new()
        {
            ["repositoryId"] = JsonSerializer.SerializeToElement(Repo),
            ["page"] = Raw(page),
            ["perPage"] = JsonSerializer.SerializeToElement(100),
        }), CancellationToken.None);

        result.Status.ShouldBe(NodeStatus.Success);
        result.Outputs["pullRequests"].GetArrayLength().ShouldBe(0);
        result.Outputs["count"].GetInt32().ShouldBe(0);
        stub.Calls.ShouldBe(0, "there is nothing at that page to fetch, so nothing is fetched");
    }

    [Theory]
    [InlineData("2.0", "30.0", 2, 30)]                                    // written with a fraction, the whole number it says
    [InlineData("1e1", "5E1", 10, 50)]                                    // or with an exponent
    [InlineData("2.5", "50.9", 2, 50)]                                    // a number with a fraction is read at its whole part
    [InlineData("3", "3000000000", 3, ListPullRequestsQuery.MaxPerPage)]  // a size too large for an Int32 is the ceiling, as 101 is — not the default
    [InlineData("1", "1e12", 1, ListPullRequestsQuery.MaxPerPage)]
    [InlineData("0.5", "0.5", 1, 30)]                                     // below one it is not a page or a size, as 0 is not
    [InlineData("-3000000000", "-1e12", 1, 30)]                           // and a number far below one is no more one than -5 is
    public async Task Reads_a_number_by_its_value_and_not_by_how_it_is_written(string page, string perPage, int expectedPage, int expectedPerPage)
    {
        var stub = new StubPrService();

        await new GitListPullRequestsNode(stub).RunAsync(ContextFrom(new()
        {
            ["repositoryId"] = JsonSerializer.SerializeToElement(Repo),
            ["page"] = Raw(page),
            ["perPage"] = Raw(perPage),
        }), CancellationToken.None);

        stub.Page.ShouldBe(expectedPage);
        stub.PerPage.ShouldBe(expectedPerPage);
    }

    [Fact]
    public void The_input_schema_advertises_the_ceilings_the_node_enforces()
    {
        var properties = new GitListPullRequestsNode(new StubPrService()).Manifest.InputSchema.GetProperty("properties");

        properties.GetProperty("page").GetProperty("maximum").GetInt32().ShouldBe(ListPullRequestsQuery.MaxPage);
        properties.GetProperty("perPage").GetProperty("maximum").GetInt32().ShouldBe(ListPullRequestsQuery.MaxPerPage);
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

    /// <summary>A JSON value exactly as a caller wrote it — <c>1001.0</c> and <c>1001</c> are different tokens to a reader.</summary>
    private static JsonElement Raw(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static NodeRunContext Context(string repositoryId, string? state = null)
    {
        var inputs = new Dictionary<string, JsonElement> { ["repositoryId"] = JsonSerializer.SerializeToElement(repositoryId) };
        if (state != null) inputs["state"] = JsonSerializer.SerializeToElement(state);
        return ContextFrom(inputs);
    }

    // Default context carries the run's team in sys scope (as the engine always does) so the node resolves it.
    private static NodeRunContext ContextFrom(Dictionary<string, JsonElement> inputs) => ContextWithSys(inputs, WithTeam());

    private static Dictionary<string, JsonElement> WithTeam() =>
        new() { [SystemScopeKeys.TeamId] = JsonSerializer.SerializeToElement(Team) };

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
