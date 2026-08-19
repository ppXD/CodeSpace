using CodeSpace.Messages.Contracts;

namespace CodeSpace.Core.Persistence.Entities;

/// <summary>
/// One physical provider attempt of a <see cref="WorkflowRunModelCall"/>. Requested route lives on the logical call;
/// effective provider/model, provider request id, wire artifacts, usage, cost and timing live here so retry/fallback
/// attempts remain individually auditable instead of being collapsed into a last-write-wins summary.
/// </summary>
public class WorkflowRunModelCallAttempt : IEntity<Guid>, IAuditable
{
    public Guid Id { get; set; }

    /// <summary>Denormalized tenant scope, protected by the composite parent FK.</summary>
    public Guid TeamId { get; set; }

    /// <summary>Denormalized workflow-run scope for high-volume run queries, protected by the composite parent FK.</summary>
    public Guid WorkflowRunId { get; set; }

    public Guid ModelCallId { get; set; }

    /// <summary>One-based physical attempt order within the logical call.</summary>
    public int AttemptOrdinal { get; set; }

    /// <summary>
    /// Exact immutable <c>interaction.started</c> source row when observed. Null is honest missing/late evidence;
    /// it may be filled once, never replaced or removed.
    /// </summary>
    public Guid? SourceStartedRecordId { get; set; }

    /// <summary>
    /// Exact immutable <c>interaction.completed</c> or <c>interaction.failed</c> source row. This is the idempotent
    /// physical-attempt admission key for the Workflow Run record projector.
    /// </summary>
    public Guid? SourceTerminalRecordId { get; set; }

    /// <summary>
    /// Monotonic evidence revision for a projected attempt: zero for a native/direct row, one at first source
    /// admission, then exactly +1 for each late-evidence update. It is also EF's optimistic concurrency token.
    /// </summary>
    public int SourceEvidenceRevision { get; set; }

    public string? EffectiveProvider { get; set; }

    public string? EffectiveModel { get; set; }

    /// <summary>The catalog row actually dispatched, when resolution was observable.</summary>
    public Guid? EffectiveModelRowId { get; set; }

    /// <summary>Open transport protocol name/version, e.g. in-process/v1, harness-native/v1 or proxy/v1.</summary>
    public string? TransportKind { get; set; }

    /// <summary>Sanitized endpoint identity/fingerprint; never a URL containing credentials or query secrets.</summary>
    public string? EndpointFingerprint { get; set; }

    public string? ProviderRequestId { get; set; }

    /// <summary>The exact provider-wire request artifact, distinct from the logical call's canonical request.</summary>
    public Guid? RequestArtifactId { get; set; }

    public Guid? ResponseArtifactId { get; set; }

    public Guid? ErrorArtifactId { get; set; }

    public string Status { get; set; } = "Pending";

    public string? ErrorCode { get; set; }

    public string? FinishReason { get; set; }

    public int? HttpStatusCode { get; set; }

    public string CaptureSource { get; set; } = "unknown";

    public WorkflowRunCaptureCompleteness CaptureCompleteness { get; set; } = WorkflowRunCaptureCompleteness.Unavailable;

    public long? InputTokens { get; set; }

    public long? OutputTokens { get; set; }

    public long? CacheReadTokens { get; set; }

    public long? CacheWriteTokens { get; set; }

    public long? ReasoningTokens { get; set; }

    /// <summary>Provider-attempt cost in <see cref="CostCurrency"/> under <see cref="PricingVersion"/>.</summary>
    public decimal? CostAmount { get; set; }

    public string? CostCurrency { get; set; }

    public string? PricingVersion { get; set; }

    /// <summary>
    /// Every figure this row's producer DECLARES it could not produce, named from <see cref="ModelCallFigures"/>. Each
    /// named figure's own column is NULL rather than zero, which the database enforces — so a token class a harness never
    /// reports, a provider request id its CLI never prints, and a cost for a model with no price entry are all readable
    /// as unavailable instead of as measured.
    ///
    /// <para>Empty means the producer declares nothing, NOT that every figure on the row was measured — which is what
    /// every row written before this column existed says.</para>
    /// </summary>
    public string[] UnavailableFigures { get; set; } = Array.Empty<string>();

    /// <summary>
    /// The captured native frame this attempt was read out of, for a row projected from a harness's own record. A soft
    /// reference like the other cross-aggregate ids here — telemetry may outlive the frames — and unique per attempt, so
    /// one frame can evidence at most one attempt. Null for a producer that did not read a frame, which is every
    /// in-process call.
    /// </summary>
    public Guid? SourceNativeRecordId { get; set; }

    public DateTimeOffset StartedAt { get; set; }

    public DateTimeOffset? FirstTokenAt { get; set; }

    public DateTimeOffset? CompletedAt { get; set; }

    public int SchemaVersion { get; set; } = WorkflowRunDataContract.CurrentVersion;

    public DateTimeOffset CreatedDate { get; set; }
    public Guid CreatedBy { get; set; }
    public DateTimeOffset LastModifiedDate { get; set; }
    public Guid LastModifiedBy { get; set; }
}
