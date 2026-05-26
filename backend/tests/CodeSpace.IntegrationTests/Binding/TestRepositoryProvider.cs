using CodeSpace.Core.Services.Providers;
using CodeSpace.Core.Services.Providers.Capabilities;
using CodeSpace.Messages.Dtos.Providers;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Events;

namespace CodeSpace.IntegrationTests.Binding;

/// <summary>
/// In-memory remote-hook store backing the <see cref="TestRepositoryProvider"/>. Registered
/// as a singleton in the test container so every fixture sees a fresh, isolated store — no
/// cross-test bleed through process-static fields.
///
/// <para>Records every call <c>RegisterWebhookAsync</c> makes and lets <c>FindWebhookByCallbackUrlAsync</c>
/// return the matching row. The registrar's idempotency check (find-by-URL → reuse instead of
/// re-register) flows through both methods, so test assertions like "registration ran exactly
/// once" can read <see cref="RegisterCallCount"/>.</para>
/// </summary>
public sealed class TestRemoteHookStore
{
    private readonly object _lock = new();
    private readonly Dictionary<string, RemoteWebhook> _byCallbackUrl = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Total times <c>RegisterWebhookAsync</c> created a fresh hook (not counting find-by-URL reuse).</summary>
    public int RegisterCallCount { get; private set; }

    public RemoteWebhook? Find(string callbackUrl)
    {
        lock (_lock)
        {
            return _byCallbackUrl.TryGetValue(callbackUrl, out var hook) ? hook : null;
        }
    }

    public RemoteWebhook Register(WebhookRegistration request)
    {
        var hook = new RemoteWebhook
        {
            ExternalId = $"test-hook-{Guid.NewGuid():N}",
            CallbackUrl = request.CallbackUrl,
            SubscribedEvents = request.SubscribedEvents.ToList(),
            Active = true
        };

        lock (_lock)
        {
            _byCallbackUrl[request.CallbackUrl] = hook;
            RegisterCallCount++;
        }

        return hook;
    }

    public void Reset()
    {
        lock (_lock)
        {
            _byCallbackUrl.Clear();
            RegisterCallCount = 0;
        }
    }
}

public sealed class TestRepositoryProvider : IRepositoryCatalogCapability, ICredentialProbeCapability, IWebhookRegistrationCapability, IWebhookSignatureVerifier, IWebhookEventNormalizer
{
    private readonly TestRemoteHookStore _hookStore;

    public TestRepositoryProvider(TestRemoteHookStore hookStore) { _hookStore = hookStore; }

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

    public Task<RemoteWebhook?> FindWebhookByCallbackUrlAsync(ProviderContext context, RemoteRepository repository, string callbackUrl, CancellationToken cancellationToken) =>
        Task.FromResult(_hookStore.Find(callbackUrl));

    public Task<RemoteWebhook> RegisterWebhookAsync(ProviderContext context, RemoteRepository repository, WebhookRegistration request, CancellationToken cancellationToken) =>
        Task.FromResult(_hookStore.Register(request));

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
