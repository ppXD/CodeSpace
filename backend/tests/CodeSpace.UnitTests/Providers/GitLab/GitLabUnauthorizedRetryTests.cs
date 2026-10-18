using System.Net;
using CodeSpace.Core.Services.Providers;
using CodeSpace.Core.Services.Providers.Auth;
using CodeSpace.Core.Services.Providers.GitLab;
using CodeSpace.IntegrationTests.Webhooks;
using CodeSpace.Messages.Dtos.Providers;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Exceptions;
using NGitLab.Models;
using Shouldly;
using static CodeSpace.IntegrationTests.Webhooks.StubProviderHost;
using static CodeSpace.UnitTests.Providers.GitLab.GitLabLoopback;

namespace CodeSpace.UnitTests.Providers.GitLab;

/// <summary>
/// A 401 to an OAuth credential is sent once more with the token the resolver says replaces it. GitLab retires an access token
/// the moment its pair is refreshed — by another run, another pod — however much of its lifetime is left, so a token read
/// from the row an instant ago can be refused; before, that surfaced as a 422 "provider_unauthorized" for up to two hours.
/// Every GitLab call path is covered: NGitLab's typed client, its raw paged reader, and each raw HTTP call (the matrix is the
/// loopback suite's own list of calls, so a new one joins it). A static token has nothing to replace it with and is not
/// retried. The loopback GitLab refuses the stale token and answers the fresh one.
/// </summary>
[Trait("Category", "Unit")]
public sealed class GitLabUnauthorizedRetryTests : IDisposable
{
    private const string Stale = "oauth-stale-token-0123456789";
    private const string Fresh = "oauth-fresh-token-0123456789";

    private readonly List<IDisposable> _gitlabs = new();

    public void Dispose()
    {
        foreach (var gitlab in _gitlabs) gitlab.Dispose();
    }

    public static TheoryData<string> EveryCall()
    {
        var data = new TheoryData<string>();

        foreach (var operation in Operations.Keys) data.Add(operation);

        return data;
    }

    [Theory]
    [MemberData(nameof(EveryCall))]
    public async Task A_call_refused_with_a_stale_token_is_sent_again_with_the_replacement_and_the_replacement_stays_for_the_rest_of_the_call(string operation)
    {
        var gitlab = GitLabThatRefuses(Stale);
        var auth = new RecoveringAuth(Stale, replacement: Fresh);

        await Operations[operation](Provider(auth), Context(gitlab.BaseUrl, AuthType.OAuth));

        auth.Rejected.ShouldHaveSingleItem($"'{operation}' was refused once and asked once: the replacement is what every later request of the call carries");
        auth.Rejected[0].ShouldBe(Stale);

        var refused = gitlab.Requests.Where(r => r.AuthorizationHeader == $"Bearer {Stale}").ToList();
        var served = gitlab.Requests.Where(r => r.AuthorizationHeader == $"Bearer {Fresh}").ToList();

        refused.ShouldHaveSingleItem("only the first request of the call is ever refused");
        served.ShouldContain(r => r.Method == refused[0].Method && r.PathAndQuery == refused[0].PathAndQuery && r.Body == refused[0].Body, $"{refused[0].Method} {refused[0].PathAndQuery} was sent again unchanged");
        gitlab.Requests.ShouldAllBe(r => r.PrivateTokenHeader == null, "an OAuth token is never a PRIVATE-TOKEN, replayed or not");
    }

    [Theory]
    [MemberData(nameof(EveryCall))]
    public async Task A_replacement_that_is_refused_too_is_the_answer_and_the_stale_token_is_never_sent_twice(string operation)
    {
        var gitlab = GitLabThatRefuses(Stale, Fresh);
        var auth = new RecoveringAuth(Stale, replacement: Fresh);

        var failure = await Record.ExceptionAsync(() => Operations[operation](Provider(auth), Context(gitlab.BaseUrl, AuthType.OAuth)));

        // Some calls degrade a refusal to an empty result by design (a languages tab, an actor's access); the rest throw it.
        if (failure is not null) IsUnauthorized(failure).ShouldBeTrue($"the second 401 is what the caller is shown — {failure.GetType().Name}: {failure.Message}");

        gitlab.Requests.Count(r => r.AuthorizationHeader == $"Bearer {Stale}").ShouldBe(1, "the refused token is sent once, however the call is retried above the transport");
        auth.Rejected.Count(token => token == Stale).ShouldBe(1, "the resolver is asked about the refused token once; the replacement it hands out is not asked about again");
    }

    [Theory]
    [InlineData(AuthType.Pat)]
    [InlineData(AuthType.ProjectAccessToken)]
    [InlineData(AuthType.GroupAccessToken)]
    public async Task A_static_token_has_nothing_to_replace_it_with_so_its_401_is_not_retried(AuthType type)
    {
        var gitlab = Track(new StubProviderHost().Answer("GET", "/api/v4/", _ => new StubReply(401, """{"message":"401 Unauthorized"}""")));
        var auth = new RecoveringAuth(Stale, replacement: Fresh);

        await Should.ThrowAsync<ProviderApiException>(() => Provider(auth).GetByExternalIdAsync(Context(gitlab.BaseUrl, type), "42", CancellationToken.None));

        gitlab.Requests.Count.ShouldBe(1, "the same token would be refused the same way");
        auth.Rejected.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_resolver_with_nothing_to_offer_leaves_the_401_as_it_was()
    {
        var gitlab = GitLabThatRefuses(Stale);
        var auth = new RecoveringAuth(Stale, replacement: null);

        var failure = await Should.ThrowAsync<ProviderApiException>(() => Provider(auth).GetByExternalIdAsync(Context(gitlab.BaseUrl, AuthType.OAuth), "42", CancellationToken.None));

        failure.StatusCode.ShouldBe(401);
        gitlab.Requests.Count.ShouldBe(1);
        auth.Rejected.ShouldHaveSingleItem();
    }

    [Theory]
    [MemberData(nameof(EveryCall))]
    public async Task A_credential_the_provider_refuses_for_good_is_what_the_caller_is_told_instead_of_the_401(string operation)
    {
        var gitlab = GitLabThatRefuses(Stale);
        var dead = new CredentialNeedsReconnectException(Guid.NewGuid(), "GitLab · nacho", "invalid_grant");
        var auth = new RecoveringAuth(Stale, replacement: null) { OnInvalidate = () => throw dead };

        var failure = await Record.ExceptionAsync(() => Operations[operation](Provider(auth), Context(gitlab.BaseUrl, AuthType.OAuth)));

        // Some calls degrade any failure to an empty result by design; the rest say why the credential cannot be used.
        if (failure is not null) Chain(failure).ShouldContain(dead, $"{failure.GetType().Name}: {failure.Message}");

        auth.Rejected.ShouldNotBeEmpty("the refusal was taken to the resolver");
    }

    [Fact]
    public async Task A_request_that_changes_something_through_the_typed_client_cannot_be_sent_again_but_the_credential_is_still_put_right()
    {
        // NGitLab has written the body to a stream it does not hand back, so a write is not replayed. Asking still settles the
        // credential — the stored token is the replacement afterwards — so the next call carries it, and this one's 401 stands.
        var gitlab = GitLabThatRefuses(Stale);
        var recoveries = 0;
        var credential = new GitLabCredential(Stale, AuthType.OAuth, (_, _) =>
        {
            recoveries++;
            return Task.FromResult<string?>(Fresh);
        });
        var client = GitLabHttp.NewClient(gitlab.BaseUrl, credential);

        Should.Throw<NGitLab.GitLabException>(() => client.Issues.Create(new IssueCreate { ProjectId = 42, Title = "t", Description = "d" }));

        gitlab.Requests.Count.ShouldBe(1, "the write was sent once");
        recoveries.ShouldBe(1);
        credential.Token.ShouldBe(Fresh, "the credential carries the replacement from then on, so the call's next request is not refused");
    }

    public static TheoryData<string> EveryTypedWrite()
    {
        var data = new TheoryData<string>();

        foreach (var operation in TypedWrites.Keys) data.Add(operation);

        return data;
    }

    [Theory]
    [MemberData(nameof(EveryTypedWrite))]
    public async Task A_write_through_the_typed_client_that_gitlab_refused_with_a_stale_token_is_sent_again_once_with_the_replacement(string operation)
    {
        // NGitLab cannot send a write again (it wrote the body to a stream it does not hand back), but a 401 is GitLab's answer
        // before it acts, and the refusal has already put the replacement on the credential — so the provider repeats the call:
        // the first merge, comment or issue after another consumer rotated the pair is not lost to the very 401 this recovers from.
        var gitlab = GitLabThatRefusesWrites(Stale);
        var auth = new RecoveringAuth(Stale, replacement: Fresh);

        await TypedWrites[operation](Provider(auth), Context(gitlab.BaseUrl, AuthType.OAuth));

        auth.Rejected.ShouldHaveSingleItem($"'{operation}' was refused once and asked once");

        var refused = gitlab.Requests.Where(r => r.AuthorizationHeader == $"Bearer {Stale}").ToList();
        var served = gitlab.Requests.Where(r => r.AuthorizationHeader == $"Bearer {Fresh}").ToList();

        refused.ShouldHaveSingleItem("only the first request of the call is ever refused");
        refused[0].Method.ShouldNotBe("GET", "the refused request is the write itself: nothing before it settled the token");
        served.ShouldContain(r => r.Method == refused[0].Method && r.PathAndQuery == refused[0].PathAndQuery && r.Body == refused[0].Body, $"{refused[0].Method} {refused[0].PathAndQuery} was sent again, with the same body");
        gitlab.Requests.ShouldAllBe(r => r.PrivateTokenHeader == null, "an OAuth token is never a PRIVATE-TOKEN, replayed or not");
    }

    [Theory]
    [MemberData(nameof(EveryTypedWrite))]
    public async Task A_typed_write_whose_replacement_is_refused_too_is_the_answer_and_is_not_sent_a_third_time(string operation)
    {
        var gitlab = GitLabThatRefusesWrites(Stale, Fresh);
        var auth = new RecoveringAuth(Stale, replacement: Fresh);

        var failure = await Should.ThrowAsync<ProviderApiException>(() => TypedWrites[operation](Provider(auth), Context(gitlab.BaseUrl, AuthType.OAuth)));

        failure.StatusCode.ShouldBe(401);

        // Counted over the writes: a failed open also reads the project's merge requests, to see whether it was a repeat of one already open.
        var writes = gitlab.Requests.Where(r => r.Method != "GET").ToList();

        writes.Count(r => r.AuthorizationHeader == $"Bearer {Stale}").ShouldBe(1, "the refused token is sent once");
        writes.Count(r => r.AuthorizationHeader == $"Bearer {Fresh}").ShouldBe(1, "and the replacement once: a second refusal is the answer");

        // That read builds a client of its own from this resolver, which still hands out the stale token (the real one hands out
        // what it stored), so the refusal of it is asked about once more.
        auth.Rejected.Count(token => token == Stale).ShouldBe(operation == "open a merge request" ? 2 : 1);
    }

    [Theory]
    [MemberData(nameof(EveryTypedWrite))]
    public async Task A_typed_write_whose_401_has_no_replacement_is_not_sent_again(string operation)
    {
        var gitlab = GitLabThatRefusesWrites(Stale);
        var auth = new RecoveringAuth(Stale, replacement: null);

        var failure = await Should.ThrowAsync<ProviderApiException>(() => TypedWrites[operation](Provider(auth), Context(gitlab.BaseUrl, AuthType.OAuth)));

        failure.StatusCode.ShouldBe(401);
        gitlab.Requests.Count(r => r.Method != "GET").ShouldBe(1, "the same token would be refused the same way");
    }

    [Theory]
    [InlineData(AuthType.Pat)]
    [InlineData(AuthType.ProjectAccessToken)]
    [InlineData(AuthType.GroupAccessToken)]
    public async Task A_static_tokens_typed_write_is_not_sent_again_after_a_401(AuthType type)
    {
        var gitlab = Track(new StubProviderHost().Answer("POST", "/api/v4/", _ => new StubReply(401, """{"message":"401 Unauthorized"}""")));
        var auth = new RecoveringAuth(Stale, replacement: Fresh);

        await Should.ThrowAsync<ProviderApiException>(() => Provider(auth).CommentIssueAsync(Context(gitlab.BaseUrl, type), Repository, 7, "hello", CancellationToken.None));

        gitlab.Requests.ShouldHaveSingleItem("the same token would be refused the same way");
        auth.Rejected.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_replayed_raw_write_carries_its_body_again()
    {
        var gitlab = GitLabThatRefuses(Stale);
        var auth = new RecoveringAuth(Stale, replacement: Fresh);

        var rendered = await Provider(auth).RenderMarkdownAsync(Context(gitlab.BaseUrl, AuthType.OAuth), Repository, "# hello", CancellationToken.None);

        rendered.Html.ShouldBe("<p>x</p>");
        gitlab.Requests.Select(r => r.Method).ShouldBe(new[] { "POST", "POST" });
        gitlab.Requests[0].Body.ShouldContain("# hello");
        gitlab.Requests[1].Body.ShouldBe(gitlab.Requests[0].Body, "a 401 happens before GitLab acts on a request, so repeating it repeats nothing");
        gitlab.Requests[1].AuthorizationHeader.ShouldBe($"Bearer {Fresh}");
    }

    /// <summary>A refusal as the call sites surface it: the typed provider failure itself, or — for the webhook calls, which wrap what they catch in their own exception and say the status in its text — that wrapper.</summary>
    private static bool IsUnauthorized(Exception failure) => Chain(failure).Any(e => e is ProviderApiException { StatusCode: 401 } or NGitLab.GitLabException { StatusCode: HttpStatusCode.Unauthorized } || (e is ProviderWebhookRegistrationException && e.Message.Contains("401", StringComparison.Ordinal)));

    private static IEnumerable<Exception> Chain(Exception failure)
    {
        for (var e = failure; e is not null; e = e.InnerException) yield return e;
    }

    /// <summary>A GitLab that answers <see cref="GitLabLoopback.Answer"/> to any token but the refused ones, which it answers 401.</summary>
    private StubProviderHost GitLabThatRefuses(params string[] refusedTokens)
    {
        StubReply Respond(RecordedRequest request) =>
            refusedTokens.Any(token => request.AuthorizationHeader == $"Bearer {token}") ? new StubReply(401, """{"message":"401 Unauthorized"}""") : AnswerWithMergeRequestNotes(request);

        return Track(new StubProviderHost().Answer("GET", "/api/v4/", Respond).Answer("POST", "/api/v4/", Respond));
    }

    /// <summary>A GitLab that refuses the given tokens and otherwise answers the typed writes (and every route <see cref="GitLabLoopback.Answer"/> knows).</summary>
    private StubProviderHost GitLabThatRefusesWrites(params string[] refusedTokens)
    {
        StubReply Respond(RecordedRequest request) =>
            refusedTokens.Any(token => request.AuthorizationHeader == $"Bearer {token}") ? new StubReply(401, """{"message":"401 Unauthorized"}""") : AnswerWrites(request);

        return Track(new StubProviderHost().Answer("GET", "/api/v4/", Respond).Answer("POST", "/api/v4/", Respond).Answer("PUT", "/api/v4/", Respond).Answer("DELETE", "/api/v4/", Respond));
    }

    private const string MergeRequestJson = """{"id":71,"iid":7,"project_id":42,"title":"t","description":"d","state":"opened","source_branch":"feature","target_branch":"main","source_project_id":42,"target_project_id":42,"web_url":"http://gitlab.test/g/r/-/merge_requests/7","sha":"abc","labels":[],"author":{"id":7,"username":"nacho"},"created_at":"2030-01-01T00:00:00Z","updated_at":"2030-01-01T00:00:00Z"}""";

    private const string IssueJson = """{"id":51,"iid":7,"project_id":42,"title":"t","description":"d","state":"opened","labels":[],"web_url":"http://gitlab.test/g/r/-/issues/7","author":{"id":7,"username":"nacho"},"created_at":"2030-01-01T00:00:00Z","updated_at":"2030-01-01T00:00:00Z"}""";

    private const string NoteJson = """{"id":9,"body":"b","author":{"id":7,"username":"nacho"},"created_at":"2030-01-01T00:00:00Z"}""";

    /// <summary>What GitLab answers each typed write; anything else falls through to the loopback's own routes.</summary>
    private static StubReply AnswerWrites(RecordedRequest request)
    {
        var path = request.PathAndQuery.Split('?')[0];

        return (request.Method, path) switch
        {
            ("POST", ProjectPath + "/merge_requests") => new StubReply(201, MergeRequestJson),
            ("PUT", ProjectPath + "/merge_requests/7/merge") => new StubReply(200, MergeRequestJson.Replace("\"opened\"", "\"merged\"", StringComparison.Ordinal)),
            ("POST", ProjectPath + "/issues") => new StubReply(201, IssueJson),
            ("POST", ProjectPath + "/issues/7/notes") => new StubReply(201, NoteJson),
            ("PUT", ProjectPath + "/issues/7") => new StubReply(200, IssueJson.Replace("\"opened\"", "\"closed\"", StringComparison.Ordinal)),
            ("POST", ProjectPath + "/merge_requests/7/notes") => new StubReply(201, NoteJson),
            ("DELETE", ProjectPath + "/hooks/9") => new StubReply(204, string.Empty),
            ("GET", ProjectPath + "/merge_requests") => new StubReply(200, "[]"),
            ("GET", ProjectPath + "/merge_requests/7") => new StubReply(200, MergeRequestJson),
            ("GET", ProjectPath + "/issues") => new StubReply(200, "[]"),
            ("GET", ProjectPath + "/issues/7/notes") => new StubReply(200, "[]"),
            ("GET", ProjectPath + "/merge_requests/7/notes") => new StubReply(200, "[]"),
            _ => AnswerWithMergeRequestNotes(request)
        };
    }

    /// <summary>The typed writes: the provider's calls that change something through NGitLab, the first request of each being the write itself.</summary>
    private static readonly IReadOnlyDictionary<string, Func<GitLabRepositoryProvider, ProviderContext, Task>> TypedWrites = new Dictionary<string, Func<GitLabRepositoryProvider, ProviderContext, Task>>
    {
        ["open a merge request"] = (p, c) => p.OpenPullRequestAsync(c, Repository, new OpenPullRequestInput { Title = "t", SourceBranch = "feature", TargetBranch = "main", Body = "d" }, CancellationToken.None),
        ["merge a merge request"] = (p, c) => p.MergePullRequestAsync(c, Repository, 7, new MergePullRequestInput(), CancellationToken.None),
        ["create an issue"] = (p, c) => p.CreateIssueAsync(c, Repository, new CreateIssueInput { Title = "t", Body = "d" }, CancellationToken.None),
        ["comment on an issue"] = (p, c) => p.CommentIssueAsync(c, Repository, 7, "hello", CancellationToken.None),
        ["close an issue"] = (p, c) => p.CloseIssueAsync(c, Repository, 7, CancellationToken.None),
        ["comment on a merge request"] = (p, c) => p.PostCommentAsync(c, Repository, 7, "hello", CancellationToken.None),
        ["delete a project hook"] = (p, c) => p.DeleteWebhookAsync(c, Repository, "9", CancellationToken.None),
    };

    /// <summary>The loopback's answers, and the merge request notes a review leaves after the unapprove.</summary>
    private static StubReply AnswerWithMergeRequestNotes(RecordedRequest request) => request.PathAndQuery.Contains("/merge_requests/7/notes", StringComparison.Ordinal)
        ? request.Method == "GET" ? new StubReply(200, "[]") : new StubReply(201, """{"id":9,"body":"b","author":{"id":7,"username":"nacho"},"created_at":"2030-01-01T00:00:00Z"}""")
        : Answer(request);

    private StubProviderHost Track(StubProviderHost gitlab)
    {
        _gitlabs.Add(gitlab);

        return gitlab;
    }

    /// <summary>Hands out <see cref="Stale"/> and, when asked about it, <paramref name="replacement"/> — what the real resolver does when another caller already rotated the pair.</summary>
    private sealed class RecoveringAuth : IProviderAuthResolver
    {
        private readonly string _token;
        private readonly string? _replacement;

        public RecoveringAuth(string token, string? replacement)
        {
            _token = token;
            _replacement = replacement;
        }

        public Action? OnInvalidate { get; init; }

        public List<string> Rejected { get; } = new();

        public Task<ResolvedAuth> ResolveAsync(ProviderContext context, CancellationToken cancellationToken) => Task.FromResult(new ResolvedAuth { Token = _token });

        public Task<ResolvedAuth?> InvalidateAsync(ProviderContext context, string rejectedToken, CancellationToken cancellationToken)
        {
            lock (Rejected) Rejected.Add(rejectedToken);

            OnInvalidate?.Invoke();

            // Like the real resolver: it replaces the token it handed out, and has nothing further for the replacement itself (a pair rotated moments ago is not rotated again).
            return Task.FromResult(_replacement is null || rejectedToken != _token ? null : new ResolvedAuth { Token = _replacement });
        }
    }
}
