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
/// The real <see cref="GitLabRepositoryProvider"/> listing issues — NGitLab, the wire, the resilience wrapper — against a
/// loopback GitLab holding 250 issues in each state. A page is one request for that page: the list never walks the pages
/// before a far one, so the page the Issues tab names cannot multiply what the connection spends. The request is its own
/// call through the connection's limiter, and cancelling the caller stops the read.
/// </summary>
[Trait("Category", "Unit")]
public sealed class GitLabListIssuesTests : IDisposable
{
    private const string IssuesPath = "/api/v4/projects/4242/issues";
    private const int RowsPerState = 250;

    private static readonly DateTimeOffset Epoch = new(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly StubProviderHost _gitlab = new();

    public GitLabListIssuesTests()
    {
        _gitlab.Answer("GET", IssuesPath, IssuePage);
    }

    public void Dispose() => _gitlab.Dispose();

    [Theory]
    [InlineData(null, null)]
    [InlineData(IssueState.Open, "opened")]
    [InlineData(IssueState.Closed, "closed")]
    public async Task A_far_page_is_one_request_for_that_page(IssueState? state, string? sentState)
    {
        var listed = await Provider().ListIssuesAsync(Context(), Repository, state, 3, 100, CancellationToken.None);

        var request = _gitlab.Requests.ShouldHaveSingleItem("page 3 is read by asking for page 3, not by walking pages 1 and 2 first");
        var query = ForgeQuery.Of(request);
        query["page"].ShouldBe("3");
        query["per_page"].ShouldBe("100");
        query["state"].ShouldBe(sentState);
        query["order_by"].ShouldBe("created_at");
        query["sort"].ShouldBe("desc");
        listed.Count.ShouldBe(state == null ? 100 : RowsPerState - 200);
        listed[0].Number.ShouldBe(Newest(state).Skip(200).First(), "newest first, as the Issues tab shows them");
    }

    [Fact]
    public async Task The_list_is_one_call_through_the_connections_limiter()
    {
        var resilience = new CountingResilience(Resilience());

        await Provider(resilience).ListIssuesAsync(Context(), Repository, IssueState.Open, 2, 100, CancellationToken.None);

        resilience.Calls.ShouldBe(_gitlab.Requests.Count, "each request on the wire takes its own token from the connection's limiter");
    }

    [Fact]
    public async Task Cancelling_the_caller_stops_the_read()
    {
        using var slow = new StubProviderHost();
        slow.Answer("GET", IssuesPath, request =>
        {
            Thread.Sleep(TimeSpan.FromSeconds(5));
            return IssuePage(request);
        });

        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        var stopwatch = Stopwatch.StartNew();

        await Should.ThrowAsync<OperationCanceledException>(() => Provider().ListIssuesAsync(Context(slow.BaseUrl), Repository, IssueState.Open, 1, 30, cancel.Token));

        stopwatch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(3), "the read must end when its caller cancels, not when GitLab answers — check that the list hands its token to the request");
    }

    // ── The loopback GitLab ─────────────────────────────────────────────────

    /// <summary>One page of the state the request names, GitLab's way: <c>per_page</c> rows (20 unless asked), <c>page</c> 1 unless asked, newest first, and a <c>Link</c> to the next page while there is one.</summary>
    private StubReply IssuePage(RecordedRequest request)
    {
        var query = ForgeQuery.Of(request);
        var perPage = int.Parse(query["per_page"] ?? "20");
        var page = int.Parse(query["page"] ?? "1");
        var state = query["state"] switch { "opened" => IssueState.Open, "closed" => IssueState.Closed, _ => (IssueState?)null };
        var all = Newest(state).ToList();
        var rows = all.Skip((page - 1) * perPage).Take(perPage).ToList();
        var next = all.Count > page * perPage ? (page + 1).ToString() : "";
        var headers = new Dictionary<string, string> { ["X-Next-Page"] = next };

        if (next.Length > 0) headers["Link"] = $"<{_gitlab.BaseUrl}{IssuesPath}?per_page={perPage}&page={next}>; rel=\"next\"";

        return new StubReply(200, JsonSerializer.Serialize(rows.Select(number => IssueJson(number, number > 2000 ? "closed" : "opened")))) { Headers = headers };
    }

    /// <summary>Issue numbers in <paramref name="state"/>, newest first: opened issues are 1001..1250, closed ones 2001..2250, and a higher number is newer.</summary>
    private static IEnumerable<int> Newest(IssueState? state)
    {
        var opened = Enumerable.Range(1001, RowsPerState);
        var closed = Enumerable.Range(2001, RowsPerState);

        return (state switch { IssueState.Open => opened, IssueState.Closed => closed, _ => opened.Concat(closed) }).OrderByDescending(number => number);
    }

    private static object IssueJson(int number, string state) => new
    {
        id = number, iid = number, project_id = 4242, title = $"Issue {number}", state, author = new { id = 1, username = "dev", name = "Dev" }, labels = Array.Empty<string>(),
        created_at = Epoch.AddMinutes(number), updated_at = Epoch.AddMinutes(number), web_url = $"https://gitlab.test/acme/api/-/issues/{number}",
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
