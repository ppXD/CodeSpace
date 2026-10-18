using System.Text.RegularExpressions;
using CodeSpace.Core.Services.OutboundHttp;
using CodeSpace.Messages.Exceptions;

namespace CodeSpace.Core.Services.Agents.Workspace;

/// <summary>
/// Names the redirect a tokened git command met. git follows a redirect of the ref advertisement itself and makes the
/// origin it was sent to the remote's new base; the credential helper (<see cref="TokenedGitCommand"/>) answers for the
/// remote's own scheme and authority only, so at the new origin no credential is found and git fails with
/// <c>could not read Username for '&lt;that origin&gt;': terminal prompts disabled</c> — its own text, which reads like a
/// credential that was never configured. When that origin is not the remote's, the failure says what happened instead: the
/// remote redirected to <c>&lt;origin&gt;</c>, as a provider's REST call to the same address says
/// (<see cref="ProviderBaseUrlRedirectsException"/>, code <c>provider_base_url_redirects</c>).
///
/// <para>Still a <see cref="WorkspaceException"/>: its catch sites — the push retry, a per-repo push that never fails a run,
/// the workflow nodes, the supervisor's grader — treat any git failure as one, and a failure that is a different type would
/// walk past all of them. A prompt at the remote's own origin is not a redirect (a remote that refused the credential once is
/// not asked again) and keeps its plain failure. Nothing is rewritten and nothing is sent anywhere new: this only reads what
/// git already reported.</para>
/// </summary>
internal static partial class GitRemoteRedirect
{
    /// <summary>
    /// <paramref name="message"/> as a <see cref="WorkspaceException"/>, naming the redirect when <paramref name="stderr"/> —
    /// the failed command's output against <paramref name="remoteUrl"/> — shows git was sent to another origin.
    /// </summary>
    internal static WorkspaceException Failure(string remoteUrl, string stderr, string message) =>
        Named(remoteUrl, stderr) is { } redirect ? new WorkspaceException(message, redirect) : new WorkspaceException(message);

    /// <summary>The redirect <paramref name="stderr"/> shows, or null: git asked for a credential at an http(s) origin other than <paramref name="remoteUrl"/>'s own.</summary>
    internal static ProviderBaseUrlRedirectsException? Named(string remoteUrl, string stderr)
    {
        if (HttpUri(remoteUrl) is not { } remote || CredentialPrompt().Match(stderr) is not { Success: true } prompt) return null;

        if (HttpUri(prompt.Groups["origin"].Value) is not { } sentTo || OriginScopedRedirectHandler.IsSameOrigin(remote, sentTo)) return null;

        return new ProviderBaseUrlRedirectsException(remote, sentTo);
    }

    private static Uri? HttpUri(string url) => Uri.TryCreate(url.Trim(), UriKind.Absolute, out var parsed) && (parsed.Scheme == Uri.UriSchemeHttp || parsed.Scheme == Uri.UriSchemeHttps) ? parsed : null;

    [GeneratedRegex(@"could not read (?:Username|Password) for '(?<origin>[^']+)'", RegexOptions.CultureInvariant)]
    private static partial Regex CredentialPrompt();
}
