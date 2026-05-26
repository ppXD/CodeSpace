using CodeSpace.Core.Services.Providers.Capabilities;
using CodeSpace.Core.Services.Providers.GitHub.Auth;
using CodeSpace.Core.Services.Providers.GitHub.Events;
using CodeSpace.Core.Services.Providers.Modules;
using CodeSpace.Core.Services.Providers.Scopes;
using CodeSpace.Messages.Enums;

namespace CodeSpace.Core.Services.Providers.GitHub;

public sealed class GitHubProviderModule : IProviderModule
{
    public ProviderKind Kind => ProviderKind.GitHub;

    public IReadOnlyList<Type> Capabilities { get; } = new[] { typeof(GitHubRepositoryProvider) };

    public IReadOnlyList<Type> AuthStrategies { get; } = new[] { typeof(GitHubPatAuthStrategy), typeof(GitHubOAuthAuthStrategy) };

    public IReadOnlyList<Type> EventSubscriptions { get; } = new[]
    {
        typeof(GitHubPushEventSubscription),
        typeof(GitHubPullRequestEventSubscription),
        typeof(GitHubIssuesEventSubscription)
    };

    public IReadOnlyList<Type> AuxiliaryServices { get; } = new[] { typeof(GitHubSignatureVerifier), typeof(GitHubEventNormalizer) };

    /// <summary>
    /// The minimum scope set that covers BOTH catalog and webhook capabilities in one consent
    /// screen. We request the union upfront so the user grants once — re-prompting later for
    /// an additional scope is a worse UX than asking for the slightly-larger superset.
    ///
    /// • <c>repo</c> covers repository read + webhook administration on private + public repos.
    ///   (GitHub treats <c>admin:repo_hook</c> as a subset, so <c>repo</c> alone is enough.)
    /// • <c>read:user</c> lets ProbeCredential identify the authenticated user.
    /// </summary>
    public IReadOnlyList<string> DefaultOAuthScopes { get; } = new[] { GitHubScopes.Repo, GitHubScopes.ReadUser };

    public IReadOnlyDictionary<Type, ScopeRequirement> CapabilityScopeRequirements { get; } = new Dictionary<Type, ScopeRequirement>
    {
        // Listing repos: full `repo` reads everything; `public_repo` is enough for public-only.
        [typeof(IRepositoryCatalogCapability)] = ScopeRequirement.AnyOf(GitHubScopes.Repo, GitHubScopes.PublicRepo),

        // Listing PRs uses the same `repo`/`public_repo` family — GitHub treats PR reads as
        // a subset of repo content reads, no separate scope.
        [typeof(IPullRequestCatalogCapability)] = ScopeRequirement.AnyOf(GitHubScopes.Repo, GitHubScopes.PublicRepo),

        // Posting PR conversation comments writes to the underlying issue — `repo` is the
        // canonical write-capable scope. `public_repo` works for public-only repos.
        [typeof(IPullRequestCommentCapability)] = ScopeRequirement.AnyOf(GitHubScopes.Repo, GitHubScopes.PublicRepo),

        // Webhook registration: `repo` is the superset; `admin:repo_hook` is the narrow grant.
        [typeof(IWebhookRegistrationCapability)] = ScopeRequirement.AnyOf(GitHubScopes.Repo, GitHubScopes.AdminRepoHook),

        // Probe just calls /user — any valid token works, no specific scope required.
        [typeof(ICredentialProbeCapability)] = ScopeRequirement.None
    };
}
