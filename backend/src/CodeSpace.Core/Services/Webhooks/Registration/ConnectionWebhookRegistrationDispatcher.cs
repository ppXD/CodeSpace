using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Services.Jobs;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CodeSpace.Core.Services.Webhooks.Registration;

/// <summary>
/// Connection-scoped twin of <see cref="RepositoryWebhookRegistrationDispatcher"/>. Same CAS, same
/// revert-on-throw, against <c>connection_webhook</c>.
/// </summary>
public sealed class ConnectionWebhookRegistrationDispatcher : IConnectionWebhookRegistrationDispatcher, IScopedDependency
{
    private readonly CodeSpaceDbContext _db;
    private readonly ICodeSpaceBackgroundJobClient _backgroundJobClient;
    private readonly ILogger<ConnectionWebhookRegistrationDispatcher> _logger;

    public ConnectionWebhookRegistrationDispatcher(CodeSpaceDbContext db, ICodeSpaceBackgroundJobClient backgroundJobClient, ILogger<ConnectionWebhookRegistrationDispatcher> logger)
    {
        _db = db;
        _backgroundJobClient = backgroundJobClient;
        _logger = logger;
    }

    public async Task<bool> DispatchAsync(Guid connectionWebhookId, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;

        // Atomic CAS — Pending → Enqueued. Two callers racing the same row (a bind and a second
        // bind under the same group, arriving together) cannot both win: Postgres answers
        // rows-affected 1 to one and 0 to the other.
        var transitioned = await _db.ConnectionWebhook
            .Where(w => w.Id == connectionWebhookId && w.RegistrationStatus == RepositoryWebhookRegistrationStatus.Pending)
            .ExecuteUpdateAsync(s => s
                .SetProperty(w => w.RegistrationStatus, RepositoryWebhookRegistrationStatus.Enqueued)
                .SetProperty(w => w.EnqueuedAt, (DateTimeOffset?)now), cancellationToken)
            .ConfigureAwait(false);

        if (transitioned == 0)
        {
            _logger.LogDebug("ConnectionWebhookRegistrationDispatcher: hook {ConnectionWebhookId} not in Pending state — skipping dispatch", connectionWebhookId);
            return false;
        }

        try
        {
            var jobId = _backgroundJobClient.Enqueue<IConnectionWebhookRegistrar>(r => r.RunAsync(connectionWebhookId, CancellationToken.None));
            _logger.LogInformation("ConnectionWebhookRegistrationDispatcher: hook {ConnectionWebhookId} enqueued as background job {JobId}", connectionWebhookId, jobId);
            return true;
        }
        catch (Exception ex)
        {
            // Walk the row back so it is dispatchable again. CancellationToken.None because a
            // cancelled caller must not be the reason a row is stranded in Enqueued.
            _logger.LogWarning(ex, "ConnectionWebhookRegistrationDispatcher: enqueue failed for hook {ConnectionWebhookId}; reverting to Pending", connectionWebhookId);

            await _db.ConnectionWebhook
                .Where(w => w.Id == connectionWebhookId && w.RegistrationStatus == RepositoryWebhookRegistrationStatus.Enqueued)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(w => w.RegistrationStatus, RepositoryWebhookRegistrationStatus.Pending)
                    .SetProperty(w => w.EnqueuedAt, (DateTimeOffset?)null), CancellationToken.None)
                .ConfigureAwait(false);

            throw;
        }
    }
}
