using System.Net;
using System.Text;
using CodeSpace.Core.Services.OutboundHttp;
using Shouldly;

namespace CodeSpace.UnitTests.OutboundHttp;

/// <summary>
/// The redirect follower in isolation, over a scripted inner handler that records every hop it is asked to send. The
/// built-in follower forwarded every author header except <c>Authorization</c> to a different origin; these pin that
/// a cross-origin hop now carries none of them, and that the rest of the built-in behaviour (method rewrite, no
/// https→http downgrade, a bounded hop count) is unchanged.
/// </summary>
[Trait("Category", "Unit")]
public class OriginScopedRedirectHandlerTests
{
    [Theory]
    [InlineData(HttpStatusCode.Found, "http://front.example.test/a", "http://other.example.test/b")]               // host changes
    [InlineData(HttpStatusCode.Found, "http://front.example.test/a", "http://front.example.test:8080/b")]           // port changes
    [InlineData(HttpStatusCode.Found, "http://front.example.test/a", "https://front.example.test/b")]               // scheme changes — an http→https move on the same host too: the author re-points the URL at https to keep them
    [InlineData(HttpStatusCode.TemporaryRedirect, "http://front.example.test/a", "http://other.example.test/b")]   // a body-less 307 is still followed
    public async Task A_cross_origin_redirect_carries_none_of_the_author_headers(HttpStatusCode status, string from, string to)
    {
        var inner = new ScriptedHandler(Redirect(status, to), Ok());

        using var response = await Send(inner, AuthoredRequest(HttpMethod.Get, from));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        inner.Hops[1].Uri.ShouldBe(new Uri(to));
        inner.Hops[1].Headers.ShouldBeEmpty("a hop to a different origin must not carry X-Api-Key, PRIVATE-TOKEN, Cookie or Authorization");
    }

    [Fact]
    public async Task A_same_origin_redirect_keeps_the_author_headers()
    {
        var inner = new ScriptedHandler(Redirect(HttpStatusCode.MovedPermanently, "/v2/items"), Ok());

        using var response = await Send(inner, AuthoredRequest(HttpMethod.Get, "http://api.example.test/v1/items"));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        inner.Hops[1].Uri.ShouldBe(new Uri("http://api.example.test/v2/items"), "a relative Location resolves against the current URL");
        inner.Hops[1].Headers.Keys.ShouldBe(new[] { "Authorization", "Cookie", "PRIVATE-TOKEN", "X-Api-Key" }, ignoreOrder: true);
    }

    [Theory]
    [InlineData(HttpStatusCode.SeeOther, "POST", "GET", false)]
    [InlineData(HttpStatusCode.Found, "POST", "GET", false)]
    [InlineData(HttpStatusCode.MovedPermanently, "POST", "GET", false)]
    [InlineData(HttpStatusCode.SeeOther, "PUT", "GET", false)]
    [InlineData(HttpStatusCode.TemporaryRedirect, "POST", "POST", true)]
    [InlineData(HttpStatusCode.PermanentRedirect, "PUT", "PUT", true)]
    [InlineData(HttpStatusCode.Found, "PUT", "PUT", true)]
    public async Task A_redirect_keeps_or_rewrites_the_method_like_the_builtin_follower(HttpStatusCode status, string method, string expectedMethod, bool keepsBody)
    {
        var inner = new ScriptedHandler(Redirect(status, "/next"), Ok());
        var request = AuthoredRequest(new HttpMethod(method), "http://api.example.test/start");
        request.Content = new StringContent("{\"a\":1}", Encoding.UTF8, "application/json");

        using var response = await Send(inner, request);

        inner.Hops[1].Method.ShouldBe(expectedMethod);
        (inner.Hops[1].Body != null).ShouldBe(keepsBody);
    }

    [Theory]
    [InlineData(HttpStatusCode.TemporaryRedirect, "POST")]
    [InlineData(HttpStatusCode.PermanentRedirect, "PUT")]
    [InlineData(HttpStatusCode.Found, "PUT")]               // 300/301/302 keep the body for every method but POST
    [InlineData(HttpStatusCode.MovedPermanently, "PATCH")]
    public async Task A_redirect_that_would_carry_the_body_to_another_origin_is_returned_unfollowed(HttpStatusCode status, string method)
    {
        // Dropping the headers is not enough when the method keeps its body: an OAuth token request or a webhook POST
        // carries its secret there. Re-sending it without the body would be a different request, so the 3xx comes back.
        var inner = new ScriptedHandler(Redirect(status, "http://collector.example.test/collect"), Ok());
        var request = AuthoredRequest(new HttpMethod(method), "http://api.example.test/token");
        request.Content = new StringContent("{\"client_secret\":\"FAKE-BODY-SECRET\"}", Encoding.UTF8, "application/json");

        using var response = await Send(inner, request);

        response.StatusCode.ShouldBe(status);
        inner.Hops.Count.ShouldBe(1, "the body never reaches the other origin");
    }

    [Theory]
    [InlineData("https://api.example.test/a", "http://api.example.test/b")]  // downgrade
    [InlineData("http://api.example.test/a", "ftp://api.example.test/b")]    // not http
    [InlineData("http://api.example.test/a", "file:///etc/passwd")]          // not http
    public async Task A_redirect_the_builtin_follower_refuses_is_returned_unfollowed(string from, string to)
    {
        var inner = new ScriptedHandler(Redirect(HttpStatusCode.Found, to));

        using var response = await Send(inner, AuthoredRequest(HttpMethod.Get, from));

        response.StatusCode.ShouldBe(HttpStatusCode.Found);
        inner.Hops.Count.ShouldBe(1);
    }

    [Fact]
    public async Task A_redirect_loop_stops_after_the_hop_limit_and_returns_the_last_redirect()
    {
        var inner = new ScriptedHandler(Enumerable.Repeat(Redirect(HttpStatusCode.Found, "/again"), 50).ToArray());

        using var response = await Send(inner, AuthoredRequest(HttpMethod.Get, "http://api.example.test/start"));

        response.StatusCode.ShouldBe(HttpStatusCode.Found);
        inner.Hops.Count.ShouldBe(OriginScopedRedirectHandler.MaxRedirects + 1, "the first request plus MaxRedirects follow-ups, then stop");
    }

    [Fact]
    public async Task A_response_that_is_not_a_redirect_is_returned_as_is()
    {
        var inner = new ScriptedHandler(() => new HttpResponseMessage(HttpStatusCode.NotModified));

        using var response = await Send(inner, AuthoredRequest(HttpMethod.Get, "http://api.example.test/x"));

        response.StatusCode.ShouldBe(HttpStatusCode.NotModified);
        inner.Hops.Count.ShouldBe(1);
    }

    [Theory]
    [InlineData(HttpStatusCode.MovedPermanently, "http://front.example.test/a", "http://other.example.test/b")]   // host changes
    [InlineData(HttpStatusCode.Found, "http://front.example.test/a", "http://front.example.test:8080/b")]        // port changes
    [InlineData(HttpStatusCode.Found, "http://front.example.test/a", "https://front.example.test/b")]            // http to https on the same host
    [InlineData(HttpStatusCode.TemporaryRedirect, "http://front.example.test/a", "http://other.example.test/b")]
    [InlineData(HttpStatusCode.Found, "https://front.example.test/a", "http://front.example.test/b")]            // a downgrade
    public async Task A_caller_that_keeps_its_credential_to_one_origin_gets_a_cross_origin_redirect_back_unfollowed(HttpStatusCode status, string from, string to)
    {
        // A git provider's token belongs to the one origin it was connected for. Followed anonymously a cross-origin hop
        // is a 401 that reads as a bad token; returned, the caller can name the URL — and nothing was sent there.
        var inner = new ScriptedHandler(Redirect(status, to), Ok());

        using var response = await Send(inner, AuthoredRequest(HttpMethod.Get, from), followsCrossOrigin: false);

        response.StatusCode.ShouldBe(status);
        response.Headers.Location.ShouldBe(new Uri(to));
        inner.Hops.Count.ShouldBe(1, "no request was made to the other origin");
    }

    [Fact]
    public async Task A_caller_that_keeps_its_credential_to_one_origin_still_follows_a_same_origin_redirect_with_every_header()
    {
        var inner = new ScriptedHandler(Redirect(HttpStatusCode.MovedPermanently, "/v2/items"), Redirect(HttpStatusCode.Found, "http://API.example.test:80/v3/items"), Ok());

        using var response = await Send(inner, AuthoredRequest(HttpMethod.Get, "http://api.example.test/v1/items"), followsCrossOrigin: false);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        inner.Hops.Select(h => h.Uri.AbsolutePath).ShouldBe(new[] { "/v1/items", "/v2/items", "/v3/items" }, "a spelling of the same origin (case, the default port) is the same origin");

        foreach (var hop in inner.Hops) hop.Headers.Keys.ShouldBe(new[] { "Authorization", "Cookie", "PRIVATE-TOKEN", "X-Api-Key" }, ignoreOrder: true);
    }

    [Fact]
    public async Task A_caller_that_keeps_its_credential_to_one_origin_re_sends_a_body_inside_the_origin()
    {
        var inner = new ScriptedHandler(Redirect(HttpStatusCode.TemporaryRedirect, "/next"), Ok());
        var request = AuthoredRequest(HttpMethod.Post, "http://api.example.test/start");
        request.Content = new StringContent("{\"a\":1}", Encoding.UTF8, "application/json");

        using var response = await Send(inner, request, followsCrossOrigin: false);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        inner.Hops[1].Body.ShouldBe("{\"a\":1}");
    }

    [Theory]
    [InlineData(HttpStatusCode.MovedPermanently, "POST")]
    [InlineData(HttpStatusCode.Found, "POST")]
    [InlineData(HttpStatusCode.SeeOther, "POST")]
    [InlineData(HttpStatusCode.MultipleChoices, "POST")]
    [InlineData(HttpStatusCode.SeeOther, "PUT")]
    [InlineData(HttpStatusCode.Found, "PUT")]               // a PUT is not turned into a GET, but the new request is the client's guess
    [InlineData(HttpStatusCode.MovedPermanently, "DELETE")]
    public async Task A_caller_whose_writes_are_answered_by_the_write_gets_a_rewriting_redirect_back_unfollowed(HttpStatusCode status, string method)
    {
        // A POST that a 301 or 302 turns into a GET would return that GET's answer as the write's: a markdown render, an
        // unapprove, a hook create that "succeeded" because a list came back. Returned, the caller can say so.
        var inner = new ScriptedHandler(Redirect(status, "/next"), Ok());
        var request = AuthoredRequest(new HttpMethod(method), "http://api.example.test/start");
        request.Content = new StringContent("{\"a\":1}", Encoding.UTF8, "application/json");

        using var response = await Send(inner, request, repeatsWritesOnly: true);

        response.StatusCode.ShouldBe(status);
        inner.Hops.Count.ShouldBe(1, "the write was sent once, and nothing was sent after it");
    }

    [Theory]
    [InlineData(HttpStatusCode.TemporaryRedirect, "POST")]
    [InlineData(HttpStatusCode.PermanentRedirect, "POST")]
    [InlineData(HttpStatusCode.TemporaryRedirect, "PUT")]
    [InlineData(HttpStatusCode.MovedPermanently, "GET")]
    [InlineData(HttpStatusCode.Found, "HEAD")]
    [InlineData(HttpStatusCode.SeeOther, "GET")]
    public async Task A_caller_whose_writes_are_answered_by_the_write_still_follows_a_redirect_that_repeats_the_request(HttpStatusCode status, string method)
    {
        // 307 and 308 mean "send this same request there"; a read is the same read at any status.
        var inner = new ScriptedHandler(Redirect(status, "/next"), Ok());
        var request = AuthoredRequest(new HttpMethod(method), "http://api.example.test/start");
        if (method is "POST" or "PUT") request.Content = new StringContent("{\"a\":1}", Encoding.UTF8, "application/json");

        using var response = await Send(inner, request, repeatsWritesOnly: true);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        inner.Hops[1].Method.ShouldBe(method);
    }

    [Fact]
    public void By_default_a_write_may_be_rewritten_by_a_redirect()
    {
        new OriginScopedRedirectHandler().RepeatsWritesOnly.ShouldBeFalse();
    }

    [Fact]
    public void By_default_a_cross_origin_redirect_is_followed()
    {
        // http.request has always followed them (dropping every header); the new mode is opt-in.
        new OriginScopedRedirectHandler().FollowsCrossOrigin.ShouldBeTrue();
    }

    private static async Task<HttpResponseMessage> Send(ScriptedHandler inner, HttpRequestMessage request, bool followsCrossOrigin = true, bool repeatsWritesOnly = false)
    {
        using var invoker = new HttpMessageInvoker(new OriginScopedRedirectHandler { InnerHandler = inner, FollowsCrossOrigin = followsCrossOrigin, RepeatsWritesOnly = repeatsWritesOnly });

        return await invoker.SendAsync(request, CancellationToken.None);
    }

    private static HttpRequestMessage AuthoredRequest(HttpMethod method, string url)
    {
        var request = new HttpRequestMessage(method, url);

        request.Headers.TryAddWithoutValidation("X-Api-Key", "FAKE-TEAM-API-KEY");
        request.Headers.TryAddWithoutValidation("PRIVATE-TOKEN", "FAKE-PRIVATE-TOKEN");
        request.Headers.TryAddWithoutValidation("Cookie", "sid=FAKE-COOKIE");
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer FAKE-BEARER");

        return request;
    }

    private static Func<HttpResponseMessage> Redirect(HttpStatusCode status, string location) => () =>
    {
        var response = new HttpResponseMessage(status);
        response.Headers.Location = new Uri(location, UriKind.RelativeOrAbsolute);
        return response;
    };

    private static Func<HttpResponseMessage> Ok() => () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("ok") };

    private sealed record Hop(Uri Uri, string Method, Dictionary<string, string> Headers, string? Body);

    /// <summary>Answers each hop with the next scripted response, recording what the hop carried.</summary>
    private sealed class ScriptedHandler : HttpMessageHandler
    {
        private readonly Queue<Func<HttpResponseMessage>> _responses;

        public ScriptedHandler(params Func<HttpResponseMessage>[] responses) { _responses = new Queue<Func<HttpResponseMessage>>(responses); }

        public List<Hop> Hops { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var headers = request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value));
            var body = request.Content == null ? null : await request.Content.ReadAsStringAsync(cancellationToken);

            Hops.Add(new Hop(request.RequestUri!, request.Method.Method, headers, body));

            return _responses.Dequeue()();
        }
    }
}
