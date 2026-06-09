using CodeSpace.Core.Services.Providers.Capabilities;
using CodeSpace.Core.Services.Providers.GitHub;
using CodeSpace.Core.Services.Providers.GitLab;
using Shouldly;

namespace CodeSpace.UnitTests.Providers.Scopes;

/// <summary>
/// Pins the new <see cref="IRepositorySourceCapability"/> to the repo-READ scope family on both providers
/// (the same family the catalog/PR reads already require). Asserted via <c>IsSatisfied</c> rather than
/// instance equality because ScopeRequirement's list uses reference equality. The companion guarantee —
/// that adding this capability does NOT widen the OAuth consent screen — is the last test here and is also
/// enforced by OAuthScopeDefaultsTests' module pins.
/// </summary>
[Trait("Category", "Unit")]
public class RepositorySourceScopeTests
{
    [Fact]
    public void GitHub_requires_repo_or_public_repo_to_browse_source()
    {
        var reqs = new GitHubProviderModule().CapabilityScopeRequirements;
        reqs.ShouldContainKey(typeof(IRepositorySourceCapability));

        var req = reqs[typeof(IRepositorySourceCapability)];
        req.IsSatisfied(new[] { "repo" }).ShouldBeTrue();
        req.IsSatisfied(new[] { "public_repo" }).ShouldBeTrue();
        req.IsSatisfied(new[] { "read:user" }).ShouldBeFalse();
    }

    [Fact]
    public void GitLab_requires_api_or_read_api_to_browse_source()
    {
        var reqs = new GitLabProviderModule().CapabilityScopeRequirements;
        reqs.ShouldContainKey(typeof(IRepositorySourceCapability));

        var req = reqs[typeof(IRepositorySourceCapability)];
        req.IsSatisfied(new[] { "api" }).ShouldBeTrue();
        req.IsSatisfied(new[] { "read_api" }).ShouldBeTrue();
        // read_repository is clone-only — it can't reach the tree/files REST endpoints, so it must NOT satisfy.
        req.IsSatisfied(new[] { "read_repository" }).ShouldBeFalse();
    }

    [Fact]
    public void Adding_source_browsing_does_not_widen_the_oauth_consent()
    {
        new GitHubProviderModule().DefaultOAuthScopes.ShouldBe(new[] { "repo", "read:user" });
        new GitLabProviderModule().DefaultOAuthScopes.ShouldBe(new[] { "api" });
    }
}
