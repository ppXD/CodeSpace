using CodeSpace.Core.Services.Providers.Capabilities;
using CodeSpace.Core.Services.Providers.GitHub;
using CodeSpace.Core.Services.Providers.GitLab;
using Shouldly;

namespace CodeSpace.UnitTests.Providers;

/// <summary>
/// Wiring pin: every repo provider must implement <see cref="IRepositoryAccessCapability"/> so the
/// pre-flight gate's <c>IProviderRegistry.Require&lt;IRepositoryAccessCapability&gt;</c> resolves it.
/// A new provider that forgets the interface would throw at click time, not compile time — pin it here.
/// </summary>
[Trait("Category", "Unit")]
public class RepositoryAccessCapabilityTests
{
    [Theory]
    [InlineData(typeof(GitHubRepositoryProvider))]
    [InlineData(typeof(GitLabRepositoryProvider))]
    public void Provider_implements_the_repository_access_capability(Type providerType)
    {
        typeof(IRepositoryAccessCapability).IsAssignableFrom(providerType)
            .ShouldBeTrue($"{providerType.Name} must implement IRepositoryAccessCapability so the pre-flight gate can resolve it");
    }

    // ── Write-scope gate (the read-only-token case): a token that can READ but lacks the write scope
    //    must be refused at pre-flight, not pass and 403 at write time. Null/empty scopes = unknown → allow.

    [Theory]
    [InlineData("read_api,read_user")]   // the reported case: Owner role, read-only token
    [InlineData("read_repository")]
    public void GitLab_denies_a_token_without_api_scope(string csv) =>
        GitLabRepositoryProvider.LacksApiScope(csv.Split(',')).ShouldBeTrue();

    [Theory]
    [InlineData("api")]
    [InlineData("api,read_user,write_repository")]
    public void GitLab_allows_a_token_with_api_scope(string csv) =>
        GitLabRepositoryProvider.LacksApiScope(csv.Split(',')).ShouldBeFalse();

    [Fact]
    public void GitLab_unknown_scopes_are_not_a_basis_to_deny()
    {
        GitLabRepositoryProvider.LacksApiScope(null).ShouldBeFalse();
        GitLabRepositoryProvider.LacksApiScope(System.Array.Empty<string>()).ShouldBeFalse();
    }

    [Theory]
    [InlineData("read:user")]
    [InlineData("read:org,gist")]
    public void GitHub_denies_a_token_without_repo_scope(string csv) =>
        GitHubRepositoryProvider.LacksReviewScope(csv.Split(',')).ShouldBeTrue();

    [Theory]
    [InlineData("repo")]
    [InlineData("public_repo,read:user")]
    public void GitHub_allows_a_token_with_repo_scope(string csv) =>
        GitHubRepositoryProvider.LacksReviewScope(csv.Split(',')).ShouldBeFalse();

    [Fact]
    public void GitHub_unknown_scopes_are_not_a_basis_to_deny()
    {
        GitHubRepositoryProvider.LacksReviewScope(null).ShouldBeFalse();
        GitHubRepositoryProvider.LacksReviewScope(System.Array.Empty<string>()).ShouldBeFalse();
    }
}
