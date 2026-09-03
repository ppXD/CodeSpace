using System.Text.Json;
using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Workflows;

namespace CodeSpace.Core.Services.Workflows.Lifecycle;

/// <summary>
/// Append-only writer for <see cref="WorkflowRunRecord"/>. Each public method builds the
/// canonical payload shape for its <c>record_type</c> (documented on
/// <c>WorkflowRunRecordTypes</c>) and inserts one row + SaveChanges. The DB assigns a gapful
/// run-scoped commit-admission cursor after acquiring the run's transaction lock. It is safe for
/// <c>sequence &gt; cursor</c> tails of that run, but is never a gapless/global commit cursor.
///
/// Scoped per DbContext (matches the engine's lifetime) so writes within a single run share
/// the same change-tracker. Thread-safety mirrors the DbContext's: one logger instance per
/// scoped concurrent run.
/// </summary>
public sealed class RunRecordLogger : IRunRecordLogger, IRedactedNodeOutputLedger, IScopedDependency
{
    private readonly CodeSpaceDbContext _db;

    public RunRecordLogger(CodeSpaceDbContext db) { _db = db; }

    public async Task RunQueuedAsync(Guid runId, string sourceType, Guid? actorId, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Serialize(new { source_type = sourceType, actor_id = actorId });
        await InsertAsync(runId, WorkflowRunRecordTypes.RunQueued, nodeId: null, iterationKey: string.Empty, payload, correlationId: null, parentRecordId: null, cancellationToken).ConfigureAwait(false);
    }

    public async Task RunStartedAsync(Guid runId, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Serialize(new { started_at = DateTimeOffset.UtcNow.ToString("o") });
        await InsertAsync(runId, WorkflowRunRecordTypes.RunStarted, nodeId: null, iterationKey: string.Empty, payload, correlationId: null, parentRecordId: null, cancellationToken).ConfigureAwait(false);
    }

    public async Task ReleaseLoadedAsync(Guid runId, int version, string definitionHash, int nodeCount, int edgeCount, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Serialize(new { version, definition_hash = definitionHash, node_count = nodeCount, edge_count = edgeCount });
        await InsertAsync(runId, WorkflowRunRecordTypes.ReleaseLoaded, nodeId: null, iterationKey: string.Empty, payload, correlationId: null, parentRecordId: null, cancellationToken).ConfigureAwait(false);
    }

    public async Task ScopeResolvedAsync(Guid runId, int wfCount, int teamCount, int sysCount, int secretPathCount, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Serialize(new { wf_count = wfCount, team_count = teamCount, sys_count = sysCount, secret_path_count = secretPathCount });
        await InsertAsync(runId, WorkflowRunRecordTypes.ScopeResolved, nodeId: null, iterationKey: string.Empty, payload, correlationId: null, parentRecordId: null, cancellationToken).ConfigureAwait(false);
    }

    public async Task VariablesSnapshottedAsync(Guid runId, int wfCount, int teamCount, string releaseHash, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Serialize(new { wf_count = wfCount, team_count = teamCount, release_hash = releaseHash });
        await InsertAsync(runId, WorkflowRunRecordTypes.VariablesSnapshotted, nodeId: null, iterationKey: string.Empty, payload, correlationId: null, parentRecordId: null, cancellationToken).ConfigureAwait(false);
    }

    public async Task RunCompletedAsync(Guid runId, TimeSpan duration, bool outputsPresent, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Serialize(new { duration_ms = (long)duration.TotalMilliseconds, outputs_present = outputsPresent });
        await InsertAsync(runId, WorkflowRunRecordTypes.RunCompleted, nodeId: null, iterationKey: string.Empty, payload, correlationId: null, parentRecordId: null, cancellationToken).ConfigureAwait(false);
    }

    public async Task RunFailedAsync(Guid runId, string error, TimeSpan duration, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Serialize(new { error, duration_ms = (long)duration.TotalMilliseconds });
        await InsertAsync(runId, WorkflowRunRecordTypes.RunFailed, nodeId: null, iterationKey: string.Empty, payload, correlationId: null, parentRecordId: null, cancellationToken).ConfigureAwait(false);
    }

    public async Task RunCancelledAsync(Guid runId, TimeSpan duration, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Serialize(new { duration_ms = (long)duration.TotalMilliseconds });
        await InsertAsync(runId, WorkflowRunRecordTypes.RunCancelled, nodeId: null, iterationKey: string.Empty, payload, correlationId: null, parentRecordId: null, cancellationToken).ConfigureAwait(false);
    }

    public async Task RunReplayedAsync(Guid runId, Guid? parentRunId, int snapshotCount, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Serialize(new { parent_run_id = parentRunId, snapshot_count = snapshotCount });
        await InsertAsync(runId, WorkflowRunRecordTypes.RunReplayed, nodeId: null, iterationKey: string.Empty, payload, correlationId: null, parentRecordId: null, cancellationToken).ConfigureAwait(false);
    }

    public async Task SupervisorRunRecoveredAsync(Guid runId, int attempt, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Serialize(new { attempt });
        await InsertAsync(runId, WorkflowRunRecordTypes.SupervisorRunRecovered, nodeId: null, iterationKey: string.Empty, payload, correlationId: null, parentRecordId: null, cancellationToken).ConfigureAwait(false);
    }

    public async Task<Guid> NodeStartedAsync(Guid runId, string nodeId, string iterationKey, IReadOnlyDictionary<string, JsonElement> resolvedInputs, IReadOnlyDictionary<string, JsonElement> resolvedConfig, CancellationToken cancellationToken)
    {
        // Payload includes BOTH inputs + config. Both come pre-redacted from the engine
        // (IPayloadRedactor). Operators can answer "what model / timeout / temperature was
        // this node running with" from the ledger.
        var payload = JsonSerializer.Serialize(new { inputs = resolvedInputs, config = resolvedConfig });
        return await InsertAsync(runId, WorkflowRunRecordTypes.NodeStarted, nodeId, iterationKey, payload, correlationId: null, parentRecordId: null, cancellationToken).ConfigureAwait(false);
    }

    public async Task<Guid> NodeCompletedAsync(Guid runId, string nodeId, string iterationKey, IReadOnlyDictionary<string, JsonElement> outputs, IReadOnlyList<string>? routingHints, TimeSpan duration, CancellationToken cancellationToken)
    {
        var payload = NodeCompletedPayload(outputs, routingHints, duration, outputsRedacted: false);

        return await InsertAsync(runId, WorkflowRunRecordTypes.NodeCompleted, nodeId, iterationKey, payload, correlationId: null, parentRecordId: null, cancellationToken).ConfigureAwait(false);
    }

    public async Task<Guid> NodeCompletedRedactedAsync(RedactedNodeCompletion completion, CancellationToken cancellationToken)
    {
        var payload = NodeCompletedPayload(completion.RedactedOutputs, completion.RoutingHints, completion.Duration, outputsRedacted: true);

        return await InsertAsync(completion.RunId, WorkflowRunRecordTypes.NodeCompleted, completion.NodeId, completion.IterationKey, payload, correlationId: null, parentRecordId: null, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The single <c>node.completed</c> payload shape. routingHints are persisted ONLY for branch nodes
    /// (NodeResult.RoutingHints != null) so the durable walker can rebuild edge-liveness on re-entry without
    /// re-running the branch; omitted when null → the view's routing_hints_jsonb projects SQL NULL (follow all
    /// edges). <paramref name="outputsRedacted"/> appends the claim that these outputs are a redacted stand-in;
    /// the un-redacted shapes are byte-identical to what this method emitted before the claim existed, so every
    /// row already written keeps parsing exactly as it did. Internal (not private) so both shapes are unit-pinned
    /// directly via InternalsVisibleTo — the wire key is a ledger contract, not an implementation detail.
    /// </summary>
    internal static string NodeCompletedPayload(IReadOnlyDictionary<string, JsonElement> outputs, IReadOnlyList<string>? routingHints, TimeSpan duration, bool outputsRedacted)
    {
        var duration_ms = (long)duration.TotalMilliseconds;

        if (!outputsRedacted)
            return routingHints == null
                ? JsonSerializer.Serialize(new { outputs, duration_ms })
                : JsonSerializer.Serialize(new { outputs, routingHints, duration_ms });

        return routingHints == null
            ? JsonSerializer.Serialize(new { outputs, duration_ms, outputsRedacted })
            : JsonSerializer.Serialize(new { outputs, routingHints, duration_ms, outputsRedacted });
    }

    public async Task NodeFailedAsync(Guid runId, string nodeId, string iterationKey, string error, TimeSpan duration, CancellationToken cancellationToken)
    {
        // outputs included as empty object for view-projection consistency (the view's
        // COALESCE on the latest record's payload->outputs needs the key to exist).
        var payload = JsonSerializer.Serialize(new { error, outputs = EmptyObject(), duration_ms = (long)duration.TotalMilliseconds });
        await InsertAsync(runId, WorkflowRunRecordTypes.NodeFailed, nodeId, iterationKey, payload, correlationId: null, parentRecordId: null, cancellationToken).ConfigureAwait(false);
    }

    public async Task AttemptFailedAsync(Guid runId, string nodeId, string iterationKey, int attempt, int maxAttempts, string error, TimeSpan duration, double retryInSeconds, Guid? parentRecordId, CancellationToken cancellationToken)
    {
        // The full per-attempt error is kept (it can differ from the FINAL error on node.failed) — the durable
        // answer to "what did attempt 2 actually see". Chains to the node.started row via parent_record_id, exactly
        // as external_call.* rows do, so the run-detail tree nests the attempt under its node.
        var payload = JsonSerializer.Serialize(new { attempt, max_attempts = maxAttempts, error, duration_ms = (long)duration.TotalMilliseconds, retry_in_seconds = retryInSeconds });
        await InsertAsync(runId, WorkflowRunRecordTypes.AttemptFailed, nodeId, iterationKey, payload, correlationId: null, parentRecordId, cancellationToken).ConfigureAwait(false);
    }

    public async Task NodeSkippedAsync(Guid runId, string nodeId, string iterationKey, string reason, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Serialize(new { reason, outputs = EmptyObject() });
        await InsertAsync(runId, WorkflowRunRecordTypes.NodeSkipped, nodeId, iterationKey, payload, correlationId: null, parentRecordId: null, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Record that the node's outputs could not reach the configured destination and were written inline instead. Never throws: it is called from the offload fail-open, where a second failure must not stop the node settling.</summary>
    public async Task NodeStorageUnavailableAsync(Guid runId, string nodeId, string iterationKey, string reason, string code, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Serialize(new { reason, code, outputs = EmptyObject() });

        await InsertAsync(runId, WorkflowRunRecordTypes.NodeStorageUnavailable, nodeId, iterationKey, payload, correlationId: null, parentRecordId: null, cancellationToken).ConfigureAwait(false);
    }

    public async Task NodeSuspendedAsync(Guid runId, string nodeId, string iterationKey, string waitKind, DateTimeOffset? wakeAt, CancellationToken cancellationToken)
    {
        // outputs included as empty object for view-projection consistency (the view's
        // COALESCE on the latest record's payload->outputs needs the key to exist).
        var payload = JsonSerializer.Serialize(new { wait_kind = waitKind, wake_at = wakeAt?.ToString("o"), outputs = EmptyObject() });
        await InsertAsync(runId, WorkflowRunRecordTypes.NodeSuspended, nodeId, iterationKey, payload, correlationId: null, parentRecordId: null, cancellationToken).ConfigureAwait(false);
    }

    public async Task WaitReissuedAsync(Guid runId, string nodeId, string iterationKey, string waitKind, Guid waitId, Guid byUserId, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Serialize(new { wait_kind = waitKind, wait_id = waitId.ToString(), by = byUserId.ToString() });
        await InsertAsync(runId, WorkflowRunRecordTypes.WaitReissued, nodeId, iterationKey, payload, correlationId: null, parentRecordId: null, cancellationToken).ConfigureAwait(false);
    }

    public async Task IterationStartedAsync(Guid runId, string nodeId, int itemCount, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Serialize(new { item_count = itemCount });
        await InsertAsync(runId, WorkflowRunRecordTypes.IterationStarted, nodeId, iterationKey: string.Empty, payload, correlationId: null, parentRecordId: null, cancellationToken).ConfigureAwait(false);
    }

    public async Task IterationCompletedAsync(Guid runId, string nodeId, int itemCount, TimeSpan duration, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Serialize(new { item_count = itemCount, duration_ms = (long)duration.TotalMilliseconds });
        await InsertAsync(runId, WorkflowRunRecordTypes.IterationCompleted, nodeId, iterationKey: string.Empty, payload, correlationId: null, parentRecordId: null, cancellationToken).ConfigureAwait(false);
    }

    public async Task<(Guid RecordId, Guid CorrelationId)> ExternalCallStartedAsync(Guid runId, string? nodeId, string target, string method, JsonElement? requestPayload, Guid? parentRecordId, CancellationToken cancellationToken)
    {
        var correlationId = Guid.NewGuid();
        var payload = JsonSerializer.Serialize(new
        {
            target,
            method,
            request_payload = requestPayload.HasValue ? (object)requestPayload.Value : null,
        });

        var recordId = await InsertAsync(runId, WorkflowRunRecordTypes.ExternalCallStarted, nodeId, iterationKey: string.Empty, payload, correlationId, parentRecordId, cancellationToken).ConfigureAwait(false);
        return (recordId, correlationId);
    }

    public async Task ExternalCallCompletedAsync(Guid runId, string? nodeId, Guid correlationId, int? statusCode, JsonElement? responsePayload, TimeSpan duration, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Serialize(new
        {
            status = statusCode,
            response_payload = responsePayload.HasValue ? (object)responsePayload.Value : null,
            duration_ms = (long)duration.TotalMilliseconds,
        });
        await InsertAsync(runId, WorkflowRunRecordTypes.ExternalCallCompleted, nodeId, iterationKey: string.Empty, payload, correlationId, parentRecordId: null, cancellationToken).ConfigureAwait(false);
    }

    public async Task ExternalCallFailedAsync(Guid runId, string? nodeId, Guid correlationId, string target, string error, TimeSpan duration, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Serialize(new
        {
            target,
            error,
            duration_ms = (long)duration.TotalMilliseconds,
        });
        await InsertAsync(runId, WorkflowRunRecordTypes.ExternalCallFailed, nodeId, iterationKey: string.Empty, payload, correlationId, parentRecordId: null, cancellationToken).ConfigureAwait(false);
    }

    public async Task LogAsync(Guid runId, string? nodeId, LogLevel level, string message, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Serialize(new { level = level.ToString().ToLowerInvariant(), message });
        await InsertAsync(runId, WorkflowRunRecordTypes.Log, nodeId, iterationKey: string.Empty, payload, correlationId: null, parentRecordId: null, cancellationToken).ConfigureAwait(false);
    }

    public async Task<Guid> RecordInteractionAsync(Guid runId, string recordType, string? nodeId, string iterationKey, Guid correlationId, Guid? parentRecordId, JsonElement payload, CancellationToken cancellationToken) =>
        await InsertAsync(runId, recordType, nodeId, iterationKey, payload.GetRawText(), correlationId, parentRecordId, cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Single insertion point; lets the public methods stay shape-only and centralises the
    /// "insert + save + return id" flow. SaveChanges is the per-write commit (matches the
    /// engine's existing per-node-write pattern); batching could land later if write volume
    /// becomes a bottleneck.
    /// </summary>
    private async Task<Guid> InsertAsync(Guid runId, string recordType, string? nodeId, string iterationKey, string payloadJson, Guid? correlationId, Guid? parentRecordId, CancellationToken cancellationToken)
    {
        var record = new WorkflowRunRecord
        {
            Id = Guid.NewGuid(),
            RunId = runId,
            RecordType = recordType,
            NodeId = nodeId,
            IterationKey = iterationKey,
            CorrelationId = correlationId,
            ParentRecordId = parentRecordId,
            OccurredAt = DateTimeOffset.UtcNow,

            // The ONE place every record's payload is written, so the ONE place it has to be storable. These payloads
            // carry model and harness words — an LLM completion, a node's outputs, an error line off a subprocess —
            // and a stray U+0000 in any of them is refused by jsonb, failing the write that was recording what
            // happened. See PersistedText: `jsonb` rejects the ESCAPE, so a raw-byte strip alone would not do.
            PayloadJson = PersistedText.SanitizeJson(payloadJson)!,
        };

        _db.WorkflowRunRecord.Add(record);
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return record.Id;
    }

    private static object EmptyObject() => new { };
}
