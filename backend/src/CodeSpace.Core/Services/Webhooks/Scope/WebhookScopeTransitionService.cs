using System.Security.Cryptography;
using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Credentials;
using CodeSpace.Core.Services.Providers;
using CodeSpace.Core.Services.Providers.Capabilities;
using CodeSpace.Core.Services.Providers.Events;
using CodeSpace.Core.Services.Webhooks.Registration;
using CodeSpace.Core.Settings.Webhooks;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CodeSpace.Core.Services.Webhooks.Scope;

/// <summary>
/// Retire, then register. See <see cref="IWebhookScopeTransitionService"/> for why that order and
/// not the other one.
/// </summary>
public sealed class WebhookScopeTransitionService : IWebhookScopeTransitionService, IScopedDependency
{
    private readonly CodeSpaceDbContext _db;
    private readonly IProviderRegistry _registry;
    private readonly IProviderEventSubscriptionRegistry _subscriptionRegistry;
    private readonly IConnectionWebhookProvisioner _connectionProvisioner;
    private readonly IConnectionWebhookRegistrationDispatcher _connectionDispatcher;
    private readonly IRepositoryWebhookRegistrationDispatcher _repositoryDispatcher;
    private readonly IPayloadEncryptor _encryptor;
    private readonly WebhookBaseUrlSetting _webhookBaseUrl;
    private readonly ILogger<WebhookScopeTransitionService> _logger;

    public WebhookScopeTransitionService(CodeSpaceDbContext db, IProviderRegistry registry, IProviderEventSubscriptionRegistry subscriptionRegistry, IConnectionWebhookProvisioner connectionProvisioner, IConnectionWebhookRegistrationDispatcher connectionDispatcher, IRepositoryWebhookRegistrationDispatcher repositoryDispatcher, IPayloadEncryptor encryptor, WebhookBaseUrlSetting webhookBaseUrl, ILogger<WebhookScopeTransitionService> logger)
    {
        _db = db;
        _registry = registry;
        _subscriptionRegistry = subscriptionRegistry;
        _connectionProvisioner = connectionProvisioner;
        _connectionDispatcher = connectionDispatcher;
        _repositoryDispatcher = repositoryDispatcher;
        _encryptor = encryptor;
        _webhookBaseUrl = webhookBaseUrl;
        _logger = logger;
    }

    public async Task ApplyAsync(Guid providerInstanceId, ProviderWebhookScope previousScope, CancellationToken cancellationToken)
    {
        var instance = await LoadInstanceAsync(providerInstanceId, cancellationToken).ConfigureAwait(false);

        if (instance.WebhookScope == previousScope) return;

        var repositories = await LoadBoundRepositoriesAsync(providerInstanceId, cancellationToken).ConfigureAwait(false);

        if (instance.WebhookScope == ProviderWebhookScope.Connection)
        {
            await MoveToConnectionScopeAsync(instance, repositories, cancellationToken).ConfigureAwait(false);
            return;
        }

        await MoveToRepositoryScopeAsync(instance, repositories, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Per-repository hooks stop first, then one hook per distinct owner takes over. The owners come
    /// from the repositories actually bound, so a connection spanning three groups gets three hooks
    /// and no more — a hook on a group holding nothing we track would deliver only traffic we drop.
    /// </summary>
    private async Task MoveToConnectionScopeAsync(ProviderInstance instance, List<Repository> repositories, CancellationToken cancellationToken)
    {
        var retired = await RetireRepositoryWebhooksAsync(instance, repositories, cancellationToken).ConfigureAwait(false);

        var dispatched = 0;
        foreach (var owner in repositories.Select(r => r.NamespacePath).Distinct(StringComparer.Ordinal))
        {
            var credentialId = repositories.First(r => r.NamespacePath == owner).CredentialId;

            if (credentialId == null) continue;

            var hookId = await _connectionProvisioner.EnsureForOwnerAsync(instance, credentialId.Value, owner, cancellationToken).ConfigureAwait(false);

            if (hookId == null) continue;

            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await _connectionDispatcher.DispatchAsync(hookId.Value, cancellationToken).ConfigureAwait(false);
            dispatched++;
        }

        _logger.LogInformation("WebhookScopeTransition: connection {ProviderInstanceId} moved to connection-wide scope — {Retired} repository hooks retired, {Dispatched} owner hooks registering", instance.Id, retired, dispatched);
    }

    /// <summary>
    /// The group hooks stop first, then every bound repository gets its own — which is the state a
    /// connection that had never left per-repository scope would be in.
    /// </summary>
    private async Task MoveToRepositoryScopeAsync(ProviderInstance instance, List<Repository> repositories, CancellationToken cancellationToken)
    {
        var retired = await _connectionProvisioner.RetireAllAsync(instance, cancellationToken).ConfigureAwait(false);
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        var dispatched = 0;
        foreach (var repository in repositories.Where(r => r.CredentialId != null))
        {
            if (await HasLiveRepositoryWebhookAsync(repository.Id, cancellationToken).ConfigureAwait(false)) continue;

            var webhookId = StagePendingRepositoryWebhook(instance, repository.Id);
            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await _repositoryDispatcher.DispatchAsync(webhookId, cancellationToken).ConfigureAwait(false);
            dispatched++;
        }

        _logger.LogInformation("WebhookScopeTransition: connection {ProviderInstanceId} moved to per-repository scope — {Retired} connection hooks retired, {Dispatched} repository hooks registering", instance.Id, retired, dispatched);
    }

    /// <summary>
    /// Take every per-repository hook out of service: delete it at the provider best-effort, drop the
    /// Registered rows (they described a hook that no longer exists), and CAS the rest to Cancelled
    /// so a registration still in flight cannot complete behind the switch.
    /// </summary>
    private async Task<int> RetireRepositoryWebhooksAsync(ProviderInstance instance, List<Repository> repositories, CancellationToken cancellationToken)
    {
        var repositoryIds = repositories.Select(r => r.Id).ToList();

        var hooks = await _db.RepositoryWebhook
            .Where(w => repositoryIds.Contains(w.RepositoryId) && WebhookRegistrationLifecycle.InService.Contains(w.RegistrationStatus))
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        if (hooks.Count == 0) return 0;

        await BestEffortDeleteRemoteAsync(instance, repositories, hooks, cancellationToken).ConfigureAwait(false);

        _db.RepositoryWebhook.RemoveRange(hooks.Where(h => h.RegistrationStatus == RepositoryWebhookRegistrationStatus.Registered));
        await CancelNonTerminalRepositoryWebhooksAsync(repositoryIds, cancellationToken).ConfigureAwait(false);
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return hooks.Count;
    }

    private async Task BestEffortDeleteRemoteAsync(ProviderInstance instance, List<Repository> repositories, List<RepositoryWebhook> hooks, CancellationToken cancellationToken)
    {
        if (!_registry.TryGet<IWebhookRegistrationCapability>(instance.Provider, out var capability)) return;
        if (!_registry.TryGet<IRepositoryCatalogCapability>(instance.Provider, out var catalog)) return;

        var byId = repositories.ToDictionary(r => r.Id);

        foreach (var hook in hooks.Where(h => !string.IsNullOrEmpty(h.ExternalId)))
        {
            if (!byId.TryGetValue(hook.RepositoryId, out var repository) || repository.Credential == null) continue;

            try
            {
                var context = new ProviderContext(instance, repository.Credential);
                var remote = await catalog!.GetByExternalIdAsync(context, repository.ExternalId, cancellationToken).ConfigureAwait(false);
                await capability!.DeleteWebhookAsync(context, remote, hook.ExternalId!, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // A hook we cannot delete is one the operator has to remove by hand. Worth a warning,
                // not worth stopping the switch — the local row goes either way, because a connection
                // that claims a mode it is not in is the more misleading state.
                _logger.LogWarning(ex, "Failed to delete remote repository webhook {WebhookId} during scope switch; removing the local record anyway", hook.Id);
            }
        }
    }

    private async Task CancelNonTerminalRepositoryWebhooksAsync(List<Guid> repositoryIds, CancellationToken cancellationToken)
    {

        await _db.RepositoryWebhook
            .Where(w => repositoryIds.Contains(w.RepositoryId) && WebhookRegistrationLifecycle.RetirableToCancelled.Contains(w.RegistrationStatus))
            .ExecuteUpdateAsync(s => s.SetProperty(w => w.RegistrationStatus, RepositoryWebhookRegistrationStatus.Cancelled), cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<bool> HasLiveRepositoryWebhookAsync(Guid repositoryId, CancellationToken cancellationToken) =>
        await _db.RepositoryWebhook.AsNoTracking()
            .AnyAsync(w => w.RepositoryId == repositoryId && WebhookRegistrationLifecycle.InService.Contains(w.RegistrationStatus), cancellationToken)
            .ConfigureAwait(false);

    /// <summary>Same durable intent a bind stages — encrypted secret, committed callback URL, Pending.</summary>
    private Guid StagePendingRepositoryWebhook(ProviderInstance instance, Guid repositoryId)
    {
        var id = Guid.NewGuid();
        var secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

        _db.RepositoryWebhook.Add(new RepositoryWebhook
        {
            Id = id,
            RepositoryId = repositoryId,
            ExternalId = null,
            CallbackUrl = $"{_webhookBaseUrl.Value.TrimEnd('/')}/api/webhooks/{id}",
            SecretEnc = _encryptor.Encrypt(secret),
            SubscribedEvents = _subscriptionRegistry.GetSubscribedRawEvents(instance.Provider).ToList(),
            Active = true,
            RegistrationStatus = RepositoryWebhookRegistrationStatus.Pending,
            NextAttemptAt = DateTimeOffset.UtcNow
        });

        return id;
    }

    private async Task<ProviderInstance> LoadInstanceAsync(Guid providerInstanceId, CancellationToken cancellationToken)
    {
        return await _db.ProviderInstance.AsNoTracking()
            .SingleOrDefaultAsync(p => p.Id == providerInstanceId && p.DeletedDate == null, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Provider instance {providerInstanceId} not found — cannot apply a webhook scope change");
    }

    private async Task<List<Repository>> LoadBoundRepositoriesAsync(Guid providerInstanceId, CancellationToken cancellationToken)
    {
        return await _db.Repository
            .Include(r => r.Credential)
            .Where(r => r.ProviderInstanceId == providerInstanceId && r.DeletedDate == null)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
    }
}
