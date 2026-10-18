using System.Net;
using CodeSpace.Messages.Enums;

namespace CodeSpace.Core.Services.Providers.GitLab;

/// <summary>
/// The token a GitLab call presents and the ONE header it travels in. A personal, project or group access token goes in
/// <c>PRIVATE-TOKEN</c>, which every GitLab version reads as exactly that; an OAuth token goes in
/// <c>Authorization: Bearer</c>. Never both, and never an access token as a Bearer: GitLab 13.0 to 17.7 reads a Bearer
/// value of 41 or more characters as an OAuth token alone, so a long access token (a custom prefix, a 66-character token) is
/// refused there however valid it is — a 401 that looks like a revoked token. Sending both made the raw calls work with a
/// PAT and fail with an OAuth token as soon as a redirect dropped the Bearer, and the typed client (Bearer only) fail on
/// every long access token.
///
/// <para>A struct that overrides <see cref="ToString"/>: a record's generated one would print the token into whatever
/// message or log line a value of this type is formatted into.</para>
/// </summary>
internal readonly struct GitLabCredential
{
    internal const string PrivateTokenHeader = "PRIVATE-TOKEN";

    internal const string AuthorizationHeader = "Authorization";

    public GitLabCredential(string token, AuthType type)
    {
        Token = token;
        Type = type;
    }

    public string Token { get; }

    public AuthType Type { get; }

    /// <summary>The header this credential's type names: <c>PRIVATE-TOKEN</c> for an access token, <c>Authorization</c> for anything else (OAuth).</summary>
    public string HeaderName => IsAccessToken ? PrivateTokenHeader : AuthorizationHeader;

    public string HeaderValue => IsAccessToken ? Token : $"Bearer {Token}";

    private bool IsAccessToken => Type is AuthType.Pat or AuthType.ProjectAccessToken or AuthType.GroupAccessToken;

    /// <summary>This credential as the request's only credential header — a stale one of either name is removed first.</summary>
    public void Attach(HttpRequestMessage request)
    {
        request.Headers.Remove(PrivateTokenHeader);
        request.Headers.Remove(AuthorizationHeader);
        request.Headers.TryAddWithoutValidation(HeaderName, HeaderValue);
    }

    /// <summary>The same, for NGitLab's request, whose headers it has already written an <c>Authorization: Bearer</c> into.</summary>
    public void Attach(WebHeaderCollection headers)
    {
        headers.Remove(PrivateTokenHeader);
        headers.Remove(AuthorizationHeader);
        headers[HeaderName] = HeaderValue;
    }

    public override string ToString() => $"{HeaderName} credential ({Type})";
}
