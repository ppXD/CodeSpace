using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Redaction;
using CodeSpace.Messages.Enums;
using Microsoft.Extensions.Logging;

namespace CodeSpace.Core.Services.Agents.Workspace;

/// <summary>
/// Whether a GitLab repository is cloned from the origin its provider connection was set up for. A clone's credential is
/// answered for the clone URL's scheme and authority alone (<see cref="TokenedGitCommand"/>), so a URL the host redirects to
/// another origin — http to https, a host alias, another port — finds no credential and fails with a prompt for one, which
/// <see cref="GitRemoteRedirect"/> names as the redirect it is once the command has failed (<c>provider_base_url_redirects</c>,
/// as the provider's REST calls name it). This check is the other half: it says so BEFORE any clone, from the two stored
/// URLs, when they disagree — but not when they agree and a proxy in front redirects anyway, which only the failed command
/// can show. The two origins differ when GitLab's
/// own <c>external_url</c> disagrees with the base URL the owner typed, which is exactly the case that redirects. Nothing is
/// rewritten — the clone URL is what GitLab reported, the base URL what the owner stored, and each is theirs to change — but
/// the first thing anyone debugging that run needs is the pair, so the resolve says so, with both origins, and only when a
/// credential is about to be presented (an anonymous clone follows a redirect without harm).
/// </summary>
internal static class CloneUrlAuthority
{
    /// <summary>
    /// True when <paramref name="cloneUrl"/> and the provider's <paramref name="baseUrl"/> are both http(s) URLs of a GitLab
    /// connection and name different origins — scheme, host or port; path and userinfo are not part of one.
    /// </summary>
    internal static bool IsForeign(ProviderKind provider, string? baseUrl, string? cloneUrl) => provider == ProviderKind.GitLab && IsHttp(baseUrl) && IsHttp(cloneUrl) && !CredentialUrl.SameEndpoint(baseUrl, cloneUrl);

    /// <summary>Logs a Warning naming both origins when <paramref name="repository"/> is about to be cloned with a credential from an origin other than its provider's base URL. Never throws, never changes the request.</summary>
    internal static void WarnWhenForeign(ILogger logger, Repository repository, bool credentialed)
    {
        if (!credentialed || !IsForeign(repository.ProviderInstance.Provider, repository.ProviderInstance.BaseUrl, repository.CloneUrlHttps)) return;

        logger.LogWarning("Repository {RepositoryId} is cloned from {CloneOrigin}, which is not the origin of its {Provider} provider's base URL {BaseOrigin}. git presents the credential to the clone URL's origin only, so if either one redirects to the other the clone fails asking for credentials. Edit the provider's base URL to the origin GitLab serves from, or correct GitLab's external_url", repository.Id, CredentialUrl.Endpoint(repository.CloneUrlHttps), repository.ProviderInstance.Provider, CredentialUrl.Endpoint(repository.ProviderInstance.BaseUrl));
    }

    private static bool IsHttp(string? url) => Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var parsed) && (parsed.Scheme == Uri.UriSchemeHttp || parsed.Scheme == Uri.UriSchemeHttps);
}
