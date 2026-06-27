using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Workflows.Lifecycle;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace CodeSpace.Core.Services.Workflows.RunSources;

/// <summary>
/// Stages the <c>workflow_run_request</c> + <c>workflow_run</c> rows and emits
/// <c>run.queued</c>.
/// <para>NOTE — unlike the rest of the engine which expects the caller to own the
/// transaction boundary, this class issues a focused <c>SaveChangesAsync</c> so the
/// (source_type, external_event_id) unique-violation can be caught and turned into a
/// silent dedup-no-op return. Doing it any later (e.g. at the controller's outer
/// SaveChanges) means a duplicate webhook delivery surfaces an unhandled 500.</para>
/// </summary>
public sealed class RunStarter : IRunStarter, IScopedDependency
{
    private readonly CodeSpaceDbContext _db;
    private readonly IRunRecordLogger _recordLogger;
    private readonly ILogger<RunStarter> _logger;

    /// <summary>Postgres unique-constraint violation SQLSTATE. <see cref="DbUpdateException"/> wraps this when our duplicate-event index fires.</summary>
    private const string PostgresUniqueViolation = "23505";

    public RunStarter(CodeSpaceDbContext db, IRunRecordLogger recordLogger, ILogger<RunStarter> logger)
    {
        _db = db;
        _recordLogger = recordLogger;
        _logger = logger;
    }

    public async Task<Guid> StartAsync(RunSourceEnvelope envelope, CancellationToken cancellationToken)
    {
        Validate(envelope);

        var requestId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        // Every run flows through a Consumed-state request row first. The request is the
        // "we accepted this trigger" record; the run is the execution handle. Idempotency
        // fields (SourceInstanceId/ExternalEventId/IdempotencyKey) — when supplied —
        // are protected by partial unique indexes on workflow_run_request. A duplicate
        // delivery (provider re-sends the same X-GitHub-Delivery, the matcher fires twice)
        // raises 23505 below and we return Guid.Empty so the caller treats it as "already
        // accepted, drop on the floor".
        _db.WorkflowRunRequest.Add(new WorkflowRunRequest
        {
            Id = requestId,
            TeamId = envelope.TeamId,
            WorkflowId = envelope.WorkflowId,
            SourceType = envelope.SourceType,
            SourceInstanceId = envelope.SourceInstanceId,
            ExternalEventId = envelope.ExternalEventId,
            IdempotencyKey = envelope.IdempotencyKey,
            ActorType = envelope.ActorType,
            ActorId = envelope.ActorId,
            NormalizedPayloadJson = envelope.NormalizedPayloadJson,
            Status = WorkflowRunRequestStatus.Consumed,
            ReceivedAt = now,
            VerifiedAt = now,
            NormalizedAt = now,
            ActivationId = envelope.ActivationId,
            ActivationSnapshotJson = envelope.ActivationSnapshotJson,
            CausationId = envelope.CausationRequestId,
        });

        _db.WorkflowRun.Add(new WorkflowRun
        {
            Id = runId,
            WorkflowId = envelope.WorkflowId,
            WorkflowVersion = envelope.WorkflowVersion,
            TeamId = envelope.TeamId,
            RunRequestId = requestId,
            SourceType = envelope.SourceType,
            ActorId = envelope.ActorId,
            ReleaseHashAtRun = envelope.ReleaseHashAtRun ?? string.Empty,
            ParentRunId = envelope.ParentRunId,
            RootRunId = envelope.RootRunId,
            SessionId = envelope.Session?.SessionId,
            SessionTurnIndex = envelope.Session?.TurnIndex,
            Status = WorkflowRunStatus.Pending,
            CreatedBy = envelope.CreatedBy,
            LastModifiedBy = envelope.CreatedBy,
        });

        try
        {
            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            // Duplicate ingestion of the SAME upstream event (provider retried after
            // transient failure, or the matcher fired twice for the same delivery id).
            // The first run already exists; the second attempt is a no-op. Detach both
            // tracked-but-unsaved entities so the same DbContext isn't poisoned for the
            // caller's subsequent reads.
            //
            // EF's behaviour with `Added` entities that failed a unique-violation insert is
            // inconsistent across providers (Npgsql leaves them tracked as Added; SQL Server
            // detaches some). We defensively probe + remove only what's still tracked. The
            // older `.First(...)` form crashed with "Sequence contains no matching element"
            // when EF had already detached the entity for us.
            DetachIfTracked(_db.WorkflowRunRequest.Local, requestId, r => r.Id);
            DetachIfTracked(_db.WorkflowRun.Local, runId, r => r.Id);

            _logger.LogInformation(
                "RunStarter: deduplicated duplicate provider event. SourceType={SourceType} ExternalEventId={ExternalEventId} IdempotencyKey={IdempotencyKey} — silently no-op",
                envelope.SourceType, envelope.ExternalEventId, envelope.IdempotencyKey);

            return Guid.Empty;
        }

        // The caller commits the EF transaction, then calls
        // IWorkflowRunDispatcher.DispatchAsync(runId) which atomically transitions
        // Pending→Enqueued + hands to Hangfire. PostBoy-style: workflow_run.Status IS the
        // queue — no separate intent table needed.

        // Ledger entry that the run exists. Engine emits run.started when it picks up the
        // background-job; run.queued here is the "we accepted" marker.
        await _recordLogger.RunQueuedAsync(runId, envelope.SourceType, envelope.ActorId, cancellationToken).ConfigureAwait(false);

        return runId;
    }

    private static bool IsUniqueViolation(DbUpdateException ex) =>
        ex.InnerException is PostgresException pg && pg.SqlState == PostgresUniqueViolation;

    /// <summary>
    /// Defensive detach: removes <paramref name="id"/> from the local change tracker view
    /// only if EF still has it. Some EF/provider combinations auto-detach entities after a
    /// failed-insert SaveChanges; calling <c>Local.Remove</c> on a non-tracked instance is
    /// fine but locating it via <c>.First()</c> on an empty match throws — hence the probe.
    /// </summary>
    private static void DetachIfTracked<TEntity>(Microsoft.EntityFrameworkCore.ChangeTracking.LocalView<TEntity> local, Guid id, Func<TEntity, Guid> selectId)
        where TEntity : class
    {
        var match = local.FirstOrDefault(e => selectId(e) == id);
        if (match != null) local.Remove(match);
    }

    private static void Validate(RunSourceEnvelope envelope)
    {
        if (envelope.WorkflowVersion <= 0)
            throw new ArgumentException("WorkflowVersion must be > 0", nameof(envelope));

        if (string.IsNullOrWhiteSpace(envelope.SourceType))
            throw new ArgumentException("SourceType must be non-empty", nameof(envelope));

        if (string.IsNullOrWhiteSpace(envelope.ActorType))
            throw new ArgumentException("ActorType must be non-empty", nameof(envelope));

        if (string.IsNullOrWhiteSpace(envelope.NormalizedPayloadJson))
            throw new ArgumentException("NormalizedPayloadJson must be valid JSON (use \"{}\" for empty)", nameof(envelope));

        // Identity-shape sanity: user actors carry their id; webhook actors don't (the
        // sending provider isn't a CodeSpace identity). System actors are flexible (cron uses
        // SeederId, sub-workflow uses the parent run's actor) — we don't enforce there.
        switch (envelope.ActorType)
        {
            case WorkflowRunActorTypes.User when envelope.ActorId == null:
                throw new ArgumentException("ActorType=User requires a non-null ActorId", nameof(envelope));
            case WorkflowRunActorTypes.Webhook when envelope.ActorId != null:
                throw new ArgumentException("ActorType=Webhook requires a null ActorId (the provider isn't a CodeSpace identity)", nameof(envelope));
        }

        // Replay re-runs a FROZEN version, so its frozen-version fields travel together: a
        // CausationRequestId (lineage to the original request) iff a ReleaseHashAtRun (the hash the
        // engine verifies the version against). ParentRunId is the broader "spawning run" link — set
        // by replay AND by a sub-workflow child (which runs its own LATEST version, so it carries no
        // frozen hash) — so it is validated independently, not bundled with the replay fields.
        var hasCausation = envelope.CausationRequestId.HasValue;
        var hasReleaseHash = !string.IsNullOrEmpty(envelope.ReleaseHashAtRun);

        if (hasCausation != hasReleaseHash)
            throw new ArgumentException(
                "Replay's frozen-version fields must travel together: CausationRequestId iff ReleaseHashAtRun.",
                nameof(envelope));
    }
}
