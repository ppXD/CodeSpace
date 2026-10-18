using CodeSpace.IntegrationTests.Webhooks;
using CodeSpace.Messages.Dtos.Providers;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Exceptions;
using CodeSpace.Messages.Failures;
using CodeSpace.UnitTests.OutboundHttp;
using Shouldly;
using static CodeSpace.IntegrationTests.Webhooks.StubProviderHost;
using static CodeSpace.UnitTests.Providers.GitLab.GitLabLoopback;

namespace CodeSpace.UnitTests.Providers.GitLab;

/// <summary>
/// A GitLab base URL that redirects. The typed client (NGitLab, on HttpWebRequest) dropped its <c>Authorization</c> on a
/// 301 and arrived anonymous; a raw call dropped its Bearer but kept <c>PRIVATE-TOKEN</c> and carried it on to wherever the
/// redirect named — so a PAT worked, an OAuth token read 401 and looked like a random failure, and the PAT went to a host
/// the redirect picked. The provider now follows a redirect only inside one origin (scheme, host and port), with the
/// credential attached again on each hop; a redirect to another origin is not followed — no request is made to it — and
/// the call fails with <see cref="ProviderBaseUrlRedirectsException"/>, which names the URL GitLab pointed at so the owner
/// can edit the base URL. The stub redirects for http to https, a host alias and another port, as a proxy or an
/// <c>external_url</c> that disagrees with the base URL would.
/// </summary>
[Trait("Category", "Unit")]
public sealed class GitLabRedirectTests : IDisposable
{
    private readonly List<IDisposable> _gitlabs = new();
    private readonly List<LoopbackResponder> _elsewhere = new();

    public void Dispose()
    {
        foreach (var gitlab in _gitlabs) gitlab.Dispose();

        foreach (var responder in _elsewhere) responder.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    // ── Inside one origin: followed, credential attached again ──

    public static TheoryData<string, AuthType> SameOriginRedirects()
    {
        var data = new TheoryData<string, AuthType>();

        foreach (var operation in new[] { "ngitlab: project by id", "ngitlab: paged issue list", "raw: member standing", "raw: latest commit", "raw: languages" })
        foreach (var type in new[] { AuthType.Pat, AuthType.OAuth })
            data.Add(operation, type);

        return data;
    }

    [Theory]
    [MemberData(nameof(SameOriginRedirects))]
    public async Task A_redirect_inside_one_origin_is_followed_with_the_credential_attached_again(string operation, AuthType type)
    {
        var gitlab = RedirectingEveryGet(request => request.PathAndQuery + (request.PathAndQuery.Contains('?') ? "&" : "?") + "via=redirect");

        await Operations[operation](Provider(), Context(gitlab.BaseUrl, type));

        var hops = gitlab.Requests.Where(r => r.PathAndQuery.Contains("via=redirect")).ToList();
        hops.ShouldNotBeEmpty($"fixture check: '{operation}' followed the 301 to the same origin");
        gitlab.Requests.Count.ShouldBe(hops.Count * 2, "each call is one request that was redirected and one that followed");

        foreach (var request in gitlab.Requests) ShouldCarryOnly(request, type);
    }

    [Fact]
    public async Task A_raw_post_redirected_inside_one_origin_is_sent_again_with_its_body_and_credential()
    {
        // 307 keeps the method and the body: a raw call's own content can be read twice, so it is re-sent.
        var gitlab = Track(new StubProviderHost().Answer("POST", "/api/v4/", request => request.PathAndQuery.Contains("via=redirect") ? Answer(request) : Redirect(307, request.PathAndQuery + "?via=redirect")));

        var rendered = await Provider().RenderMarkdownAsync(Context(gitlab.BaseUrl, AuthType.OAuth), Repository, "# hello", CancellationToken.None);

        rendered.Html.ShouldBe("<p>x</p>");
        gitlab.Requests.Select(r => r.Method).ShouldBe(new[] { "POST", "POST" });
        gitlab.Requests[1].Body.ShouldBe(gitlab.Requests[0].Body, "the redirected request carries the body again");
        gitlab.Requests[1].Body.ShouldContain("# hello");

        foreach (var request in gitlab.Requests) ShouldCarryOnly(request, AuthType.OAuth);
    }

    [Theory]
    [InlineData(300, "markdown render", AuthType.Pat)]
    [InlineData(301, "markdown render", AuthType.OAuth)]
    [InlineData(302, "markdown render", AuthType.Pat)]
    [InlineData(303, "markdown render", AuthType.OAuth)]
    [InlineData(301, "unapprove", AuthType.Pat)]
    [InlineData(302, "unapprove", AuthType.OAuth)]
    [InlineData(302, "project hook create", AuthType.Pat)]
    public async Task A_raw_write_that_a_redirect_inside_one_origin_would_turn_into_a_get_is_refused_not_read_as_the_writes_answer(int status, string call, AuthType type)
    {
        // A client that follows a 301 or 302 after a POST sends a GET and returns its answer: here the markdown render or the
        // unapprove would "succeed" with whatever a read of the redirected path says. The redirect is named instead, and the
        // write was sent once.
        var gitlab = Track(new StubProviderHost().Answer("POST", "/api/v4/", request => Redirect(status, request.PathAndQuery + "?via=redirect")).Answer("GET", "/api/v4/", request => Answer(request)));
        var context = Context(gitlab.BaseUrl, type);
        var provider = Provider();

        var failure = call switch
        {
            "markdown render" => await Should.ThrowAsync<ProviderBaseUrlRedirectsException>(() => provider.RenderMarkdownAsync(context, Repository, "# hello", CancellationToken.None)),
            "unapprove" => await Should.ThrowAsync<ProviderBaseUrlRedirectsException>(() => provider.SubmitReviewAsync(context, Repository, 7, new SubmitPullRequestReviewInput { Verdict = PullRequestReviewVerdict.RequestChanges, Body = "no" }, CancellationToken.None)),
            _ => (await Should.ThrowAsync<ProviderWebhookRegistrationException>(() => provider.RegisterWebhookAsync(context, Repository, new WebhookRegistration { CallbackUrl = "https://codespace.test/hook", Secret = "whsec", SubscribedEvents = ["Push Hook"] }, CancellationToken.None))).InnerException.ShouldBeOfType<ProviderBaseUrlRedirectsException>()
        };

        failure.RequestedOrigin.ShouldBe(gitlab.BaseUrl);
        failure.FinalOrigin.ShouldBe(gitlab.BaseUrl, "inside one origin: the failure says it was not followed, not that it went elsewhere");
        failure.Message.ShouldContain("never sent again blindly");
        gitlab.Requests.Select(r => r.Method).ShouldBe(new[] { "POST" }, "the write was sent once and nothing followed it");
    }

    [Fact]
    public async Task A_typed_client_write_redirected_inside_one_origin_is_refused_not_replayed()
    {
        // NGitLab has written its body to a stream it does not give back, so a write is never re-sent on a redirect: a
        // second copy of a create that the first redirect may already have landed is worse than a refusal naming the URL.
        var gitlab = Track(new StubProviderHost().Answer("POST", "/api/v4/", request => Redirect(307, request.PathAndQuery + "?via=redirect")).Answer("GET", "/api/v4/", _ => new StubReply(200, "[]")));

        var failure = await Should.ThrowAsync<ProviderBaseUrlRedirectsException>(() => Provider().CreateIssueAsync(Context(gitlab.BaseUrl, AuthType.Pat), Repository, new CreateIssueInput { Title = "t", Body = "b" }, CancellationToken.None));

        failure.FinalUrl.ShouldBe($"{gitlab.BaseUrl}{ProjectPath}/issues");
        gitlab.Requests.Count(r => r.Method == "POST").ShouldBe(1, "the create was sent once and never again");
    }

    // ── To another origin: not followed, nothing sent there, the URL named ──

    public static TheoryData<string, string, AuthType> CrossOriginRedirects()
    {
        var data = new TheoryData<string, string, AuthType>();

        foreach (var operation in new[] { "ngitlab: project by id", "ngitlab: paged issue list", "raw: member standing", "raw: latest commit", "raw: markdown render" })
        foreach (var target in new[] { "http to https", "host alias", "other port" })
        foreach (var type in new[] { AuthType.Pat, AuthType.OAuth })
            data.Add(operation, target, type);

        return data;
    }

    [Theory]
    [MemberData(nameof(CrossOriginRedirects))]
    public async Task A_redirect_to_another_origin_is_an_error_naming_it_and_nothing_is_sent_there(string operation, string target, AuthType type)
    {
        var elsewhere = Elsewhere();
        var origin = OriginOf(target, elsewhere.Port);
        var gitlab = RedirectingEveryRoute(request => origin + request.PathAndQuery.Split('?')[0]);

        var failure = await Should.ThrowAsync<ProviderBaseUrlRedirectsException>(() => Bounded(() => Operations[operation](Provider(), Context(gitlab.BaseUrl, type))));

        failure.FinalOrigin.ShouldBe(origin);
        failure.FinalUrl.ShouldStartWith(origin + "/api/v4/");
        failure.Message.ShouldContain(origin, Case.Sensitive, "the owner reads where GitLab pointed");
        ((IFailure)failure).Code.ShouldBe("provider_base_url_redirects");
        ((IFailure)failure).Kind.ShouldBe(FailureKind.Unprocessable);
        elsewhere.Accepted.ShouldBe(0, "no connection was opened to the other origin, so no credential could have reached it");
        gitlab.Requests.Count.ShouldBe(1, "a redirect is a verdict on the base URL, not a transient fault to retry");
    }

    [Theory]
    [InlineData("http to https", AuthType.Pat)]
    [InlineData("host alias", AuthType.OAuth)]
    [InlineData("other port", AuthType.Pat)]
    [InlineData("other port", AuthType.OAuth)]
    public async Task A_paging_link_to_another_origin_is_an_error_naming_it_and_nothing_is_sent_there(string target, AuthType type)
    {
        // NGitLab follows `Link: <url>; rel="next"` by building a request for whatever URL the answer names: the response is
        // the other origin's to write, so the credential must not follow it there however the page was reached.
        var elsewhere = Elsewhere();
        var origin = OriginOf(target, elsewhere.Port);
        var gitlab = Track(new StubProviderHost().Answer("GET", "/api/v4/", _ => PageWithNext("[]", $"{origin}{ProjectPath}/repository/branches?page=2")));

        var failure = await Should.ThrowAsync<ProviderBaseUrlRedirectsException>(() => Bounded(() => Provider().ListBranchesAsync(Context(gitlab.BaseUrl, type), Repository, CancellationToken.None)));

        failure.FinalOrigin.ShouldBe(origin);
        failure.FinalUrl.ShouldBe($"{origin}{ProjectPath}/repository/branches");
        failure.RequestedOrigin.ShouldBe(gitlab.BaseUrl);
        ((IFailure)failure).Code.ShouldBe("provider_base_url_redirects");
        elsewhere.Accepted.ShouldBe(0, "no connection was opened to the origin the page link named, so no credential could have reached it");
        gitlab.Requests.Count.ShouldBe(1, "the first page was read once and the link was not taken");
    }

    [Theory]
    [InlineData(AuthType.Pat)]
    [InlineData(AuthType.OAuth)]
    public async Task A_paging_link_inside_the_origin_is_followed_with_the_credential_attached_again(AuthType type)
    {
        var gitlab = Track(new StubProviderHost());
        gitlab.Answer("GET", "/api/v4/", request => request.PathAndQuery.Contains("page=2")
            ? new StubReply(200, """[{"name":"two"}]""")
            : PageWithNext("""[{"name":"one"}]""", $"{gitlab.BaseUrl}{ProjectPath}/repository/branches?page=2"));

        var branches = await Provider().ListBranchesAsync(Context(gitlab.BaseUrl, type), Repository, CancellationToken.None);

        branches.Select(b => b.Name).ShouldBe(new[] { "one", "two" });
        gitlab.Requests.Count.ShouldBe(2);

        foreach (var request in gitlab.Requests) ShouldCarryOnly(request, type);
    }

    [Theory]
    [InlineData("group hook create", AuthType.Pat)]
    [InlineData("group hook create", AuthType.OAuth)]
    [InlineData("project hook create", AuthType.Pat)]
    [InlineData("project hook create", AuthType.OAuth)]
    public async Task A_hook_registration_through_a_cross_origin_redirect_fails_with_the_redirect_as_its_failure_and_is_not_sent_again(string hook, AuthType type)
    {
        // The registrar persists this failure as the attempt's error: it must read as the redirect, and the create is sent
        // once — a redirect is not the transient fault the non-idempotent wrapper retries after probing for a landed hook.
        var elsewhere = Elsewhere();
        var origin = OriginOf("other port", elsewhere.Port);
        var gitlab = RedirectingEveryRoute(request => origin + request.PathAndQuery.Split('?')[0]);
        var registration = new WebhookRegistration { CallbackUrl = "https://codespace.test/hook", Secret = "whsec", SubscribedEvents = ["Push Hook"] };

        var failure = hook == "group hook create"
            ? await Should.ThrowAsync<ProviderBaseUrlRedirectsException>(() => Bounded(() => Provider().RegisterConnectionWebhookAsync(Context(gitlab.BaseUrl, type), "acme/platform", registration, CancellationToken.None)))
            : (await Should.ThrowAsync<ProviderWebhookRegistrationException>(() => Bounded(() => Provider().RegisterWebhookAsync(Context(gitlab.BaseUrl, type), Repository, registration, CancellationToken.None)))).InnerException.ShouldBeOfType<ProviderBaseUrlRedirectsException>();

        failure.FinalOrigin.ShouldBe(origin);
        gitlab.Requests.Count(r => r.Method == "POST").ShouldBe(1, "the create was sent once");
        elsewhere.Accepted.ShouldBe(0);
        ((IFailure)failure).Code.ShouldBe("provider_base_url_redirects");
    }

    [Theory]
    [InlineData("http to https", AuthType.Pat)]
    [InlineData("host alias", AuthType.OAuth)]
    [InlineData("other port", AuthType.Pat)]
    public async Task A_credential_probe_through_a_cross_origin_redirect_reports_where_gitlab_pointed(string target, AuthType type)
    {
        // "Use a token" validates through this probe; its answer is the dialog's message — a bare 401 before.
        var elsewhere = Elsewhere();
        var origin = OriginOf(target, elsewhere.Port);
        var gitlab = RedirectingEveryRoute(request => origin + request.PathAndQuery.Split('?')[0]);

        var probe = await Bounded(() => Provider().ProbeCredentialAsync(Context(gitlab.BaseUrl, type), CancellationToken.None));

        probe.IsValid.ShouldBeFalse();
        probe.Error.ShouldNotBeNull().ShouldContain(origin + "/api/v4/user", Case.Sensitive);
        elsewhere.Accepted.ShouldBe(0);
    }

    [Theory]
    [InlineData("ngitlab: project by id")]
    [InlineData("raw: member standing")]
    public async Task A_redirect_to_another_origin_does_not_leak_the_message_credentials_query_or_userinfo(string operation)
    {
        // The Location is the redirecting server's to write, and the message is shown to the owner and logged: the URL it
        // names is scheme, host, port and path alone.
        var gitlab = RedirectingEveryRoute(_ => "https://svc:hunter2@gitlab.example.test:8443/api/v4/projects/42?private_token=SECRET#frag");

        var failure = await Should.ThrowAsync<ProviderBaseUrlRedirectsException>(() => Operations[operation](Provider(), Context(gitlab.BaseUrl, AuthType.Pat)));

        failure.FinalUrl.ShouldBe("https://gitlab.example.test:8443/api/v4/projects/42");
        failure.Message.ShouldNotContain("hunter2");
        failure.Message.ShouldNotContain("SECRET");
    }

    // ── Rig ──

    /// <summary>A call that follows a redirect to the https origin dials a listener that never answers a TLS handshake, and NGitLab's calls block their caller; run off the test thread and bounded, so a regression there fails fast instead of waiting out the client's own timeout.</summary>
    private static async Task<T> Bounded<T>(Func<Task<T>> call) => await Task.Run(call).WaitAsync(TimeSpan.FromSeconds(15));

    private static async Task Bounded(Func<Task> call) => await Task.Run(call).WaitAsync(TimeSpan.FromSeconds(15));

    private StubProviderHost RedirectingEveryGet(Func<RecordedRequest, string> location) =>
        Track(new StubProviderHost().Answer("GET", "/api/v4/", request => request.PathAndQuery.Contains("via=redirect") ? Answer(request) : Redirect(301, location(request))));

    private StubProviderHost RedirectingEveryRoute(Func<RecordedRequest, string> location) =>
        Track(new StubProviderHost().Answer("GET", "/api/v4/", request => Redirect(301, location(request))).Answer("POST", "/api/v4/", request => Redirect(301, location(request))));

    private static StubReply PageWithNext(string json, string next) => new(200, json) { Headers = new Dictionary<string, string> { ["Link"] = $"<{next}>; rel=\"next\"" } };

    private static StubReply Redirect(int status, string location) => new(status, string.Empty) { Headers = new Dictionary<string, string> { ["Location"] = location } };

    private StubProviderHost Track(StubProviderHost gitlab)
    {
        _gitlabs.Add(gitlab);

        return gitlab;
    }

    /// <summary>A listener on another port that answers 200 to anything — and counts the connections it is dialled on, which must stay at none.</summary>
    private LoopbackResponder Elsewhere()
    {
        var responder = LoopbackResponder.Start(_ => LoopbackResponder.Ok("{}"));
        _elsewhere.Add(responder);

        return responder;
    }

    private static string OriginOf(string target, int port) => target switch
    {
        "http to https" => $"https://127.0.0.1:{port}",
        "host alias" => $"http://localhost:{port}",
        "other port" => $"http://127.0.0.1:{port}",
        _ => throw new ArgumentOutOfRangeException(nameof(target), target, "an unknown redirect shape")
    };
}
