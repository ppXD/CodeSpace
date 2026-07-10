using CodeSpace.Core.Services.Providers.Capabilities;
using CodeSpace.Core.Services.Providers.GitHub;
using CodeSpace.Core.Services.Providers.GitLab;
using Shouldly;

namespace CodeSpace.UnitTests.Providers;

/// <summary>
/// DC-2c (bind-or-create idempotency) — <see cref="IPullRequestCatalogCapability.FindPullRequestByBranchAsync"/>
/// itself calls the SDK directly (Octokit / NGitLab HTTP), which — like every provider capability in this
/// codebase — is a thin, untestable-without-network wrapper (see <c>PullRequestReviewCapabilityTests</c>'s own
/// doc). The rigor surface here is the WIRING: both providers must implement the capability so the registry's
/// interface-scan resolves it (the same check <c>PullRequestReviewCapabilityTests.Provider_implements_the_review_capability</c>
/// pins for the review capability), since the new bind-or-create fallback in each provider's
/// <c>OpenPullRequestAsync</c> calls it as <c>this.FindPullRequestByBranchAsync(...)</c> — a compile-time
/// dependency the interface guarantees, not a runtime lookup.
/// </summary>
[Trait("Category", "Unit")]
public class PullRequestCatalogCapabilityTests
{
    [Theory]
    [InlineData(typeof(GitHubRepositoryProvider))]
    [InlineData(typeof(GitLabRepositoryProvider))]
    public void Provider_implements_the_catalog_capability(Type providerType)
    {
        typeof(IPullRequestCatalogCapability).IsAssignableFrom(providerType)
            .ShouldBeTrue($"{providerType.Name} must implement IPullRequestCatalogCapability so IProviderRegistry.Require resolves it");
    }

    [Theory]
    [InlineData(typeof(GitHubRepositoryProvider))]
    [InlineData(typeof(GitLabRepositoryProvider))]
    public void Provider_declares_FindPullRequestByBranchAsync(Type providerType)
    {
        providerType.GetMethod(nameof(IPullRequestCatalogCapability.FindPullRequestByBranchAsync))
            .ShouldNotBeNull($"{providerType.Name} must implement FindPullRequestByBranchAsync — its own OpenPullRequestAsync's bind-or-create fallback depends on it directly");
    }
}
