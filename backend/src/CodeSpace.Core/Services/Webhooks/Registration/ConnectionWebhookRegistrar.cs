using System.Text.Json;
using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Credentials;
using CodeSpace.Core.Services.Providers;
using CodeSpace.Core.Services.Providers.Capabilities;
using CodeSpace.Core.Services.Providers.Diagnostics;
using CodeSpace.Messages.Dtos.Providers;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Exceptions;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CodeSpace.Core.Services.Webhooks.Registration;

/// <summary>
/// Registers one group / organization hook at the provider. Deliberately shaped like
/// <see cref="RepositoryWebhookRegistrar"/> — same CAS steps, same backoff, same attempt timeline —
/// because the two are one lifecycle applied to two grains, and a reader of either should recognise
/// the other. What differs is only what the hook is addressed by: an owner path, not a repository.
///
/// <para><see cref="AutomaticRetryAttribute"/> Attempts=0 for the same reason: our own Failed →
/// Pending walk owns retry, and Hangfire's would stack on top of it.</para>
/// </summary>
[AutomaticRetry(Attempts = 0)]
public sealed class ConnectionWebhookRegistrar : IConnectionWebhookRegistrar, IScopedDependency
{
    /// <summary>Shared with <see cref="RepositoryWebhookRegistrar.MaxAttempts"/> — one lifecycle, one budget. Diverging would make the same failure dead-letter at different times depending on scope.</summary>
    public const int MaxAttempts = RepositoryWebhookRegistrar.MaxAttempts;

    private readonly CodeSpaceDbContext _db;
    private readonly IProviderRegistry _registry;
    private readonly IPayloadEncryptor _encryptor;
    private readonly ILogger<ConnectionWebhookRegistrar> _logger;

    public ConnectionWebhookRegistrar(CodeSpaceDbContext db, IProviderRegistry registry, IPayloadEncryptor encryptor, ILogger<ConnectionWebhookRegistrar> logger)
    {
        _db = db;
        _registry = registry;
        _encryptor = encryptor;
        _logger = logger;
    }

    public async Task RunAsync(Guid connectionWebhookId, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var transitioned = await _db.ConnectionWebhook
            .Where(w => w.Id == connectionWebhookId && w.RegistrationStatus == RepositoryWebhookRegistrationStatus.Enqueued)
            .ExecuteUpdateAsync(s => s
                .SetProperty(w => w.RegistrationStatus, RepositoryWebhookRegistrationStatus.Registering)
                .SetProperty(w => w.RegisteringAt, (DateTimeOffset?)now), cancellationToken)
            .ConfigureAwait(false);

        if (transitioned == 0)
        {
            _logger.LogDebug("ConnectionWebhookRegistrar: hook {ConnectionWebhookId} not in Enqueued state — skipping run", connectionWebhookId);
            return;
        }

        try
        {
            await PerformRegistrationAsync(connectionWebhookId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ConnectionWebhookRegistrar: registration failed for hook {ConnectionWebhookId}", connectionWebhookId);
            await RecordFailureAsync(connectionWebhookId, ex, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Load row, resolve provider, find-or-register on the owner, write external_id + flip to Registered.</summary>
    private async Task PerformRegistrationAsync(Guid connectionWebhookId, CancellationToken cancellationToken)
    {
        var webhook = await LoadWebhookAsync(connectionWebhookId, cancellationToken).ConfigureAwait(false);

        var providerContext = new ProviderContext(webhook.ProviderInstance, webhook.Credential);
        var capability = _registry.Require<IConnectionWebhookRegistrationCapability>(webhook.ProviderInstance.Provider);

        // Idempotency by callback URL, exactly as the repository path does it: a retry, or a
        // re-dispatch after a crash between the provider call and the DB write, must not leave a
        // second hook on the group. The callback path carries this row's own GUID, so the URL is
        // unique to this registration by construction.
        var existing = await capability.FindConnectionWebhookByCallbackUrlAsync(providerContext, webhook.OwnerPath, webhook.CallbackUrl, cancellationToken).ConfigureAwait(false);

        var registered = existing ?? await CreateRemoteHookAsync(capability, providerContext, webhook, cancellationToken).ConfigureAwait(false);

        if (existing != null)
            _logger.LogInformation("ConnectionWebhookRegistrar: existing remote hook found for {ConnectionWebhookId} on {OwnerPath} — reusing external id {ExternalId}", connectionWebhookId, webhook.OwnerPath, existing.ExternalId);

        await CompleteRegistrationAsync(connectionWebhookId, registered.ExternalId, cancellationToken).ConfigureAwait(false);
    }

    private async Task<RemoteWebhook> CreateRemoteHookAsync(IConnectionWebhookRegistrationCapability capability, ProviderContext providerContext, ConnectionWebhook webhook, CancellationToken cancellationToken)
    {
        var registration = new WebhookRegistration
        {
            CallbackUrl = webhook.CallbackUrl,
            Secret = _encryptor.Decrypt(webhook.SecretEnc),
            SubscribedEvents = webhook.SubscribedEvents
        };

        var created = await capability.RegisterConnectionWebhookAsync(providerContext, webhook.OwnerPath, registration, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation("ConnectionWebhookRegistrar: hook {ConnectionWebhookId} registered on {OwnerPath} — external id {ExternalId}", webhook.Id, webhook.OwnerPath, created.ExternalId);

        return created;
    }

    private async Task<ConnectionWebhook> LoadWebhookAsync(Guid connectionWebhookId, CancellationToken cancellationToken)
    {
        var webhook = await _db.ConnectionWebhook
            .AsNoTracking()
            .Include(w => w.ProviderInstance)
            .Include(w => w.Credential)
            .SingleOrDefaultAsync(w => w.Id == connectionWebhookId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"ConnectionWebhook {connectionWebhookId} disappeared between dispatch and run");

        if (webhook.ProviderInstance.DeletedDate != null)
            throw new InvalidOperationException($"Provider instance {webhook.ProviderInstanceId} was removed — cannot register its connection webhook");

        return webhook;
    }

    /// <summary>
    /// Atomic CAS Registering → Registered with external_id in the same UPDATE. The WHERE guards a
    /// teardown that raced us: a scope switch back to per-repository flips rows to Cancelled while a
    /// registration may still be in flight, and the Cancel wins.
    /// </summary>
    private async Task CompleteRegistrationAsync(Guid connectionWebhookId, string externalId, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var transitioned = await _db.ConnectionWebhook
            .Where(w => w.Id == connectionWebhookId && w.RegistrationStatus == RepositoryWebhookRegistrationStatus.Registering)
            .ExecuteUpdateAsync(s => s
                .SetProperty(w => w.RegistrationStatus, RepositoryWebhookRegistrationStatus.Registered)
                .SetProperty(w => w.ExternalId, (string?)externalId)
                .SetProperty(w => w.RegisteredAt, (DateTimeOffset?)now)
                .SetProperty(w => w.LastError, (string?)null), cancellationToken)
            .ConfigureAwait(false);

        if (transitioned == 0)
            _logger.LogWarning("ConnectionWebhookRegistrar: completion CAS failed for hook {ConnectionWebhookId} — row is no longer Registering (Cancelled or reconciled)", connectionWebhookId);
    }

    /// <summary>Record what happened, then walk the state machine forward — Failed with backoff, or DeadLettered once the attempts are spent.</summary>
    private async Task RecordFailureAsync(Guid connectionWebhookId, Exception exception, CancellationToken cancellationToken)
    {
        var attempts = await _db.ConnectionWebhook.AsNoTracking()
            .Where(w => w.Id == connectionWebhookId)
            .Select(w => w.Attempts)
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);

        var nextAttempts = attempts + 1;

        // Evidence first, for the reason its repository twin gives: a crash before the CAS costs a
        // transition, which is recoverable; a crash before the attempt row costs the only account of
        // why this failed, which is not.
        await RecordAttemptAsync(connectionWebhookId, nextAttempts, exception).ConfigureAwait(false);

        if (nextAttempts >= MaxAttempts)
        {
            await DeadLetterAsync(connectionWebhookId, nextAttempts, exception.Message).ConfigureAwait(false);
            return;
        }

        await ScheduleRetryAsync(connectionWebhookId, nextAttempts, exception.Message).ConfigureAwait(false);
    }

    /// <summary>
    /// Append this attempt to the hook's timeline. For a GitLab Free instance this is the row that
    /// carries the 403 and GitLab's own words about it, next to a <c>last_error</c> that names the
    /// plan — our sentence says what to do, this one is the evidence for it.
    /// </summary>
    private async Task RecordAttemptAsync(Guid connectionWebhookId, int attemptNumber, Exception exception)
    {
        // Down the chain rather than off the top: a plan refusal REPLACES the exception with one
        // that says what to do, and the evidence it says it about is the registration exception it
        // wraps. Reading only the outermost type would leave the attempt row blank for exactly the
        // failure the row exists to explain.
        var diagnostic = ProviderCallCapture.FindInChain<ProviderWebhookRegistrationException>(exception)?.Diagnostic;
        var request = diagnostic?.Request;

        _db.ConnectionWebhookAttempt.Add(new ConnectionWebhookAttempt
        {
            Id = Guid.NewGuid(),
            ConnectionWebhookId = connectionWebhookId,
            AttemptNumber = attemptNumber,
            AttemptedAt = DateTimeOffset.UtcNow,
            Error = exception.Message,
            StatusCode = diagnostic?.StatusCode,
            ResponseBody = diagnostic?.ResponseBody,
            RequestMethod = request?.Method,
            RequestUrl = request?.Url,
            RequestBody = request?.Body,
            RequestHeadersJson = request == null ? null : JsonSerializer.Serialize(request.Headers)
        });

        await _db.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>Terminal CAS. The WHERE guards against a teardown that flipped the row mid-call.</summary>
    private async Task DeadLetterAsync(Guid connectionWebhookId, int attempts, string errorMessage)
    {
        await _db.ConnectionWebhook
            .Where(w => w.Id == connectionWebhookId && w.RegistrationStatus == RepositoryWebhookRegistrationStatus.Registering)
            .ExecuteUpdateAsync(s => s
                .SetProperty(w => w.RegistrationStatus, RepositoryWebhookRegistrationStatus.DeadLettered)
                .SetProperty(w => w.Attempts, attempts)
                .SetProperty(w => w.LastError, (string?)errorMessage), CancellationToken.None)
            .ConfigureAwait(false);

        _logger.LogError("ConnectionWebhookRegistrar: hook {ConnectionWebhookId} dead-lettered after {Attempts} attempts: {Error}", connectionWebhookId, attempts, errorMessage);
    }

    /// <summary>Retryable CAS: Failed with next_attempt_at set, for a later dispatch to revive once the backoff elapses.</summary>
    private async Task ScheduleRetryAsync(Guid connectionWebhookId, int attempts, string errorMessage)
    {
        var nextAttemptAt = DateTimeOffset.UtcNow + RepositoryWebhookRegistrar.ComputeBackoff(attempts);

        await _db.ConnectionWebhook
            .Where(w => w.Id == connectionWebhookId && w.RegistrationStatus == RepositoryWebhookRegistrationStatus.Registering)
            .ExecuteUpdateAsync(s => s
                .SetProperty(w => w.RegistrationStatus, RepositoryWebhookRegistrationStatus.Failed)
                .SetProperty(w => w.Attempts, attempts)
                .SetProperty(w => w.LastError, (string?)errorMessage)
                .SetProperty(w => w.NextAttemptAt, nextAttemptAt), CancellationToken.None)
            .ConfigureAwait(false);
    }
}
