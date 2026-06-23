namespace CodeSpace.Core.Services.Workflows.RunSources;

/// <summary>
/// Uniform input shape for starting a workflow run. Captures every variation (manual / replay
/// / webhook-driven) in one place; the <see cref="IRunStarter"/> consumes it.
///
/// <para>Field philosophy:
/// <list type="bullet">
///   <item><b>Always set</b>: TeamId, WorkflowId, WorkflowVersion, SourceType, ActorType,
///         NormalizedPayloadJson, CreatedBy / LastModifiedBy.</item>
///   <item><b>Provider-event only</b>: ActivationId + ActivationSnapshotJson — link the run
///         back to the activation row that matched.</item>
///   <item><b>Replay only</b>: CausationRequestId + ParentRunId + ReleaseHashAtRun — preserve
///         the lineage and freeze the release the original ran against.</item>
///   <item><b>Optional</b>: ActorId — null when the actor is anonymous (Webhook).</item>
/// </list>
/// </para>
///
/// <para>Validation lives on the starter: any combination of fields that the schema
/// constraints would reject (e.g. ActorType=User without ActorId) throws before any DB
/// write happens, so a malformed envelope never leaves a partial state.</para>
/// </summary>
public sealed record RunSourceEnvelope
{
    public required Guid TeamId { get; init; }
    public required Guid WorkflowId { get; init; }

    /// <summary>Frozen workflow version this run targets. Manual = workflow.LatestVersion; replay = parent run's WorkflowVersion.</summary>
    public required int WorkflowVersion { get; init; }

    /// <summary>String from <see cref="CodeSpace.Messages.Constants.WorkflowRunSourceTypes"/> OR a matcher's TypeKey.</summary>
    public required string SourceType { get; init; }

    /// <summary>String from <see cref="CodeSpace.Messages.Constants.WorkflowRunActorTypes"/>.</summary>
    public required string ActorType { get; init; }

    /// <summary>Specific actor identity. Null for <c>Webhook</c> (no CodeSpace identity); otherwise required.</summary>
    public Guid? ActorId { get; init; }

    /// <summary>The normalised payload the engine sees as <c>{{trigger.*}}</c>. Must be valid JSON.</summary>
    public required string NormalizedPayloadJson { get; init; }

    /// <summary>Audit/audit-write user id stamped on the run row. Same as ActorId for user-driven runs; <c>SystemUsers.SeederId</c> for system-driven.</summary>
    public required Guid CreatedBy { get; init; }

    // Provider-event optional fields.
    public Guid? ActivationId { get; init; }
    public string? ActivationSnapshotJson { get; init; }

    // Replay-only optional fields.
    public Guid? CausationRequestId { get; init; }
    public Guid? ParentRunId { get; init; }
    public string? ReleaseHashAtRun { get; init; }

    /// <summary>
    /// Pre-resolved WorkSession binding for this run — the upstream resolver decides it; the starter only
    /// WRITES <c>SessionId</c> / <c>SessionTurnIndex</c> from it. NULL (the default at every current creation
    /// site) = a session-less run, byte-identical to pre-session behaviour.
    /// </summary>
    public SessionAssignment? Session { get; init; }

    // ── Idempotency triple — provider-event paths only ──────────────────────────────────
    //
    // Hardening (Phase 3.0): protects against duplicate ingestion of the SAME upstream
    // event. The provider (GitHub/GitLab/etc.) re-delivers a webhook after a transient
    // failure, the matcher fires a second time, and absent these we'd end up with two
    // runs for one external event.
    //
    // <para>
    // The unique index <c>idx_workflow_run_request_provider_event</c> on
    // (source_instance_id, external_event_id) enforces the no-duplicate guarantee at the
    // DB level. <see cref="RunStarter"/> catches the <c>23505</c> unique-violation,
    // logs it, and returns <c>Guid.Empty</c> to signal "already accepted, no-op".
    // </para>

    /// <summary>
    /// Free-form source identity — e.g. <c>"github.com/octocat/repo"</c> for a GitHub
    /// webhook, the schedule's UUID for cron firings. Drives the per-source audit view.
    /// Null for non-provider sources (manual / replay / api).
    /// </summary>
    public string? SourceInstanceId { get; init; }

    /// <summary>
    /// Provider-stamped event id. For GitHub this is the <c>X-GitHub-Delivery</c> header
    /// (uuid4); for GitLab it's <c>X-Gitlab-Event-UUID</c>. The OTHER half of the (source,
    /// id) idempotency tuple. Null for non-provider sources.
    /// </summary>
    public string? ExternalEventId { get; init; }

    /// <summary>
    /// Application-level idempotency token. Currently only the operator-driven path uses
    /// this (e.g. <c>Idempotency-Key</c> header on POST /run/{workflowId}); provider events
    /// rely on the (SourceInstanceId, ExternalEventId) pair instead. Null when not supplied.
    /// </summary>
    public string? IdempotencyKey { get; init; }
}
