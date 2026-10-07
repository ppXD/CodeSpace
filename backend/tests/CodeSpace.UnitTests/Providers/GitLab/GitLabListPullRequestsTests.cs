using System.Diagnostics;
using System.Text.Json;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Providers;
using CodeSpace.Core.Services.Providers.Auth;
using CodeSpace.Core.Services.Providers.Errors;
using CodeSpace.Core.Services.Providers.Events;
using CodeSpace.Core.Services.Providers.GitLab;
using CodeSpace.Core.Services.Providers.Resilience;
using CodeSpace.IntegrationTests.Webhooks;
using CodeSpace.Messages.Dtos.Providers;
using CodeSpace.Messages.Enums;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using static CodeSpace.IntegrationTests.Webhooks.StubProviderHost;

namespace CodeSpace.UnitTests.Providers.GitLab;

/// <summary>
/// The real <see cref="GitLabRepositoryProvider"/> listing merge requests — NGitLab, the wire, the resilience wrapper —
/// against a loopback GitLab holding 250 merge requests in each state. A page is one request for that page: the list never
/// walks the pages before a far one, so what a caller names cannot multiply what the connection spends. Every request the
/// list sends is its own call through the connection's limiter, and cancelling the caller stops the read.
/// </summary>
[Trait("Category", "Unit")]
public sealed class GitLabListPullRequestsTests : IDisposable
{
    private const string MergeRequestsPath = "/api/v4/projects/4242/merge_requests?";
    private const string LabelsPath = "/api/v4/projects/4242/labels";
    private const int RowsPerState = 250;

    private static readonly DateTimeOffset Epoch = new(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly StubProviderHost _gitlab = new();

    public GitLabListPullRequestsTests()
    {
        _gitlab.Answer("GET", LabelsPath, 200, """[{"id":1,"name":"bug","color":"#d9534f"}]""").Answer("GET", MergeRequestsPath, MergeRequestPage);
    }

    public void Dispose() => _gitlab.Dispose();

    [Theory]
    [InlineData(null, null)]
    [InlineData(PullRequestState.Open, "opened")]
    [InlineData(PullRequestState.Merged, "merged")]
    public async Task A_far_page_is_one_request_for_that_page(PullRequestState? state, string? sentState)
    {
        var listed = await Provider().ListPullRequestsAsync(Context(), Repository, state, 3, 100, CancellationToken.None);

        _gitlab.Sent("GET", MergeRequestsPath).ShouldBe(1, "page 3 is read by asking for page 3, not by walking pages 1 and 2 first");
        var query = ForgeQuery.Of(_gitlab.Requests.Single(r => r.PathAndQuery.Contains(MergeRequestsPath)));
        query["page"].ShouldBe("3");
        query["per_page"].ShouldBe("100");
        query["state"].ShouldBe(sentState);
        query["order_by"].ShouldBe("updated_at");
        query["sort"].ShouldBe("desc");
        listed.Count.ShouldBe(state == null ? 100 : RowsPerState - 200);
    }

    [Fact]
    public async Task A_closed_page_reads_at_most_two_pages_of_each_finished_state()
    {
        // Closed is GitLab's closed and merged together, merged newest first, so a page needs the newest rows of both. Asking
        // for them a row at a page used to cost a request per row: page 200 of one row each sent 400 requests.
        var listed = await Provider().ListPullRequestsAsync(Context(), Repository, PullRequestState.Closed, 200, 1, CancellationToken.None);

        _gitlab.Requests.Count(r => r.PathAndQuery.Contains(MergeRequestsPath) && r.PathAndQuery.Contains("state=closed")).ShouldBe(2);
        _gitlab.Requests.Count(r => r.PathAndQuery.Contains(MergeRequestsPath) && r.PathAndQuery.Contains("state=merged")).ShouldBe(2);
        listed.ShouldHaveSingleItem().Number.ShouldBe(MergedNumber(100), "the 200th newest of the two states together");
    }

    [Fact]
    public async Task A_closed_page_interleaves_both_finished_states_newest_first()
    {
        var listed = await Provider().ListPullRequestsAsync(Context(), Repository, PullRequestState.Closed, 2, 3, CancellationToken.None);

        listed.Select(pr => pr.Number).ToList().ShouldBe([MergedNumber(2), ClosedNumber(3), MergedNumber(3)]);
    }

    [Theory]
    [InlineData(PullRequestState.Open, 2)]
    [InlineData(PullRequestState.Closed, 5)]
    public async Task Every_request_the_list_sends_is_charged_to_the_limiter(PullRequestState state, int expectedRequests)
    {
        var resilience = new CountingResilience(Resilience());

        await Provider(resilience).ListPullRequestsAsync(Context(), Repository, state, 2, 100, CancellationToken.None);

        _gitlab.Requests.Count.ShouldBe(expectedRequests);
        resilience.Calls.ShouldBe(_gitlab.Requests.Count, "each request on the wire takes its own token from the connection's limiter");
    }

    [Fact]
    public async Task The_label_colours_still_reach_the_listed_requests()
    {
        var listed = await Provider().ListPullRequestsAsync(Context(), Repository, PullRequestState.Open, 1, 1, CancellationToken.None);

        listed.ShouldHaveSingleItem().Labels.ShouldHaveSingleItem().ShouldBe(new LabelRef { Name = "bug", Color = "d9534f" });
        ForgeQuery.Of(_gitlab.Requests.Single(r => r.PathAndQuery.Contains(LabelsPath)))["per_page"].ShouldBe("100");
    }

    [Fact]
    public async Task Cancelling_the_caller_stops_the_read()
    {
        using var slow = new StubProviderHost();
        slow.Answer("GET", MergeRequestsPath, request =>
        {
            Thread.Sleep(TimeSpan.FromSeconds(5));
            return MergeRequestPage(request);
        });

        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        var stopwatch = Stopwatch.StartNew();

        await Should.ThrowAsync<OperationCanceledException>(() => Provider().ListPullRequestsAsync(Context(slow.BaseUrl), Repository, PullRequestState.Open, 1, 30, cancel.Token));

        stopwatch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(3), "the read must end when its caller cancels, not when GitLab answers — check that the list hands its token to the request");
    }

    // ── The loopback GitLab ─────────────────────────────────────────────────

    /// <summary>One page of the state the request names, GitLab's way: <c>per_page</c> rows (20 unless asked), <c>page</c> 1 unless asked, newest activity first.</summary>
    private StubReply MergeRequestPage(RecordedRequest request)
    {
        var query = ForgeQuery.Of(request);
        var perPage = int.Parse(query["per_page"] ?? "20");
        var page = int.Parse(query["page"] ?? "1");
        var rows = Rows(query["state"]).OrderByDescending(row => row.UpdatedAt).Skip((page - 1) * perPage).Take(perPage).ToList();
        var next = Rows(query["state"]).Count() > page * perPage ? (page + 1).ToString() : "";
        var headers = new Dictionary<string, string> { ["X-Next-Page"] = next };

        if (next.Length > 0) headers["Link"] = $"<{_gitlab.BaseUrl}/api/v4/projects/4242/merge_requests?state={query["state"]}&per_page={perPage}&page={next}>; rel=\"next\"";

        return new StubReply(200, JsonSerializer.Serialize(rows.Select(MergeRequestJson))) { Headers = headers };
    }

    private static IEnumerable<(int Number, string State, DateTimeOffset UpdatedAt)> Rows(string? state)
    {
        var opened = Enumerable.Range(1, RowsPerState).Select(i => (3000 + i, "opened", Epoch.AddMinutes(10_000 + i)));
        var closed = Enumerable.Range(1, RowsPerState).Select(i => (ClosedNumber(i), "closed", Epoch.AddMinutes(10_000 - 2 * i)));
        var merged = Enumerable.Range(1, RowsPerState).Select(i => (MergedNumber(i), "merged", Epoch.AddMinutes(10_000 - 2 * i - 1)));

        return state switch
        {
            "opened" => opened,
            "closed" => closed,
            "merged" => merged,
            _ => opened.Concat(closed).Concat(merged),
        };
    }

    private static int ClosedNumber(int newest) => 1000 + newest;

    private static int MergedNumber(int newest) => 2000 + newest;

    private static object MergeRequestJson((int Number, string State, DateTimeOffset UpdatedAt) row) => new
    {
        id = row.Number, iid = row.Number, project_id = 4242, title = $"MR {row.Number}", state = row.State, source_branch = $"f{row.Number}", target_branch = "main",
        author = new { id = 1, username = "dev", name = "Dev" }, labels = new[] { "bug" }, created_at = Epoch, updated_at = row.UpdatedAt, web_url = $"https://gitlab.test/acme/api/-/merge_requests/{row.Number}",
    };

    // ── The provider ────────────────────────────────────────────────────────

    private static readonly RemoteRepository Repository = new()
    {
        ExternalId = "4242",
        NamespacePath = "acme",
        Name = "api",
        FullPath = "acme/api",
        DefaultBranch = "main",
        Visibility = RepositoryVisibility.Private,
        WebUrl = "https://gitlab.test/acme/api"
    };

    private ProviderContext Context(string? baseUrl = null) => new(new ProviderInstance { Id = Guid.NewGuid(), TeamId = Guid.NewGuid(), Provider = ProviderKind.GitLab, DisplayName = "loopback", BaseUrl = baseUrl ?? _gitlab.BaseUrl }, new Credential { Id = Guid.NewGuid(), AuthType = AuthType.Pat, DisplayName = "pat", EncryptedPayload = "unused" });

    private static ExternalCallResilience Resilience() => new(new ProviderErrorMapperRegistry(new IProviderErrorMapper[] { new GitLabErrorMapper() }), NullLogger<ExternalCallResilience>.Instance);

    private static GitLabRepositoryProvider Provider(IExternalCallResilience? resilience = null)
    {
        var normalizer = new GitLabEventNormalizer(new ProviderEventSubscriptionRegistry(Array.Empty<IProviderEventSubscription>()));

        return new GitLabRepositoryProvider(new StaticTokenAuth(), resilience ?? Resilience(), new GitLabSignatureVerifier(), normalizer, new GitLabWebhookRepositoryIdentifier());
    }

    private sealed class StaticTokenAuth : IProviderAuthResolver
    {
        public Task<ResolvedAuth> ResolveAsync(ProviderContext context, CancellationToken cancellationToken) => Task.FromResult(new ResolvedAuth { Token = "glpat-loopback" });
    }

    /// <summary>The real resilience wrapper, counting the calls made through it — each one takes a token from the limiter.</summary>
    private sealed class CountingResilience(IExternalCallResilience inner) : IExternalCallResilience
    {
        private int _calls;

        public int Calls => _calls;

        public Task<T> ExecuteAsync<T>(ProviderInstance instance, string operationName, Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            return inner.ExecuteAsync(instance, operationName, operation, cancellationToken);
        }

        public Task ExecuteAsync(ProviderInstance instance, string operationName, Func<CancellationToken, Task> operation, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            return inner.ExecuteAsync(instance, operationName, operation, cancellationToken);
        }
    }
}
