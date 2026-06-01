using CodeSpace.Core.Services.Providers.Capabilities;
using CodeSpace.Core.Services.Providers.GitLab.Auth;
using CodeSpace.Core.Services.Providers.GitLab.Events;
using CodeSpace.Core.Services.Providers.Modules;
using CodeSpace.Core.Services.Providers.Scopes;
using CodeSpace.Messages.Enums;

namespace CodeSpace.Core.Services.Providers.GitLab;

public sealed class GitLabProviderModule : IProviderModule
{
    public ProviderKind Kind => ProviderKind.GitLab;

    public IReadOnlyList<Type> Capabilities { get; } = new[] { typeof(GitLabRepositoryProvider) };

    public IReadOnlyList<Type> AuthStrategies { get; } = new[]
    {
        typeof(GitLabPatAuthStrategy),
        typeof(GitLabProjectAccessTokenStrategy),
        typeof(GitLabGroupAccessTokenStrategy),
        typeof(GitLabOAuthAuthStrategy)
    };

    public IReadOnlyList<Type> EventSubscriptions { get; } = new[]
    {
        typeof(GitLabPushEventSubscription),
        typeof(GitLabMergeRequestEventSubscription),
        typeof(GitLabIssueEventSubscription)
    };

    public IReadOnlyList<Type> AuxiliaryServices { get; } = new[] { typeof(GitLabSignatureVerifier), typeof(GitLabEventNormalizer) };

    /// <summary>
    /// GitLab's scope model is coarser: <c>api</c> grants everything we need (list projects,
    /// register webhooks, read user profile). Asking only for <c>read_repository</c> would
    /// let the user finish OAuth then immediately fail on the first webhook registration —
    /// not worth the consent-screen savings.
    /// </summary>
    public IReadOnlyList<string> DefaultOAuthScopes { get; } = new[] { GitLabScopes.Api };

    public IReadOnlyDictionary<Type, ScopeRequirement> CapabilityScopeRequirements { get; } = new Dictionary<Type, ScopeRequirement>
    {
        // Listing repos: `api` (full) or `read_api` (read-only) both let us call /projects.
        // `read_repository` alone does NOT — it's a clone-only scope, doesn't expose the API.
        [typeof(IRepositoryCatalogCapability)] = ScopeRequirement.AnyOf(GitLabScopes.Api, GitLabScopes.ReadApi),

        // Listing MRs hits /projects/:id/merge_requests, which is part of the regular API
        // surface — same scope family as repo catalog.
        [typeof(IPullRequestCatalogCapability)] = ScopeRequirement.AnyOf(GitLabScopes.Api, GitLabScopes.ReadApi),

        // Posting MR comments needs WRITE scope — `api` only (read_api is read-only).
        [typeof(IPullRequestCommentCapability)] = ScopeRequirement.Of(GitLabScopes.Api),

        // Submitting an MR review (posted as a note today) needs the same WRITE scope.
        [typeof(IPullRequestReviewCapability)] = ScopeRequirement.Of(GitLabScopes.Api),

        // Webhook registration: only `api` works on GitLab. No narrower alternative exists.
        [typeof(IWebhookRegistrationCapability)] = ScopeRequirement.Of(GitLabScopes.Api),

        // Probe calls /user — any scope that hits the API (`api`, `read_api`, or `read_user`).
        [typeof(ICredentialProbeCapability)] = ScopeRequirement.AnyOf(GitLabScopes.Api, GitLabScopes.ReadApi, GitLabScopes.ReadUser)
    };
}
