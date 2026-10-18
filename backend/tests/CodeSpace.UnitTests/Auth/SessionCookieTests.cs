using System.Security.Claims;
using CodeSpace.Core.Services.Auth;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Shouldly;

namespace CodeSpace.UnitTests.Auth;

/// <summary>
/// The browser's session cookie: the attributes it is written with, when the bearer handler reads it, and which writes
/// it may not carry on its own. Every rule here is a defence a browser enforces only if the server asked for it, so each
/// one is asserted on the header the browser actually receives.
/// </summary>
[Trait("Category", "Unit")]
public class SessionCookieTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 12, 9, 0, 0, TimeSpan.Zero);

    /// <summary>The SPA, the CORS preflight and an operator's proxy rules all name these. A rename is a wire change, so it has to be a visible one.</summary>
    [Fact]
    public void The_wire_names_are_pinned()
    {
        SessionCookie.Name.ShouldBe("codespace_session");
        SessionCookie.Path.ShouldBe("/api");
        SessionCookie.CsrfHeader.ShouldBe("X-CodeSpace-Csrf");
    }

    [Theory]
    [InlineData("https", true)]   // the browser keeps it off plain HTTP
    [InlineData("http", false)]   // a browser drops a Secure cookie set over HTTP — Safari even on localhost — so a plain-HTTP deployment could never sign in
    public void Issuing_writes_an_http_only_strict_cookie_scoped_to_the_api_that_is_secure_exactly_over_https(string scheme, bool secure)
    {
        var context = ContextAt(scheme);

        SessionCookie.Issue(context, "the-jwt", Now.AddHours(24));

        var cookie = SetCookieOf(context);

        cookie.ShouldStartWith($"{SessionCookie.Name}=the-jwt;");
        HasAttribute(cookie, "httponly").ShouldBeTrue("no script on the page may read the session");
        HasAttribute(cookie, "samesite=strict").ShouldBeTrue("no request another site starts may carry it");
        HasAttribute(cookie, "path=/api").ShouldBeTrue("only the API needs it");
        HasAttribute(cookie, "secure").ShouldBe(secure, $"over {scheme} — Set-Cookie: {cookie}");
    }

    [Fact]
    public void The_cookie_lives_exactly_as_long_as_the_token_it_carries()
    {
        // Relative, not an absolute Expires: a browser with a wrong clock would otherwise drop it early or keep it late.
        var context = ContextAt("https");

        SessionCookie.Issue(context, "the-jwt", Now.AddHours(23).AddSeconds(17));

        HasAttribute(SetCookieOf(context), $"max-age={(23 * 3600) + 17}").ShouldBeTrue(SetCookieOf(context));
    }

    [Theory]
    [InlineData("https", true)]
    [InlineData("http", false)]
    public void Clearing_expires_the_cookie_with_the_attributes_it_was_set_with(string scheme, bool secure)
    {
        // A deletion at a different path or with a different Secure flag is a second cookie, and the browser keeps the first.
        var context = ContextAt(scheme);

        SessionCookie.Clear(context);

        var cookie = SetCookieOf(context);

        cookie.ShouldStartWith($"{SessionCookie.Name}=;");
        HasAttribute(cookie, "expires=Thu, 01 Jan 1970 00:00:00 GMT").ShouldBeTrue(cookie);
        HasAttribute(cookie, "path=/api").ShouldBeTrue(cookie);
        HasAttribute(cookie, "samesite=strict").ShouldBeTrue(cookie);
        HasAttribute(cookie, "httponly").ShouldBeTrue(cookie);
        HasAttribute(cookie, "secure").ShouldBe(secure, cookie);
    }

    [Fact]
    public void A_request_with_only_the_cookie_authenticates_with_the_cookie()
    {
        var request = RequestWith(cookie: "the-jwt", authorization: null);

        SessionCookie.ReadToken(request).ShouldBe("the-jwt");
    }

    [Theory]
    [InlineData("Bearer header-jwt")]
    [InlineData("Bearer ")]   // even a malformed header: the caller chose the header, and its failure must not fall back to a credential it did not send
    [InlineData("Basic abc")]
    public void An_authorization_header_wins_and_the_cookie_is_not_read(string authorization)
    {
        // API clients, the CLI, MCP and every test send the header. A browser cookie riding along must never change who they are.
        var request = RequestWith(cookie: "cookie-jwt", authorization: authorization);

        SessionCookie.ReadToken(request).ShouldBeNull();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void No_cookie_means_no_cookie_token(string? cookie)
    {
        SessionCookie.ReadToken(RequestWith(cookie, authorization: null)).ShouldBeNull();
    }

    [Theory]
    [InlineData("GET")]
    [InlineData("HEAD")]
    [InlineData("OPTIONS")]
    [InlineData("TRACE")]
    public void A_safe_method_may_ride_the_cookie_without_the_header(string method)
    {
        // Downloads and the run-ledger stream are GETs; a GET changes nothing, so a forged one gains nothing.
        SessionCookie.IsUnguardedWrite(RequestFor(method, csrfHeader: null)).ShouldBeFalse();
    }

    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("PATCH")]
    [InlineData("DELETE")]
    [InlineData("PROPFIND")]   // anything not known to be safe is a write
    public void A_write_without_the_header_is_unguarded(string method)
    {
        // A form on another site, or a fetch it makes without a preflight, can send the cookie but cannot set a custom header.
        SessionCookie.IsUnguardedWrite(RequestFor(method, csrfHeader: null)).ShouldBeTrue();
        SessionCookie.IsUnguardedWrite(RequestFor(method, csrfHeader: " ")).ShouldBeTrue("a blank header is no header");
    }

    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("PATCH")]
    [InlineData("DELETE")]
    public void A_write_with_the_header_is_guarded(string method)
    {
        SessionCookie.IsUnguardedWrite(RequestFor(method, csrfHeader: "1")).ShouldBeFalse();
    }

    /// <summary>
    /// A client that predates the cookie — an older SPA bundle, an API client, the CLI — has no way to end one: only the
    /// API can clear an HttpOnly cookie, and the older SPA's sign-out only empties its own storage. So a sign-in that hands
    /// it a cookie hands it a 24-hour session it can never sign out of. The SPA that knows the cookie sends the header on
    /// every request, which is the one thing that tells the two apart.
    /// </summary>
    [Theory]
    [InlineData("1", true)]
    [InlineData("anything", true)]    // presence is the signal, as it is for the write guard
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData(" ", false)]          // a blank header is no header, here as in IsUnguardedWrite
    public void A_session_is_handed_over_as_a_cookie_only_to_a_request_that_carries_the_csrf_header(string? csrfHeader, bool expected)
    {
        var context = ContextAt("https");

        if (csrfHeader != null) context.Request.Headers[SessionCookie.CsrfHeader] = csrfHeader;

        SessionCookie.IssueIfRequested(context, "the-jwt", Now.AddHours(24));

        SetCookieOf(context).StartsWith($"{SessionCookie.Name}=the-jwt;", StringComparison.Ordinal).ShouldBe(expected, $"header {csrfHeader ?? "(none)"} — Set-Cookie: {SetCookieOf(context)}");
        context.Response.Headers.SetCookie.Count.ShouldBe(expected ? 1 : 0);
    }

    [Fact]
    public void Asking_for_the_cookie_outright_never_needs_the_header()
    {
        // POST /api/auth/session is the one endpoint whose whole job is to set the cookie: whoever calls it asked.
        var context = ContextAt("https");

        SessionCookie.Issue(context, "the-jwt", Now.AddHours(24));

        SetCookieOf(context).ShouldStartWith($"{SessionCookie.Name}=the-jwt;");
    }

    [Theory]
    [InlineData(true, true, null, true)]                    // the browser on its cookie: the session must not come back in the body
    [InlineData(true, true, "Bearer header-jwt", false)]    // a header client with a stray cookie: the header carried it
    [InlineData(true, false, "Bearer header-jwt", false)]   // a plain bearer client
    [InlineData(false, true, null, false)]                  // a dead or unguarded cookie: nobody was authenticated, by the cookie or otherwise
    [InlineData(false, false, null, false)]                 // anonymous
    public void Only_a_caller_the_cookie_authenticated_counts_as_carried_by_it(bool authenticated, bool hasCookie, string? authorization, bool expected)
    {
        var context = new DefaultHttpContext { User = authenticated ? new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString())], "Bearer")) : new ClaimsPrincipal(new ClaimsIdentity()) };

        if (hasCookie) context.Request.Headers.Cookie = $"{SessionCookie.Name}=cookie-jwt";
        if (authorization != null) context.Request.Headers.Authorization = authorization;

        SessionCookie.Authenticated(context).ShouldBe(expected);
    }

    private static DefaultHttpContext ContextAt(string scheme)
    {
        var context = new DefaultHttpContext { RequestServices = new ServiceCollection().AddSingleton<TimeProvider>(new FakeTimeProvider(Now)).BuildServiceProvider() };

        context.Request.Scheme = scheme;

        return context;
    }

    private static HttpRequest RequestWith(string? cookie, string? authorization)
    {
        var context = new DefaultHttpContext();

        if (cookie != null) context.Request.Headers.Cookie = $"{SessionCookie.Name}={cookie}";
        if (authorization != null) context.Request.Headers.Authorization = authorization;

        return context.Request;
    }

    private static HttpRequest RequestFor(string method, string? csrfHeader)
    {
        var context = new DefaultHttpContext();

        context.Request.Method = method;

        if (csrfHeader != null) context.Request.Headers[SessionCookie.CsrfHeader] = csrfHeader;

        return context.Request;
    }

    private static string SetCookieOf(HttpContext context) => context.Response.Headers.SetCookie.ToString();

    /// <summary>Whether a Set-Cookie header carries this attribute — compared whole, so "secure" never matches inside another one.</summary>
    private static bool HasAttribute(string setCookie, string attribute) =>
        setCookie.Split(';').Skip(1).Any(a => string.Equals(a.Trim(), attribute, StringComparison.OrdinalIgnoreCase));
}
