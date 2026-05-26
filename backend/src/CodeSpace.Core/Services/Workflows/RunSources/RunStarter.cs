using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Workflows.Lifecycle;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Enums;

namespace CodeSpace.Core.Services.Workflows.RunSources;

/// <summary>
/// Stages the <c>workflow_run_request</c> + <c>workflow_run</c> rows and emits
/// <c>run.queued</c>. NO <c>SaveChangesAsync</c> — caller controls the transaction boundary.
/// The caller hands the runId to <c>IWorkflowRunDispatcher</c> after committing.
/// </summary>
public sealed class RunStarter : IRunStarter, IScopedDependency
{
    private readonly CodeSpaceDbContext _db;
    private readonly IRunRecordLogger _recordLogger;

    public RunStarter(CodeSpaceDbContext db, IRunRecordLogger recordLogger)
    {
        _db = db;
        _recordLogger = recordLogger;
    }

    public async Task<Guid> StartAsync(RunSourceEnvelope envelope, CancellationToken cancellationToken)
    {
        Validate(envelope);

        var requestId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        // Every run flows through a Consumed-state request row first. The request is the
        // "we accepted this trigger" record; the run is the execution handle.
        _db.WorkflowRunRequest.Add(new WorkflowRunRequest
        {
            Id = requestId,
            TeamId = envelope.TeamId,
            WorkflowId = envelope.WorkflowId,
            SourceType = envelope.SourceType,
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
            ReleaseHashAtRun = envelope.ReleaseHashAtRun ?? string.Empty,
            ParentRunId = envelope.ParentRunId,
            Status = WorkflowRunStatus.Pending,
            CreatedBy = envelope.CreatedBy,
            LastModifiedBy = envelope.CreatedBy,
        });

        // The caller commits the EF transaction, then calls
        // IWorkflowRunDispatcher.DispatchAsync(runId) which atomically transitions
        // Pending→Enqueued + hands to Hangfire. PostBoy-style: workflow_run.Status IS the
        // queue, no separate outbox row needed.

        // Ledger entry that the run exists. Engine emits run.started when it picks up the
        // background-job; run.queued here is the "we accepted" marker.
        await _recordLogger.RunQueuedAsync(runId, envelope.SourceType, envelope.ActorId, cancellationToken).ConfigureAwait(false);

        return runId;
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

        // Replay envelopes MUST carry the original release hash so the engine can verify the
        // workflow_version hash hasn't been tampered. CausationRequestId + ParentRunId travel
        // together with ReleaseHashAtRun: all three or none.
        var replayFieldsPresent =
            envelope.CausationRequestId.HasValue
            || envelope.ParentRunId.HasValue
            || !string.IsNullOrEmpty(envelope.ReleaseHashAtRun);

        var replayFieldsComplete =
            envelope.CausationRequestId.HasValue
            && envelope.ParentRunId.HasValue
            && !string.IsNullOrEmpty(envelope.ReleaseHashAtRun);

        if (replayFieldsPresent && !replayFieldsComplete)
            throw new ArgumentException(
                "Replay envelopes MUST carry CausationRequestId + ParentRunId + ReleaseHashAtRun together (all three or none)",
                nameof(envelope));
    }
}
