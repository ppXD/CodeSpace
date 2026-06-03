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

    /// <summary>Scopes GitLab OAuth needs that aren't a capability requirement. None today — <c>api</c>
    /// (required by the capabilities below) also covers profile/identity reads.</summary>
    private static readonly string[] ExtraOAuthScopes = Array.Empty<string>();

    /// <summary>
    /// DERIVED from <see cref="CapabilityScopeRequirements"/> so it can never drift: the broadest scope each
    /// capability needs (all resolve to GitLab's umbrella <c>api</c>) plus <see cref="ExtraOAuthScopes"/>.
    /// Resolves to <c>[api]</c> today — one consent covers list/webhook/review/profile. Add a capability that
    /// needs a new scope and this grows automatically.
    /// </summary>
    public IReadOnlyList<string> DefaultOAuthScopes => OAuthScopeDefaults.Compute(ExtraOAuthScopes, CapabilityScopeRequirements);

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

    public IReadOnlyDictionary<Type, RepositoryRole> CapabilityRoleRequirements { get; } = new Dictionary<Type, RepositoryRole>
    {
        // Approving / posting an MR note is a Developer-level action on GitLab — Reporter and Guest can't.
        // RepositoryRole.Write maps to GitLab Developer (access_level 30), preserving the historical
        // ≥Developer membership threshold this replaces. Every other capability falls back to the Read
        // floor — none of them is act-as-user-gated today, so the floor never fires for them.
        [typeof(IPullRequestReviewCapability)] = RepositoryRole.Write
    };
}
