namespace CodeSpace.Core.Services.OAuth;

/// <summary>
/// Where a finished OAuth connect may send the browser: a path on this site, or an address on its public origin.
///
/// <para>The return address comes from whoever starts the connect and used to be followed verbatim, so a completed
/// connect could hand the browser to any page on the internet — straight after a consent screen that made the whole
/// trip look legitimate. A path is accepted only with a single leading '/': two, or '/' then '\', is an address on
/// another host to a browser. Control characters are refused anywhere, since browsers strip them and can turn
/// <c>/&#9;/host</c> into <c>//host</c>. An absolute address must be http(s) on exactly the public origin and carry
/// no user-info. Mirrors ASP.NET Core's <c>IsLocalUrl</c> for the path half.</para>
/// </summary>
public static class OAuthReturnUrl
{
    /// <summary>Where a connect lands when it was given no return address, or one that no longer passes.</summary>
    public const string Fallback = "/";

    public static bool IsAllowed(string returnUrl, string publicBaseUrl) => !HasControlCharacter(returnUrl) && (IsSingleSlashPath(returnUrl) || IsOnOrigin(returnUrl, publicBaseUrl));

    public static string AllowedOrFallback(string? returnUrl, string publicBaseUrl) => returnUrl != null && IsAllowed(returnUrl, publicBaseUrl) ? returnUrl : Fallback;

    private static bool IsSingleSlashPath(string url) => url.StartsWith('/') && (url.Length == 1 || (url[1] != '/' && url[1] != '\\'));

    private static bool IsOnOrigin(string url, string publicBaseUrl) =>
        Uri.TryCreate(url, UriKind.Absolute, out var target)
        && Uri.TryCreate(publicBaseUrl, UriKind.Absolute, out var origin)
        && (target.Scheme == Uri.UriSchemeHttps || target.Scheme == Uri.UriSchemeHttp)
        && string.IsNullOrEmpty(target.UserInfo)
        && Uri.Compare(target, origin, UriComponents.SchemeAndServer, UriFormat.Unescaped, StringComparison.OrdinalIgnoreCase) == 0;

    private static bool HasControlCharacter(string url) => url.Any(char.IsControl);
}
