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
/// <para>An OAuth credential also carries the way to ask for a replacement when GitLab answers 401 to its token
/// (<see cref="RecoverAsync"/>). The replacement STAYS: this one object is what the typed client and every raw call of a
/// provider call present, so once a request has been refused and the token replaced, the requests after it — the write that
/// follows a read, the next page — carry the replacement without being refused first. A static token carries no way to ask.</para>
///
/// <para>A class that overrides <see cref="ToString"/>: a record's generated one would print the token into whatever
/// message or log line a value of this type is formatted into.</para>
/// </summary>
internal sealed class GitLabCredential
{
    internal const string PrivateTokenHeader = "PRIVATE-TOKEN";

    internal const string AuthorizationHeader = "Authorization";

    private readonly Func<string, CancellationToken, Task<string?>>? _recover;
    private volatile string _token;

    /// <param name="recover">Given the token GitLab refused, the token to try instead — null when there is none. Absent for a credential that cannot be replaced.</param>
    public GitLabCredential(string token, AuthType type, Func<string, CancellationToken, Task<string?>>? recover = null)
    {
        _token = token;
        Type = type;
        _recover = recover;
    }

    /// <summary>The token presented now: the one it was made with until a refusal replaced it.</summary>
    public string Token => _token;

    public AuthType Type { get; }

    /// <summary>The header this credential's type names: <c>PRIVATE-TOKEN</c> for an access token, <c>Authorization</c> for anything else (OAuth).</summary>
    public string HeaderName => IsAccessToken ? PrivateTokenHeader : AuthorizationHeader;

    public string HeaderValue => ValueOf(_token);

    private string ValueOf(string token) => IsAccessToken ? token : $"Bearer {token}";

    private bool IsAccessToken => Type is AuthType.Pat or AuthType.ProjectAccessToken or AuthType.GroupAccessToken;

    /// <summary>This credential as the request's only credential header — a stale one of either name is removed first.</summary>
    public void Attach(HttpRequestMessage request)
    {
        request.Headers.Remove(PrivateTokenHeader);
        request.Headers.Remove(AuthorizationHeader);
        request.Headers.TryAddWithoutValidation(HeaderName, ValueOf(_token));
    }

    /// <summary>The same, for NGitLab's request, whose headers it has already written an <c>Authorization: Bearer</c> into.</summary>
    public void Attach(WebHeaderCollection headers)
    {
        headers.Remove(PrivateTokenHeader);
        headers.Remove(AuthorizationHeader);
        headers[HeaderName] = ValueOf(_token);
    }

    /// <summary>Whether a 401 is worth asking <see cref="RecoverAsync"/> about.</summary>
    public bool CanRecover => _recover is not null;

    /// <summary>
    /// GitLab answered 401 to <paramref name="rejectedToken"/>. True when the credential now carries another token to send the
    /// request again with: a request that was refused alongside this one may already have replaced it, which is taken as the
    /// answer without asking again; otherwise the way to ask is used once. False when the 401 stands.
    /// </summary>
    public async Task<bool> RecoverAsync(string rejectedToken, CancellationToken cancellationToken)
    {
        if (_recover is null) return false;
        if (_token != rejectedToken) return true;

        var replacement = await _recover(rejectedToken, cancellationToken).ConfigureAwait(false);

        if (replacement is null) return false;

        _token = replacement;

        return true;
    }

    public override string ToString() => $"{HeaderName} credential ({Type})";
}
