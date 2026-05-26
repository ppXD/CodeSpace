using CodeSpace.Messages.Enums;

namespace CodeSpace.Core.Persistence.Entities;

/// <summary>
/// Generic per-source run-request record. EVERY <see cref="WorkflowRun"/> traces back through
/// exactly one of these. Captures the source identity (manual / api / schedule.cron / replay /
/// provider.&lt;vendor&gt;.&lt;event&gt;), the raw + normalised payload, idempotency + tracing
/// handles, and the actor metadata that's not tied to a single auth scheme.
///
/// A request may produce 0 runs (no matching activation, validation failure, dedup hit) or
/// exactly 1 run; the engine never fans a single request out into multiple runs (avoids replay
/// ambiguity). Webhook ingestion + manual-run + replay all funnel through this row; the run row
/// itself just points back via <see cref="WorkflowRun.RunRequestId"/>.
///
/// Audit columns are deliberately absent — this is an append-only event journal owned by the
/// engine, not a user-edited entity. We get the row's "createdBy" from <see cref="ActorId"/> /
/// <see cref="ActorType"/> instead.
/// </summary>
public class WorkflowRunRequest : IEntity<Guid>
{
    public Guid Id { get; set; }
    public Guid TeamId { get; set; }

    // Match outcome — both nullable until the matcher resolves them.
    public Guid? WorkflowId { get; set; }
    public Guid? ActivationId { get; set; }

    /// <summary>Frozen copy of the activation row at match time. Replays / audits read this rather than today's (possibly mutated) activation.</summary>
    public string? ActivationSnapshotJson { get; set; }

    /// <summary>
    /// Open string discriminator. Examples: "manual", "replay", "schedule.cron", "api",
    /// "provider.github.pull_request". See <see cref="Messages.Constants.WorkflowRunSourceTypes"/>
    /// for the well-known constants. Stored as TEXT — adding a new source is zero schema churn.
    /// </summary>
    public string SourceType { get; set; } = default!;

    /// <summary>
    /// Stable handle for the specific source instance — e.g. "github.com/octocat/repo" for
    /// a GitHub webhook, the schedule's UUID for cron firings. Used for the per-source audit
    /// view ("show me every event from this webhook source").
    /// </summary>
    public string? SourceInstanceId { get; set; }

    /// <summary>External event id — webhook delivery id, schedule fire id, etc. Pairs with
    /// <see cref="SourceType"/> in a unique index so duplicate deliveries are rejected at the DB.</summary>
    public string? ExternalEventId { get; set; }

    /// <summary>App-level idempotency token. Unique-when-not-null index dedupes duplicate replay clicks / API calls.</summary>
    public string? IdempotencyKey { get; set; }

    /// <summary>Caller-supplied trace id (e.g. propagated from HTTP headers). Joins our logs with the caller's.</summary>
    public Guid? CorrelationId { get; set; }

    /// <summary>The id of the request that triggered THIS request (e.g. replay chain links original → replay1 → replay2). Drives the audit view's lineage.</summary>
    public Guid? CausationId { get; set; }

    /// <summary>'user' | 'system' | 'webhook' | 'cron' | 'child_workflow'. Pairs with <see cref="ActorId"/>.</summary>
    public string? ActorType { get; set; }

    /// <summary>User id (when ActorType='user'), parent run id (when 'child_workflow'), etc. Polymorphic — interpretation depends on ActorType.</summary>
    public Guid? ActorId { get; set; }

    public DateTimeOffset ReceivedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? VerifiedAt { get; set; }
    public DateTimeOffset? NormalizedAt { get; set; }

    /// <summary>Request headers with secret/auth values stripped. Audit only — never re-played to providers.</summary>
    public string? RawHeadersRedactedJson { get; set; }

    /// <summary>
    /// The shape the engine sees as <c>{{trigger.*}}</c>. Built by the source plugin's
    /// normaliser; for manual / replay we copy the operator-supplied payload as-is.
    /// </summary>
    public string NormalizedPayloadJson { get; set; } = "{}";

    /// <summary>IP, user-agent, schedule fire-time, etc. — anything that's about the request itself rather than its content.</summary>
    public string RequestMetadataJson { get; set; } = "{}";

    /// <summary>Signature-check outcome for webhook sources (algorithm, key-id, validated bool, error message). Null for trusted sources.</summary>
    public string? VerificationResultJson { get; set; }

    public WorkflowRunRequestStatus Status { get; set; } = WorkflowRunRequestStatus.Received;

    /// <summary>Populated when <see cref="Status"/>=<see cref="WorkflowRunRequestStatus.Rejected"/>.</summary>
    public string? Error { get; set; }
}
