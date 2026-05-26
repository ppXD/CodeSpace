using Autofac;
using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace CodeSpace.Core.Services.Outbox;

/// <summary>
/// Outbox dispatcher with atomic claim/lease for multi-worker safety. A naive
/// <c>SELECT Pending → UPDATE Completed</c> race lets two API replicas both pick up the same
/// row and double-fire side-effecting handlers (POST a PR comment, send a Slack notification).
/// With the lease pattern below, exactly one worker ever processes a given row.
///
/// <para>Lifecycle: Pending → Claimed (atomic UPDATE) → Completed | back to Pending. A
/// separate <c>OutboxLeaseReaper</c> resets claims whose lease expires (the worker crashed)
/// so abandoned rows don't freeze forever.</para>
/// </summary>
public sealed class OutboxDispatcher : IOutboxDispatcher, IScopedDependency
{
    public const int MaxAttempts = 10;

    /// <summary>
    /// Default time a claimed row is held before the lease expires + the reaper resets it.
    /// 60s comfortably covers any single handler invocation (webhook register / engine run);
    /// shorter risks the reaper racing a slow-but-fine worker, longer delays recovery from
    /// real crashes. Override via <see cref="DrainOnceAsync"/> parameter for hot-path tests.
    /// </summary>
    public static readonly TimeSpan DefaultLeaseDuration = TimeSpan.FromSeconds(60);

    private readonly ILifetimeScope _scope;
    private readonly ILogger<OutboxDispatcher> _logger;

    /// <summary>
    /// Per-process worker identity. Diagnostic-only (the concurrency guarantee comes from
    /// SKIP LOCKED on the claim UPDATE) but invaluable for "which replica is stuck on row X"
    /// queries against the outbox table.
    /// </summary>
    public Guid WorkerId { get; } = Guid.NewGuid();

    public OutboxDispatcher(ILifetimeScope scope, ILogger<OutboxDispatcher> logger)
    {
        _scope = scope;
        _logger = logger;
    }

    public async Task<int> DrainOnceAsync(int batchSize, CancellationToken cancellationToken)
    {
        var claimedIds = await ClaimDueMessagesAsync(batchSize, DefaultLeaseDuration, cancellationToken).ConfigureAwait(false);

        foreach (var id in claimedIds) await ProcessOneAsync(id, cancellationToken).ConfigureAwait(false);

        return claimedIds.Count;
    }

    /// <summary>
    /// Atomic claim: flip up to <paramref name="batchSize"/> due-Pending rows to Claimed,
    /// stamp this worker's identity + lease deadline, return the claimed ids.
    ///
    /// <para>The SQL uses <c>FOR UPDATE SKIP LOCKED</c> inside the row-selection subquery so
    /// concurrent workers never queue on each other — each one gets its own disjoint slice.
    /// <c>RETURNING id</c> avoids a second round-trip to learn which rows we won.</para>
    /// </summary>
    private async Task<List<Guid>> ClaimDueMessagesAsync(int batchSize, TimeSpan leaseDuration, CancellationToken cancellationToken)
    {
        await using var probeScope = _scope.BeginLifetimeScope();
        var db = probeScope.Resolve<CodeSpaceDbContext>();
        var now = DateTimeOffset.UtcNow;
        var leaseUntil = now + leaseDuration;

        var conn = (NpgsqlConnection)db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open)
            await conn.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE outbox_message
            SET status      = 'Claimed',
                claimed_by  = @worker_id,
                claimed_at  = @now,
                lease_until = @lease_until
            WHERE id IN (
                SELECT id FROM outbox_message
                WHERE status = 'Pending' AND next_attempt_date <= @now
                ORDER BY created_date
                LIMIT @batch_size
                FOR UPDATE SKIP LOCKED
            )
            RETURNING id
            """;
        cmd.Parameters.Add(new NpgsqlParameter("worker_id", WorkerId));
        cmd.Parameters.Add(new NpgsqlParameter("now", now));
        cmd.Parameters.Add(new NpgsqlParameter("lease_until", leaseUntil));
        cmd.Parameters.Add(new NpgsqlParameter("batch_size", batchSize));

        var ids = new List<Guid>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            ids.Add(reader.GetGuid(0));

        if (ids.Count > 0) _logger.LogDebug("Worker {WorkerId} claimed {Count} outbox messages", WorkerId, ids.Count);
        return ids;
    }

    private async Task ProcessOneAsync(Guid messageId, CancellationToken cancellationToken)
    {
        string? errorMessage = null;

        try
        {
            await InvokeHandlerAsync(messageId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            errorMessage = ex.Message;
            _logger.LogWarning(ex, "Outbox handler for message {MessageId} threw — will record retry state", messageId);
        }

        await FinaliseClaimAsync(messageId, errorMessage, cancellationToken).ConfigureAwait(false);
    }

    private async Task InvokeHandlerAsync(Guid messageId, CancellationToken cancellationToken)
    {
        await using var handlerScope = _scope.BeginLifetimeScope();
        var db = handlerScope.Resolve<CodeSpaceDbContext>();
        var handlersByType = handlerScope.Resolve<IEnumerable<IOutboxMessageHandler>>().ToDictionary(h => h.MessageType);

        var message = await db.OutboxMessage.AsNoTracking().SingleOrDefaultAsync(m => m.Id == messageId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Outbox message {messageId} disappeared between scan and dispatch");

        if (!handlersByType.TryGetValue(message.MessageType, out var handler)) throw new InvalidOperationException($"No handler registered for outbox MessageType '{message.MessageType}'");

        await handler.HandleAsync(message, cancellationToken).ConfigureAwait(false);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Finalise a claimed row's fate: success → Completed + clear lease; failure → back to
    /// Pending with backoff (or DeadLettered if exhausted) + clear lease so the row is
    /// dispatchable again. Clearing the lease on EVERY path means a re-claim never sees a
    /// stale lease field set on a row that's actually Pending.
    /// </summary>
    private async Task FinaliseClaimAsync(Guid messageId, string? errorMessage, CancellationToken cancellationToken)
    {
        await using var statusScope = _scope.BeginLifetimeScope();
        var db = statusScope.Resolve<CodeSpaceDbContext>();

        var message = await db.OutboxMessage.SingleOrDefaultAsync(m => m.Id == messageId, cancellationToken).ConfigureAwait(false);

        if (message == null) return;

        message.LastAttemptedDate = DateTimeOffset.UtcNow;
        message.ClaimedBy = null;
        message.ClaimedAt = null;
        message.LeaseUntil = null;

        if (errorMessage == null) MarkCompleted(message);
        else MarkFailedAttempt(message, errorMessage);

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void MarkCompleted(Persistence.Entities.OutboxMessage message)
    {
        message.Status = OutboxStatus.Completed;
        message.LastError = null;
    }

    private void MarkFailedAttempt(Persistence.Entities.OutboxMessage message, string errorMessage)
    {
        message.Attempts++;
        message.LastError = errorMessage;

        if (message.Attempts >= MaxAttempts)
        {
            message.Status = OutboxStatus.DeadLettered;
            _logger.LogError("Outbox message {MessageId} dead-lettered after {Attempts} attempts: {Error}", message.Id, message.Attempts, errorMessage);
            return;
        }

        // Back to Pending — the row is dispatchable again after the backoff elapses. The claim
        // lease fields are already cleared by FinaliseClaimAsync above.
        message.Status = OutboxStatus.Pending;
        message.NextAttemptDate = DateTimeOffset.UtcNow + ComputeBackoff(message.Attempts);
    }

    /// <summary>Exponential backoff capped at 1 hour. Attempts 1→1m, 2→2m, 3→4m, …, 7+→1h.</summary>
    public static TimeSpan ComputeBackoff(int attempts)
    {
        var seconds = Math.Min(60d * Math.Pow(2, attempts - 1), 3600d);
        return TimeSpan.FromSeconds(seconds);
    }
}
