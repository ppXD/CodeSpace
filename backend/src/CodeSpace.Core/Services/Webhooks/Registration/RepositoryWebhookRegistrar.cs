using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Credentials;
using CodeSpace.Core.Services.Providers;
using CodeSpace.Core.Services.Providers.Capabilities;
using CodeSpace.Messages.Dtos.Providers;
using CodeSpace.Messages.Enums;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CodeSpace.Core.Services.Webhooks.Registration;

/// <summary>
/// Hangfire-invoked worker that performs the actual provider-side webhook registration.
/// <see cref="AutomaticRetryAttribute"/> Attempts=0 — our own state machine (Failed →
/// Pending with backoff via the reconciler) owns retry semantics; we don't want Hangfire's
/// own retry stacking on top.
/// </summary>
[AutomaticRetry(Attempts = 0)]
public sealed class RepositoryWebhookRegistrar : IRepositoryWebhookRegistrar, IScopedDependency
{
    /// <summary>
    /// Maximum number of times the worker will retry a failing registration before flipping
    /// to <see cref="RepositoryWebhookRegistrationStatus.DeadLettered"/>. 10 attempts with
    /// exponential backoff (1m, 2m, 4m, …, 1h cap) gives roughly 4 hours of recovery window
    /// before an operator needs to look. Beyond that, the credential is almost certainly
    /// revoked or the remote provider is permanently down for this repo.
    /// </summary>
    public const int MaxAttempts = 10;

    private readonly CodeSpaceDbContext _db;
    private readonly IProviderRegistry _registry;
    private readonly IPayloadEncryptor _encryptor;
    private readonly ILogger<RepositoryWebhookRegistrar> _logger;

    public RepositoryWebhookRegistrar(CodeSpaceDbContext db, IProviderRegistry registry, IPayloadEncryptor encryptor, ILogger<RepositoryWebhookRegistrar> logger)
    {
        _db = db;
        _registry = registry;
        _encryptor = encryptor;
        _logger = logger;
    }

    public async Task RunAsync(Guid webhookId, CancellationToken cancellationToken)
    {
        // Atomic CAS — Enqueued → Registering. The WHERE clause is the single-worker
        // guarantee against duplicate Hangfire executions: two workers picking up the same
        // job id race on UPDATE WHERE registration_status = 'Enqueued', one wins, one loses.
        // The loser sees rows-affected = 0 and silently returns. CombinedWithProviderIdempotency,
        // even a triple-fire ends with at most one remote hook.
        var now = DateTimeOffset.UtcNow;
        var transitioned = await _db.RepositoryWebhook
            .Where(w => w.Id == webhookId && w.RegistrationStatus == RepositoryWebhookRegistrationStatus.Enqueued)
            .ExecuteUpdateAsync(s => s
                .SetProperty(w => w.RegistrationStatus, RepositoryWebhookRegistrationStatus.Registering)
                .SetProperty(w => w.RegisteringAt, (DateTimeOffset?)now), cancellationToken)
            .ConfigureAwait(false);

        if (transitioned == 0)
        {
            _logger.LogDebug("RepositoryWebhookRegistrar: webhook {WebhookId} not in Enqueued state — skipping run", webhookId);
            return;
        }

        try
        {
            await PerformRegistrationAsync(webhookId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "RepositoryWebhookRegistrar: registration failed for webhook {WebhookId}", webhookId);
            await RecordFailureAsync(webhookId, ex.Message, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Happy path: load row, resolve provider, decrypt secret, find-or-register on the
    /// remote, atomically write external_id + flip to Registered.
    /// </summary>
    private async Task PerformRegistrationAsync(Guid webhookId, CancellationToken cancellationToken)
    {
        var webhook = await LoadWebhookAsync(webhookId, cancellationToken).ConfigureAwait(false);
        var repository = await LoadRepositoryAsync(webhook.RepositoryId, cancellationToken).ConfigureAwait(false);

        var providerContext = new ProviderContext(repository.ProviderInstance, repository.Credential!);
        var capability = _registry.Require<IWebhookRegistrationCapability>(repository.ProviderInstance.Provider);
        var catalog = _registry.Require<IRepositoryCatalogCapability>(repository.ProviderInstance.Provider);

        var remote = await catalog.GetByExternalIdAsync(providerContext, repository.ExternalId, cancellationToken).ConfigureAwait(false);

        // Idempotency — if a prior attempt of THIS registration already landed at the provider
        // (Hangfire double-fire, reconciler re-dispatch after a crash between provider call +
        // DB write, etc.) we MUST NOT create a second remote hook. Look up by callback URL —
        // unique per (callback URL, repository) by construction (BindAsync generates a fresh
        // GUID-based callback path for every webhook).
        var existing = await capability.FindWebhookByCallbackUrlAsync(providerContext, remote, webhook.CallbackUrl, cancellationToken).ConfigureAwait(false);

        RemoteWebhook registered;
        if (existing != null)
        {
            _logger.LogInformation("RepositoryWebhookRegistrar: existing remote hook found for webhook {WebhookId} at {CallbackUrl} — reusing external id {ExternalId}", webhookId, webhook.CallbackUrl, existing.ExternalId);
            registered = existing;
        }
        else
        {
            var secret = _encryptor.Decrypt(webhook.SecretEnc);
            var registration = new WebhookRegistration
            {
                CallbackUrl = webhook.CallbackUrl,
                Secret = secret,
                SubscribedEvents = webhook.SubscribedEvents
            };
            registered = await capability.RegisterWebhookAsync(providerContext, remote, registration, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("RepositoryWebhookRegistrar: webhook {WebhookId} registered at provider — external id {ExternalId}", webhookId, registered.ExternalId);
        }

        await CompleteRegistrationAsync(webhookId, registered.ExternalId, cancellationToken).ConfigureAwait(false);
    }

    private async Task<RepositoryWebhook> LoadWebhookAsync(Guid webhookId, CancellationToken cancellationToken)
    {
        return await _db.RepositoryWebhook
            .AsNoTracking()
            .SingleOrDefaultAsync(w => w.Id == webhookId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"RepositoryWebhook {webhookId} disappeared between dispatch and run");
    }

    private async Task<Repository> LoadRepositoryAsync(Guid repositoryId, CancellationToken cancellationToken)
    {
        var repo = await _db.Repository
            .Include(r => r.ProviderInstance)
            .Include(r => r.Credential)
            .SingleOrDefaultAsync(r => r.Id == repositoryId && r.DeletedDate == null, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Repository {repositoryId} not found or deleted — cannot register webhook");

        if (repo.Credential == null) throw new InvalidOperationException($"Repository {repositoryId} has no credential bound — cannot register webhook");

        return repo;
    }

    /// <summary>
    /// Atomic CAS Registering → Registered with external_id + registered_at in the same UPDATE.
    /// The WHERE clause guards against a Cancel that raced us (unbind during in-flight
    /// registration flips the row to Cancelled — the Cancel wins, we no-op).
    /// </summary>
    private async Task CompleteRegistrationAsync(Guid webhookId, string externalId, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var transitioned = await _db.RepositoryWebhook
            .Where(w => w.Id == webhookId && w.RegistrationStatus == RepositoryWebhookRegistrationStatus.Registering)
            .ExecuteUpdateAsync(s => s
                .SetProperty(w => w.RegistrationStatus, RepositoryWebhookRegistrationStatus.Registered)
                .SetProperty(w => w.ExternalId, (string?)externalId)
                .SetProperty(w => w.RegisteredAt, (DateTimeOffset?)now)
                .SetProperty(w => w.LastError, (string?)null), cancellationToken)
            .ConfigureAwait(false);

        if (transitioned == 0)
        {
            _logger.LogWarning("RepositoryWebhookRegistrar: completion CAS failed for webhook {WebhookId} — row is no longer in Registering state (Cancelled or stuck-reconciled)", webhookId);
        }
    }

    /// <summary>
    /// Failure-path CAS: bump attempts, write error, branch to Failed (with backoff) or
    /// DeadLettered. The WHERE guards against a Cancel that flipped the row while the
    /// provider call was in flight.
    /// </summary>
    private async Task RecordFailureAsync(Guid webhookId, string errorMessage, CancellationToken cancellationToken)
    {
        // Read attempts under the same scope so backoff is computed against the post-increment
        // value. Race-safe because we filter the UPDATE on the row + the Registering state;
        // even if a reconciler revives a stuck row between the read and the UPDATE, the
        // attempt count is bounded above by MaxAttempts (one stray over-count → DeadLetter
        // one attempt early, never a missed DeadLetter).
        var attempts = await _db.RepositoryWebhook.AsNoTracking()
            .Where(w => w.Id == webhookId)
            .Select(w => w.Attempts)
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);

        var nextAttempts = attempts + 1;

        if (nextAttempts >= MaxAttempts)
        {
            await _db.RepositoryWebhook
                .Where(w => w.Id == webhookId && w.RegistrationStatus == RepositoryWebhookRegistrationStatus.Registering)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(w => w.RegistrationStatus, RepositoryWebhookRegistrationStatus.DeadLettered)
                    .SetProperty(w => w.Attempts, nextAttempts)
                    .SetProperty(w => w.LastError, (string?)errorMessage), CancellationToken.None)
                .ConfigureAwait(false);

            _logger.LogError("RepositoryWebhookRegistrar: webhook {WebhookId} dead-lettered after {Attempts} attempts: {Error}", webhookId, nextAttempts, errorMessage);
            return;
        }

        var nextAttemptAt = DateTimeOffset.UtcNow + ComputeBackoff(nextAttempts);
        await _db.RepositoryWebhook
            .Where(w => w.Id == webhookId && w.RegistrationStatus == RepositoryWebhookRegistrationStatus.Registering)
            .ExecuteUpdateAsync(s => s
                .SetProperty(w => w.RegistrationStatus, RepositoryWebhookRegistrationStatus.Failed)
                .SetProperty(w => w.Attempts, nextAttempts)
                .SetProperty(w => w.LastError, (string?)errorMessage)
                .SetProperty(w => w.NextAttemptAt, nextAttemptAt), CancellationToken.None)
            .ConfigureAwait(false);
    }

    /// <summary>Exponential backoff capped at 1 hour. Attempts 1→1m, 2→2m, 3→4m, …, 7+→1h.</summary>
    public static TimeSpan ComputeBackoff(int attempts)
    {
        var seconds = Math.Min(60d * Math.Pow(2, attempts - 1), 3600d);
        return TimeSpan.FromSeconds(seconds);
    }
}
