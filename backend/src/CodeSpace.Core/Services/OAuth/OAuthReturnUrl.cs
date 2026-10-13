namespace CodeSpace.Core.Services.OAuth;

/// <summary>
/// Where a finished OAuth connect may send the browser: a path on this site, or an address on an origin this deployment
/// serves its web app from (<c>OAuthReturnOriginsSetting</c>).
///
/// <para>The return address comes from whoever starts the connect and used to be followed verbatim, so a completed
/// connect could hand the browser to any page on the internet — straight after a consent screen that made the whole
/// trip look legitimate. A path is accepted only with a single leading '/': two, or '/' then '\', is an address on
/// another host to a browser. Control characters are refused anywhere, since browsers strip them and can turn
/// <c>/&#9;/host</c> into <c>//host</c>. An absolute address must be http(s) on exactly one of the origins and carry
/// no user-info. Mirrors ASP.NET Core's <c>IsLocalUrl</c> for the path half.</para>
/// </summary>
public static class OAuthReturnUrl
{
    /// <summary>Where a connect lands when it was given no return address, or one that no longer passes.</summary>
    public const string Fallback = "/";

    /// <summary>
    /// The SPA's own route that finishes a connect in the browser. Ours, like <c>/invite/…</c> — the frontend's route,
    /// never an operator's setting.
    /// </summary>
    public const string CompletionPage = "/oauth-callback";

    /// <summary>The fragment keys the completion page (frontend/src/routes/oauth-callback.tsx) reads a handed-off code and state from.</summary>
    public const string HandOffCodeKey = "oauthCode";

    public const string HandOffStateKey = "oauthState";

    public static bool IsAllowed(string returnUrl, IReadOnlyCollection<string> origins) => !HasControlCharacter(returnUrl) && (IsSingleSlashPath(returnUrl) || origins.Any(origin => IsOnOrigin(returnUrl, origin)));

    public static string AllowedOrFallback(string? returnUrl, IReadOnlyCollection<string> origins) => returnUrl != null && IsAllowed(returnUrl, origins) ? returnUrl : Fallback;

    /// <summary>
    /// Where a connect handed to the SPA finishes: the completion page on the origin the return address named, when that
    /// origin still passes, or on this site otherwise. The provider's code travels with this redirect, so it can never
    /// name an origin outside the set — a forwarded authorize link must not deliver someone's code to its starter's page.
    /// </summary>
    public static string CompletionPageFor(string? returnUrl, IReadOnlyCollection<string> origins) => OriginOf(AllowedOrFallback(returnUrl, origins)) + CompletionPage;

    /// <summary>
    /// The redirect that hands a connect to the completion page. The code and state ride in the fragment, which a browser
    /// never sends to any server: the SPA host's access log, a proxy in front of it and a Referer all see the path alone.
    /// </summary>
    public static string HandOff(string completionPage, string code, string state) => $"{completionPage}#{HandOffCodeKey}={Uri.EscapeDataString(code)}&{HandOffStateKey}={Uri.EscapeDataString(state)}";

    private static bool IsSingleSlashPath(string url) => url.StartsWith('/') && (url.Length == 1 || (url[1] != '/' && url[1] != '\\'));

    private static bool IsOnOrigin(string url, string origin) =>
        Uri.TryCreate(url, UriKind.Absolute, out var target)
        && Uri.TryCreate(origin, UriKind.Absolute, out var allowed)
        && (target.Scheme == Uri.UriSchemeHttps || target.Scheme == Uri.UriSchemeHttp)
        && string.IsNullOrEmpty(target.UserInfo)
        && Uri.Compare(target, allowed, UriComponents.SchemeAndServer, UriFormat.Unescaped, StringComparison.OrdinalIgnoreCase) == 0;

    /// <summary>The scheme and authority of an allowed absolute address; empty for a path, which stays on this site.</summary>
    private static string OriginOf(string allowedReturnUrl) => IsSingleSlashPath(allowedReturnUrl) ? string.Empty : new Uri(allowedReturnUrl).GetLeftPart(UriPartial.Authority);

    private static bool HasControlCharacter(string url) => url.Any(char.IsControl);
}
