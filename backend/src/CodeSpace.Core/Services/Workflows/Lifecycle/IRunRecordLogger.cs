using System.Text.Json;

namespace CodeSpace.Core.Services.Workflows.Lifecycle;

/// <summary>
/// Emits append-only lifecycle records to <c>workflow_run_record</c>. The engine uses this for
/// every node-state transition (started / completed / failed / skipped) and iteration boundary.
/// Plugin authors with INodeRunContext access can emit their own <c>external_call.*</c> and
/// <c>log</c> records to make their internal API calls visible in the run-detail UI.
///
/// All methods are fire-and-forget from the caller's perspective: each call inserts one
/// record + SaveChanges. Sequence is assigned by the DB (BIGSERIAL); the returned id is the
/// generated record id, useful as a correlation parent for nested events.
///
/// Stateless service; thread-safe per scoped DbContext.
/// </summary>
public interface IRunRecordLogger
{
    // Run-level lifecycle. Each of these emits ONE workflow_run_record at the matching phase
    // of execution. The run-detail UI's timeline pane stitches these together with node.*
    // records to render a chronological story.

    /// <summary>Emit <c>run.queued</c>. Called by WorkflowService when a workflow_run is inserted (status=Pending).</summary>
    Task RunQueuedAsync(Guid runId, string sourceType, Guid? actorId, CancellationToken cancellationToken);

    /// <summary>Emit <c>run.started</c>. Called by the engine when it transitions Pending→Running.</summary>
    Task RunStartedAsync(Guid runId, CancellationToken cancellationToken);

    /// <summary>Emit <c>release.loaded</c>. Called after the frozen workflow_version JSON is fetched.</summary>
    Task ReleaseLoadedAsync(Guid runId, int version, string definitionHash, int nodeCount, int edgeCount, CancellationToken cancellationToken);

    /// <summary>Emit <c>scope.resolved</c>. Called after NodeRunScope is built (counts per bag are useful for ops triage).</summary>
    Task ScopeResolvedAsync(Guid runId, int wfCount, int teamCount, int sysCount, int secretPathCount, CancellationToken cancellationToken);

    /// <summary>Emit <c>variables.snapshotted</c>. Called after first-run snapshot persist.</summary>
    Task VariablesSnapshottedAsync(Guid runId, int wfCount, int teamCount, string releaseHash, CancellationToken cancellationToken);

    /// <summary>Emit <c>run.completed</c>. Called when the engine reaches a successful terminal (or drains the graph).</summary>
    Task RunCompletedAsync(Guid runId, TimeSpan duration, bool outputsPresent, CancellationToken cancellationToken);

    /// <summary>Emit <c>run.failed</c>. Called on NodeFailureException / WorkflowSecretLeakException; <paramref name="error"/> is the run-level message.</summary>
    Task RunFailedAsync(Guid runId, string error, TimeSpan duration, CancellationToken cancellationToken);

    /// <summary>Emit <c>run.cancelled</c>. Reserved for operator-cancel.</summary>
    Task RunCancelledAsync(Guid runId, TimeSpan duration, CancellationToken cancellationToken);

    /// <summary>Emit <c>run.replayed</c>. Called when the engine forks into the replay path (snapshot rows pre-existed).</summary>
    Task RunReplayedAsync(Guid runId, Guid? parentRunId, int snapshotCount, CancellationToken cancellationToken);

    /// <summary>
    /// Emit <c>supervisor.run_recovered</c> — the reconciler re-dispatched an abandoned-Running supervisor run
    /// with a recoverable in-flight decision (PR-E P1-2). One record per recovery attempt; the per-run COUNT of
    /// these is the durable bound the reconciler counts before re-dispatching. <paramref name="attempt"/> is the
    /// 1-based ordinal of this recovery (count of prior recovery records + 1).
    /// </summary>
    Task SupervisorRunRecoveredAsync(Guid runId, int attempt, CancellationToken cancellationToken);

    /// <summary>
    /// Emit <c>node.started</c>. Returns the new record's id so callers can chain nested
    /// events (e.g. external calls made by this node) as <c>parent_record_id</c>.
    /// </summary>
    /// <param name="resolvedInputs">Engine-resolved + redactor-processed inputs. MUST be safe to persist (redaction applied upstream by the engine).</param>
    /// <param name="resolvedConfig">Engine-resolved + redactor-processed config. Same redaction guarantee.</param>
    Task<Guid> NodeStartedAsync(Guid runId, string nodeId, string iterationKey, IReadOnlyDictionary<string, JsonElement> resolvedInputs, IReadOnlyDictionary<string, JsonElement> resolvedConfig, CancellationToken cancellationToken);

    /// <summary>
    /// Emit <c>node.completed</c>. <paramref name="outputs"/> is the node's resolved output map.
    /// <paramref name="routingHints"/> is the branch node's chosen output handles (null when the
    /// node didn't branch); persisted so the durable walker can rebuild edge-liveness on re-entry
    /// without re-running the branch node.
    /// </summary>
    Task NodeCompletedAsync(Guid runId, string nodeId, string iterationKey, IReadOnlyDictionary<string, JsonElement> outputs, IReadOnlyList<string>? routingHints, TimeSpan duration, CancellationToken cancellationToken);

    /// <summary>Emit <c>node.failed</c>. <paramref name="error"/> is the human-readable failure reason.</summary>
    Task NodeFailedAsync(Guid runId, string nodeId, string iterationKey, string error, TimeSpan duration, CancellationToken cancellationToken);

    /// <summary>Emit <c>node.skipped</c>. <paramref name="reason"/> documents why (e.g. "all-incoming-edges-dead").</summary>
    Task NodeSkippedAsync(Guid runId, string nodeId, string iterationKey, string reason, CancellationToken cancellationToken);

    /// <summary>
    /// Emit <c>node.suspended</c> — the immutable audit copy of a node parking the run. The
    /// <c>workflow_run_node</c> view projects this to <c>NodeStatus.Suspended</c>; the mutable
    /// wait state lives in <c>workflow_run_wait</c>. <paramref name="waitKind"/> is one of
    /// <c>WorkflowWaitKinds</c>; <paramref name="wakeAt"/> is set for Timer waits.
    /// </summary>
    Task NodeSuspendedAsync(Guid runId, string nodeId, string iterationKey, string waitKind, DateTimeOffset? wakeAt, CancellationToken cancellationToken);

    /// <summary>Emit <c>iteration.started</c> for flow.iterate boundary.</summary>
    Task IterationStartedAsync(Guid runId, string nodeId, int itemCount, CancellationToken cancellationToken);

    /// <summary>Emit <c>iteration.completed</c> for flow.iterate boundary.</summary>
    Task IterationCompletedAsync(Guid runId, string nodeId, int itemCount, TimeSpan duration, CancellationToken cancellationToken);

    /// <summary>
    /// Emit <c>external_call.started</c>. Returns the record id and a fresh correlation id;
    /// callers pass the same correlation id to <see cref="ExternalCallCompletedAsync"/> /
    /// <see cref="ExternalCallFailedAsync"/> so the UI can pair request with response.
    /// </summary>
    Task<(Guid RecordId, Guid CorrelationId)> ExternalCallStartedAsync(Guid runId, string? nodeId, string target, string method, JsonElement? requestPayload, Guid? parentRecordId, CancellationToken cancellationToken);

    /// <summary>Emit <c>external_call.completed</c> paired with the original start by <paramref name="correlationId"/>.</summary>
    Task ExternalCallCompletedAsync(Guid runId, string? nodeId, Guid correlationId, int? statusCode, JsonElement? responsePayload, TimeSpan duration, CancellationToken cancellationToken);

    /// <summary>Emit <c>external_call.failed</c> paired with the original start by <paramref name="correlationId"/>.</summary>
    Task ExternalCallFailedAsync(Guid runId, string? nodeId, Guid correlationId, string target, string error, TimeSpan duration, CancellationToken cancellationToken);

    /// <summary>Emit a free-form <c>log</c> entry tied to a node or to the run as a whole (nodeId=null).</summary>
    Task LogAsync(Guid runId, string? nodeId, LogLevel level, string message, CancellationToken cancellationToken);
}

/// <summary>Severity for the <c>log</c> record type's payload.</summary>
public enum LogLevel { Info, Warn, Error }
