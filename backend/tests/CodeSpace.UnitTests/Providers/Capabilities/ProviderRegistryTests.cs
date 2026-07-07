using CodeSpace.Core.Services.Providers;
using CodeSpace.Core.Services.Providers.Capabilities;
using CodeSpace.Messages.Dtos.Providers;
using CodeSpace.Messages.Enums;
using Shouldly;

namespace CodeSpace.UnitTests.Providers.Capabilities;

[Trait("Category", "Unit")]
public class ProviderRegistryTests
{
    [Fact]
    public void Require_returns_capability_when_implemented()
    {
        var registry = BuildRegistry(new GitHubCatalogStub(), new GitHubProbeStub());

        var catalog = registry.Require<IRepositoryCatalogCapability>(ProviderKind.GitHub);

        catalog.ShouldNotBeNull();
        catalog.Kind.ShouldBe(ProviderKind.GitHub);
    }

    [Fact]
    public void Require_throws_NotSupported_when_capability_absent()
    {
        var registry = BuildRegistry(new GitHubProbeStub());

        Action act = () => registry.Require<IRepositoryCatalogCapability>(ProviderKind.GitHub);

        var ex = act.ShouldThrow<NotSupportedException>();
        ex.Message.ShouldContain("GitHub");
        ex.Message.ShouldContain(nameof(IRepositoryCatalogCapability));
    }

    [Fact]
    public void Require_throws_NotSupported_when_provider_kind_unregistered()
    {
        var registry = BuildRegistry(new GitHubProbeStub());

        Action act = () => registry.Require<ICredentialProbeCapability>(ProviderKind.GitLab);

        act.ShouldThrow<NotSupportedException>();
    }

    [Fact]
    public void TryGet_returns_true_with_instance_when_capability_present()
    {
        var registry = BuildRegistry(new GitHubCatalogStub());

        var ok = registry.TryGet<IRepositoryCatalogCapability>(ProviderKind.GitHub, out var cap);

        ok.ShouldBeTrue();
        cap.ShouldNotBeNull();
    }

    [Fact]
    public void TryGet_returns_false_with_null_when_capability_absent()
    {
        var registry = BuildRegistry(new GitHubProbeStub());

        var ok = registry.TryGet<IWebhookRegistrationCapability>(ProviderKind.GitHub, out var cap);

        ok.ShouldBeFalse();
        cap.ShouldBeNull();
    }

    [Fact]
    public void GetCapabilities_returns_all_capability_interfaces_for_kind()
    {
        var registry = BuildRegistry(new GitHubCatalogStub(), new GitHubProbeStub(), new GitHubWebhookStub());

        var capabilities = registry.GetCapabilities(ProviderKind.GitHub);

        capabilities.ShouldContain(typeof(IRepositoryCatalogCapability));
        capabilities.ShouldContain(typeof(ICredentialProbeCapability));
        capabilities.ShouldContain(typeof(IWebhookRegistrationCapability));
        capabilities.ShouldNotContain(typeof(IProviderCapability));
    }

    [Fact]
    public void GetCapabilities_returns_empty_for_unknown_kind()
    {
        var registry = BuildRegistry(new GitHubCatalogStub());

        var capabilities = registry.GetCapabilities(ProviderKind.GitLab);

        capabilities.ShouldBeEmpty();
    }

    [Fact]
    public void Capabilities_are_partitioned_by_kind()
    {
        var registry = BuildRegistry(new GitHubCatalogStub(), new GitLabCatalogStub());

        registry.Require<IRepositoryCatalogCapability>(ProviderKind.GitHub).Kind.ShouldBe(ProviderKind.GitHub);
        registry.Require<IRepositoryCatalogCapability>(ProviderKind.GitLab).Kind.ShouldBe(ProviderKind.GitLab);
    }

    private static IProviderRegistry BuildRegistry(params IProviderCapability[] capabilities) => new ProviderRegistry(capabilities);

    private sealed class GitHubCatalogStub : IRepositoryCatalogCapability
    {
        public ProviderKind Kind => ProviderKind.GitHub;
        public Task<RemoteRepository> GetByExternalIdAsync(ProviderContext context, string externalId, CancellationToken cancellationToken) => throw new NotImplementedException();
        public Task<RemoteRepository> ResolveByPathAsync(ProviderContext context, string namespacePath, string name, CancellationToken cancellationToken) => throw new NotImplementedException();
        public Task<RemoteRepositoryPage> PagedListAccessibleAsync(ProviderContext context, string? search, int page, int perPage, CancellationToken cancellationToken) => throw new NotImplementedException();
    }

    private sealed class GitHubProbeStub : ICredentialProbeCapability
    {
        public ProviderKind Kind => ProviderKind.GitHub;
        public Task<CredentialProbeResult> ProbeCredentialAsync(ProviderContext context, CancellationToken cancellationToken) => throw new NotImplementedException();
    }

    private sealed class GitHubWebhookStub : IWebhookRegistrationCapability
    {
        public ProviderKind Kind => ProviderKind.GitHub;
        public Task<RemoteWebhook?> FindWebhookByCallbackUrlAsync(ProviderContext context, RemoteRepository repository, string callbackUrl, CancellationToken cancellationToken) => throw new NotImplementedException();
        public Task<RemoteWebhook> RegisterWebhookAsync(ProviderContext context, RemoteRepository repository, WebhookRegistration request, CancellationToken cancellationToken) => throw new NotImplementedException();
        public Task DeleteWebhookAsync(ProviderContext context, RemoteRepository repository, string externalWebhookId, CancellationToken cancellationToken) => throw new NotImplementedException();
    }

    private sealed class GitLabCatalogStub : IRepositoryCatalogCapability
    {
        public ProviderKind Kind => ProviderKind.GitLab;
        public Task<RemoteRepository> GetByExternalIdAsync(ProviderContext context, string externalId, CancellationToken cancellationToken) => throw new NotImplementedException();
        public Task<RemoteRepository> ResolveByPathAsync(ProviderContext context, string namespacePath, string name, CancellationToken cancellationToken) => throw new NotImplementedException();
        public Task<RemoteRepositoryPage> PagedListAccessibleAsync(ProviderContext context, string? search, int page, int perPage, CancellationToken cancellationToken) => throw new NotImplementedException();
    }
}
