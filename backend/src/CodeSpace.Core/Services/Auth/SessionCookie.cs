using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using HeaderNames = Microsoft.Net.Http.Headers.HeaderNames;

namespace CodeSpace.Core.Services.Auth;

/// <summary>
/// The browser's session: the same JWT a bearer client holds, carried in an HttpOnly cookie so that no script on the
/// page can read it. A token in <c>localStorage</c> is one injected script away from leaving the browser for a day; a
/// token the page never sees cannot be exfiltrated by the page.
///
/// <para>The bearer handler reads it only when a request carries no <c>Authorization</c> header, so every API client,
/// the CLI, MCP and the test suite keep authenticating exactly as before, and a browser cookie riding along beside a
/// header never changes who the caller is.</para>
///
/// <para>A cookie is sent by the browser whoever asked for the request, which a header never was. Three things keep a
/// request another site starts from riding it: <c>SameSite=Strict</c>, so the browser does not attach it to a request
/// another site initiates; <see cref="CsrfHeader"/>, which a cookie-authenticated write must carry and which neither a
/// form nor a preflight-free fetch can set; and the API's CORS allow-list, which is the only thing that can grant a
/// foreign origin that header.</para>
/// </summary>
public static class SessionCookie
{
    /// <summary>Pinned by unit test. Not <c>__Host-</c>-prefixed: that prefix demands <c>Secure</c> and <c>Path=/</c>, and this cookie has neither on a plain-HTTP deployment and wants the narrower path.</summary>
    public const string Name = "codespace_session";

    /// <summary>Only API requests need the session. The SPA's own pages never receive it, and neither does <c>/hangfire</c>, which keeps its own write surface off this cookie.</summary>
    public const string Path = "/api";

    /// <summary>
    /// The header the SPA sends on every request. A write authenticated by the cookie alone is refused without it: no HTML
    /// form can set a header, and a cross-origin fetch that sets one is preflighted, which the CORS allow-list answers.
    /// Pinned by unit test — the SPA and any proxy rule name it.
    /// </summary>
    public const string CsrfHeader = "X-CodeSpace-Csrf";

    /// <summary>Hands the browser the session <paramref name="token"/>, to expire with the token itself.</summary>
    public static void Issue(HttpContext context, string token, DateTimeOffset expiresAt) => context.Response.Cookies.Append(Name, token, Options(context, expiresAt - ClockOf(context).GetUtcNow()));

    /// <summary>Tells the browser to drop the session cookie. Written with the attributes it was set with, or the browser treats it as a different cookie and keeps the original.</summary>
    public static void Clear(HttpContext context) => context.Response.Cookies.Delete(Name, Options(context, maxAge: null));

    /// <summary>
    /// The token the bearer handler should validate from the cookie: null when the request carries an <c>Authorization</c>
    /// header (the header is the caller's choice and is read first, even when it fails) or no session cookie at all.
    /// </summary>
    public static string? ReadToken(HttpRequest request) => request.Headers.ContainsKey(HeaderNames.Authorization) ? null : NullIfEmpty(request.Cookies[Name]);

    /// <summary>
    /// True when the caller was authenticated by the cookie: signed in, with no <c>Authorization</c> header the session
    /// could have come from instead. Such a caller may be a script running in the page, which can send the cookie and
    /// <see cref="CsrfHeader"/> alike, so an endpoint that mints a session hands it to this caller ONLY as the cookie —
    /// never in a body that script would read.
    /// </summary>
    public static bool Authenticated(HttpContext context) => context.User.Identity?.IsAuthenticated == true && ReadToken(context.Request) != null;

    /// <summary>True for a request that would change something on the strength of the cookie alone: any method not known to be safe, without <see cref="CsrfHeader"/>.</summary>
    public static bool IsUnguardedWrite(HttpRequest request) => !IsSafeMethod(request.Method) && string.IsNullOrWhiteSpace(request.Headers[CsrfHeader]);

    /// <summary>
    /// <c>Secure</c> exactly when the request arrived over HTTPS — the effective scheme, after whatever forwarded-headers
    /// handling the host applies. A browser discards a Secure cookie set over plain HTTP, Safari on http://localhost too,
    /// so an unconditional Secure would leave every plain-HTTP deployment unable to hold a session (the SPA would then
    /// stay on its bearer token, which is how it degrades, but needlessly).
    /// </summary>
    private static CookieOptions Options(HttpContext context, TimeSpan? maxAge) => new()
    {
        HttpOnly = true,
        Secure = context.Request.IsHttps,
        SameSite = SameSiteMode.Strict,
        Path = Path,
        MaxAge = maxAge,
        IsEssential = true,
    };

    private static bool IsSafeMethod(string method) => HttpMethods.IsGet(method) || HttpMethods.IsHead(method) || HttpMethods.IsOptions(method) || HttpMethods.IsTrace(method);

    private static TimeProvider ClockOf(HttpContext context) => context.RequestServices?.GetService<TimeProvider>() ?? TimeProvider.System;

    private static string? NullIfEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;
}
