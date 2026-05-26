using CodeSpace.Core.Services.Providers;
using CodeSpace.Core.Services.Providers.Capabilities;
using CodeSpace.Messages.Dtos.Providers;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Events;

namespace CodeSpace.IntegrationTests.Binding;

public sealed class TestRepositoryProvider : IRepositoryCatalogCapability, ICredentialProbeCapability, IWebhookRegistrationCapability, IWebhookSignatureVerifier, IWebhookEventNormalizer
{
    public ProviderKind Kind => ProviderKind.Git;

    public Task<RemoteRepository> GetByExternalIdAsync(ProviderContext context, string externalId, CancellationToken cancellationToken) => Task.FromResult(BuildRemoteRepo(externalId, externalId));

    public Task<RemoteRepository> ResolveByPathAsync(ProviderContext context, string namespacePath, string name, CancellationToken cancellationToken) => Task.FromResult(BuildRemoteRepo($"id-{namespacePath}-{name}", $"{namespacePath}/{name}"));

    public Task<RemoteRepositoryPage> PagedListAccessibleAsync(ProviderContext context, string? search, int page, int perPage, CancellationToken cancellationToken)
    {
        // Three deterministic fixtures, optional search-by-substring + slice into one page.
        IEnumerable<RemoteRepository> all = new[]
        {
            BuildRemoteRepo("id-acme-api", "acme/api"),
            BuildRemoteRepo("id-acme-web", "acme/web"),
            BuildRemoteRepo("id-acme-cli", "acme/cli"),
        };
        if (!string.IsNullOrWhiteSpace(search)) all = all.Where(r => r.FullPath.Contains(search!, StringComparison.OrdinalIgnoreCase));

        var items = all.Skip((page - 1) * perPage).Take(perPage).ToList();
        return Task.FromResult(new RemoteRepositoryPage { Items = items });
    }

    public Task<CredentialProbeResult> ProbeCredentialAsync(ProviderContext context, CancellationToken cancellationToken) => Task.FromResult(new CredentialProbeResult
    {
        IsValid = true,
        AuthenticatedUserExternalId = "test-user-id",
        AuthenticatedUserName = "Test User"
    });

    public Task<RemoteWebhook> RegisterWebhookAsync(ProviderContext context, RemoteRepository repository, WebhookRegistration request, CancellationToken cancellationToken) => Task.FromResult(new RemoteWebhook
    {
        ExternalId = $"test-hook-{Guid.NewGuid():N}",
        CallbackUrl = request.CallbackUrl,
        SubscribedEvents = request.SubscribedEvents.ToList(),
        Active = true
    });

    public Task DeleteWebhookAsync(ProviderContext context, RemoteRepository repository, string externalWebhookId, CancellationToken cancellationToken) => Task.CompletedTask;

    public bool VerifySignature(string body, IReadOnlyDictionary<string, string> headers, string secret) => true;

    public NormalizedEvent? Normalize(Guid repositoryId, string body, IReadOnlyDictionary<string, string> headers) => null;

    private static RemoteRepository BuildRemoteRepo(string externalId, string fullPath) => new()
    {
        ExternalId = externalId,
        NamespacePath = fullPath.Contains('/') ? fullPath.Substring(0, fullPath.LastIndexOf('/')) : "test-ns",
        Name = fullPath.Contains('/') ? fullPath.Substring(fullPath.LastIndexOf('/') + 1) : fullPath,
        FullPath = fullPath,
        DefaultBranch = "main",
        Visibility = RepositoryVisibility.Private,
        WebUrl = $"https://test.local/{fullPath}"
    };
}
