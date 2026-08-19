using CodeSpace.Messages.Contracts;

namespace CodeSpace.Core.Persistence.Entities;

/// <summary>
/// The logical identity of one model call made while executing a workflow run. A logical call is deliberately
/// separate from <see cref="WorkflowRunModelCallAttempt"/>: retries, provider fallbacks and transport replays append
/// physical attempts without overwriting which workflow/node/work-unit attempt requested the inference.
///
/// <para>Two producers write these rows today — the interaction-tape projection and the harness capture plane — and an
/// id-addressed telemetry reader surfaces them. None of that is read by completion or terminal authority. The nullable
/// requested-route and execution-identity fields are what let each adapter record honest Partial/Unavailable capture
/// rather than inventing an exact model or a plan identity it could not observe.</para>
/// </summary>
public class WorkflowRunModelCall : IEntity<Guid>, IAuditable
{
    public Guid Id { get; set; }

    /// <summary>Tenant scope on every logical call.</summary>
    public Guid TeamId { get; set; }

    /// <summary>The owning workflow run. A soft workflow-aggregate link so telemetry can outlive run cleanup.</summary>
    public Guid WorkflowRunId { get; set; }

    /// <summary>The authored/runtime node id when the call is node-bound; null for run-level orchestration calls.</summary>
    public string? NodeId { get; set; }

    /// <summary>The workflow cell identity; empty for the top-level/non-container case.</summary>
    public string IterationKey { get; set; } = string.Empty;

    /// <summary>The atomic WorkUnitRef plan row; null together with PlanVersion/WorkUnitId outside a plan-bound attempt.</summary>
    public Guid? WorkPlanId { get; set; }

    public int? PlanVersion { get; set; }

    public string? WorkUnitId { get; set; }

    /// <summary>The unit contract digest at dispatch. Nullable for a contract-less or legacy unit.</summary>
    public string? WorkUnitContractHash { get; set; }

    /// <summary>The durable execution attempt (AgentRun today, generic attempt identity for future harnesses).</summary>
    public Guid? ExecutionAttemptId { get; set; }

    /// <summary>One-based server authorization order of the execution attempt within its unit.</summary>
    public int? ExecutionAttemptOrdinal { get; set; }

    /// <summary>The P+ generation the execution attempt was authorized under; null when genuinely unavailable.</summary>
    public int? ExecutionGeneration { get; set; }

    /// <summary>One-based order of this logical model call within its execution scope.</summary>
    public long CallOrdinal { get; set; } = 1;

    /// <summary>
    /// Open, versioned source adapter identity when this row is a physical projection, e.g.
    /// <c>workflow-run-record/v1</c>. Null together with <see cref="SourceCorrelationId"/> for native/direct producers.
    /// </summary>
    public string? SourceKind { get; set; }

    /// <summary>
    /// Stable logical identity in <see cref="SourceKind"/>. For the interaction tape this is its correlation id;
    /// it is never inferred from a global sequence or an occurrence timestamp.
    /// </summary>
    public Guid? SourceCorrelationId { get; set; }

    /// <summary>Versioned semantic purpose, e.g. supervisor.decision/v1, planner.plan/v1 or grader.oracle/v1.</summary>
    public string Purpose { get; set; } = "unknown/v1";

    /// <summary>The requested route before provider resolution. Null means auto/unobserved, never "effective".</summary>
    public string? RequestedProvider { get; set; }

    public string? RequestedModel { get; set; }

    /// <summary>The exact catalog row requested when pinned; soft reference so telemetry survives credential cleanup.</summary>
    public Guid? RequestedModelRowId { get; set; }

    /// <summary>Open, versioned selection policy name when resolution was automatic.</summary>
    public string? SelectionPolicy { get; set; }

    /// <summary>Artifact containing the canonical logical request when captured; null when unavailable.</summary>
    public Guid? RequestArtifactId { get; set; }

    /// <summary>How the observation was obtained, e.g. in-process, harness-native or controlled-proxy.</summary>
    public string CaptureSource { get; set; } = "unknown";

    /// <summary>The shared six-state Workflow Run capture vocabulary; evidence quality, never call success.</summary>
    public WorkflowRunCaptureCompleteness CaptureCompleteness { get; set; } = WorkflowRunCaptureCompleteness.Unavailable;

    /// <summary>The persisted data-contract version, independent of provider/harness protocol versions.</summary>
    public int SchemaVersion { get; set; } = WorkflowRunDataContract.CurrentVersion;

    public DateTimeOffset CreatedDate { get; set; }
    public Guid CreatedBy { get; set; }
    public DateTimeOffset LastModifiedDate { get; set; }
    public Guid LastModifiedBy { get; set; }
}
