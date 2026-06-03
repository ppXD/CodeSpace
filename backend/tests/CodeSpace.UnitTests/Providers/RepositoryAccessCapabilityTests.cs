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

}
