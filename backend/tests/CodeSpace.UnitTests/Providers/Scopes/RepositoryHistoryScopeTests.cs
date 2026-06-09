using CodeSpace.Core.Services.Providers.Capabilities;
using CodeSpace.Core.Services.Providers.GitHub;
using CodeSpace.Core.Services.Providers.GitLab;
using Shouldly;

namespace CodeSpace.UnitTests.Providers.Scopes;

/// <summary>
/// Pins <see cref="IRepositoryHistoryCapability"/> (latest + per-entry commits) to the repo-READ scope
/// family on both providers — same family as source browsing, so it adds no new OAuth consent.
/// </summary>
[Trait("Category", "Unit")]
public class RepositoryHistoryScopeTests
{
    [Fact]
    public void GitHub_requires_repo_or_public_repo_for_history()
    {
        var req = new GitHubProviderModule().CapabilityScopeRequirements[typeof(IRepositoryHistoryCapability)];

        req.IsSatisfied(new[] { "repo" }).ShouldBeTrue();
        req.IsSatisfied(new[] { "public_repo" }).ShouldBeTrue();
        req.IsSatisfied(new[] { "read:user" }).ShouldBeFalse();
    }

    [Fact]
    public void GitLab_requires_api_or_read_api_for_history()
    {
        var req = new GitLabProviderModule().CapabilityScopeRequirements[typeof(IRepositoryHistoryCapability)];

        req.IsSatisfied(new[] { "api" }).ShouldBeTrue();
        req.IsSatisfied(new[] { "read_api" }).ShouldBeTrue();
        req.IsSatisfied(new[] { "read_repository" }).ShouldBeFalse();
    }
}
