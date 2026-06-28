namespace CodeSpace.Messages.Constants;

/// <summary>
/// Canonical engine-emitted <c>record_type</c> values for <c>workflow_run_record</c>. Each
/// constant is also the JSON wire value used by the run-detail UI, replay tooling, and any
/// external consumer that reads the ledger directly — renaming a constant is a breaking
/// change for those consumers. Every literal here is pinned by
/// <c>WorkflowRunRecordTypesTests</c>; renaming a const without updating the pin test
/// makes the rename a compile-error-visible decision rather than a silent ledger schema
/// break.
///
/// Convention: dotted-namespace, source-family first. Plugin authors may emit their own
/// types under their own namespace (e.g. <c>llm.token</c>, <c>slack.message_posted</c>)
/// without engine churn — they just must not collide with the namespaces below.
/// </summary>
public static class WorkflowRunRecordTypes
{
    // ─── Run lifecycle ────────────────────────────────────────────────────────
    // The run-level timeline. Together with node.* records, these give the operator a
    // complete chronological story of one workflow run: when the request landed, when the
    // engine picked it up, when it loaded the frozen definition, when scope built, when
    // the snapshot was persisted, and how it ended. The run-detail UI's timeline pane
    // reads ALL of these.

    /// <summary>Service inserted the workflow_run row (status=Pending). Payload: {"source_type":"...","actor_id":"..."}.</summary>
    public const string RunQueued = "run.queued";

    /// <summary>Engine atomic-claimed the run (CAS Enqueued → Running). Payload: {"started_at":"ISO"}.</summary>
    public const string RunStarted = "run.started";

    /// <summary>Frozen workflow_version JSON + definition_hash loaded. Payload: {"version":N,"definition_hash":"...","node_count":N,"edge_count":N}.</summary>
    public const string ReleaseLoaded = "release.loaded";

    /// <summary>NodeRunScope built (trigger/wf/team/input/sys bags populated). Payload: {"wf_count":N,"team_count":N,"sys_count":N,"secret_path_count":N}.</summary>
    public const string ScopeResolved = "scope.resolved";

    /// <summary>workflow_run_variable snapshot rows committed on first-run path. Payload: {"wf_count":N,"team_count":N,"release_hash":"..."}.</summary>
    public const string VariablesSnapshotted = "variables.snapshotted";

    /// <summary>Engine reached a successful Terminal (or graph drained). Payload: {"duration_ms":N,"outputs_present":bool}.</summary>
    public const string RunCompleted = "run.completed";

    /// <summary>Engine halted on NodeFailureException / WorkflowSecretLeakException. Payload: {"error":"...","duration_ms":N}.</summary>
    public const string RunFailed = "run.failed";

    /// <summary>Operator cancelled the run mid-flight. Payload: {"duration_ms":N}.</summary>
    public const string RunCancelled = "run.cancelled";

    /// <summary>Engine forked into the replay path (snapshot rows pre-existing). Payload: {"parent_run_id":"...","snapshot_count":N}.</summary>
    public const string RunReplayed = "run.replayed";

    /// <summary>
    /// The stuck-run reconciler re-dispatched an abandoned-Running supervisor run that has a recoverable
    /// in-flight decision (PR-E P1-2). Payload: {"attempt":N}. ONE record is appended per recovery attempt,
    /// so its per-run count IS the durable recovery counter the reconciler bounds against — a deterministically
    /// crashing run accumulates these until the cap, then falls through to the abandoned-Running failure sweep.
    /// </summary>
    public const string SupervisorRunRecovered = "supervisor.run_recovered";

    // ─── Node lifecycle ───────────────────────────────────────────────────────
    // The (run_id, node_id, iteration_key) cell can transition Pending → Running → terminal.
    // The view projects status from the latest record_type for the cell.

    /// <summary>Engine began executing the node. Payload: {"inputs":{...},"config":{...}}.</summary>
    public const string NodeStarted = "node.started";

    /// <summary>Node returned Success. Payload: {"outputs":{...},"duration_ms":N}.</summary>
    public const string NodeCompleted = "node.completed";

    /// <summary>Node threw or returned Failure. Payload: {"error":"...","outputs":{},"duration_ms":N}.</summary>
    public const string NodeFailed = "node.failed";

    /// <summary>Node was skipped (every incoming edge dead). Payload: {"reason":"..."}.</summary>
    public const string NodeSkipped = "node.skipped";

    /// <summary>Node suspended awaiting external input. Payload: {"resume_token":"..."}.</summary>
    public const string NodeSuspended = "node.suspended";

    // ─── Retry attempts ──────────────────────────────────────────────────────
    // A node-scoped SUB-event (like external_call.*): deliberately NOT under the node.* prefix so it stays
    // OUT of the workflow_run_node cell-state view (which projects record_type LIKE 'node.%'). One row per
    // FAILED-but-retried attempt, chained to the node.started row via parent_record_id — the durable,
    // queryable retry history that replaces the lossy free-text Warn log.

    /// <summary>A node attempt failed and WILL be retried. Payload: {"attempt":N,"max_attempts":M,"error":"...","duration_ms":N,"retry_in_seconds":S}.</summary>
    public const string AttemptFailed = "attempt.failed";

    // ─── Iteration boundaries (flow.iterate, future flow.subworkflow) ─────────

    /// <summary>flow.iterate about to begin walking items. Payload: {"item_count":N}.</summary>
    public const string IterationStarted = "iteration.started";

    /// <summary>flow.iterate finished walking items. Payload: {"item_count":N,"duration_ms":N}.</summary>
    public const string IterationCompleted = "iteration.completed";

    // ─── External calls ───────────────────────────────────────────────────────
    // Paired by correlation_id: one .started + one .completed (or .failed) share the same
    // correlation id so the UI can render them as a single request/response card.

    /// <summary>Plugin began an external API call. Payload: {"target":"...","method":"...","request_artifact_id":"..."}.</summary>
    public const string ExternalCallStarted = "external_call.started";

    /// <summary>External call returned. Payload: {"status":N,"response_artifact_id":"...","duration_ms":N}.</summary>
    public const string ExternalCallCompleted = "external_call.completed";

    /// <summary>External call threw (network error, non-2xx with retry-exhausted, etc). Payload: {"target":"...","error":"...","duration_ms":N}.</summary>
    public const string ExternalCallFailed = "external_call.failed";

    // ─── Model interactions (the generic LLM/reasoning capture) ───────────────
    // Paired by correlation_id, exactly like external_call.* — one .started + one .completed (or
    // .failed). An OPEN `kind` payload field names the step (supervisor.decision / planner.plan /
    // llm.complete.node / any future caller), so the SAME three types carry every model-touching
    // step with no per-caller record type. Prompt / completion / reasoning ride the payload inline
    // when small, else as a {"$artifact_id":...} ref. Captured generically at the LLM-client seam.

    /// <summary>A model call began. Payload: {"kind":"...","model":"...","params":{...},"prompt":{"system":...,"user":...}} — prompt fields inline-or-$artifact_id.</summary>
    public const string InteractionStarted = "interaction.started";

    /// <summary>A model call returned. Payload: {"kind":"...","model":"...","usage":{"inputTokens":N,"outputTokens":N,"finishReason":"..."},"output":...} — output inline-or-$artifact_id.</summary>
    public const string InteractionCompleted = "interaction.completed";

    /// <summary>A model call threw. Payload: {"kind":"...","error":"...","category"?:"..."}.</summary>
    public const string InteractionFailed = "interaction.failed";

    // ─── Log lines ────────────────────────────────────────────────────────────

    /// <summary>Free-form log line emitted by a node. Payload: {"level":"info|warn|error","message":"..."}.</summary>
    public const string Log = "log";
}
