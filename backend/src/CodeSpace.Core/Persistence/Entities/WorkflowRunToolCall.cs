using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Contracts;

namespace CodeSpace.Core.Persistence.Entities;

/// <summary>
/// The LOGICAL identity of one tool invocation made while executing a workflow run — which tool, whether it could
/// mutate anything, and the canonical arguments it was given. Today none of that is answerable from data: tool
/// activity survives as untyped ledger noise plus, where a hash is kept at all, an INPUT hash, so "which tool did
/// what, with what arguments, to what effect, and did it retry" is not a query anyone can run.
///
/// <para>Deliberately separate from <see cref="WorkflowRunToolCallAttempt"/>, and separate along exactly the seam
/// <see cref="WorkflowRunModelCall"/> uses, because the question is the same question: a retry APPENDS a physical
/// attempt rather than overwriting the transport, result, outcome or timing of the try before it. A plane that folded
/// them would answer "did this retry?" with last-write-wins.</para>
///
/// <para>This is an OBSERVATION plane and is deliberately powerless. <c>tool_call_ledger</c> keeps governance and
/// exactly-once — its unique (agent run, idempotency key) index is the only dedup authority for a side effect, and its
/// status CAS the only single-winner execution claim. Nothing here dedups or gates an invocation; where a row is a
/// projection of a ledger row, <see cref="SourceCorrelationId"/> is that ledger row's id and the unique source index
/// deduplicates the PROJECTION alone.</para>
///
/// <para>Keyed as a model call is, so one reader can ask both planes the same question, which is why
/// <see cref="WorkflowRunId"/> is non-nullable. A tool call made by a STANDALONE Agent Run therefore has no row here
/// yet — a named gap, not an oversight: that case belongs to the Agent-Run-keyed harness execution plane, and giving
/// it a home here would mean a second nullable identity axis the attempt's composite scope proof could not use.</para>
///
/// <para>Schema-only in this slice: no producer, reader, fold or bill touches it, and <see cref="State"/> describes
/// the INVOCATION lifecycle only — never a task verdict, a completion, or a terminal decision.</para>
/// </summary>
public sealed class WorkflowRunToolCall : IEntity<Guid>
{
    public Guid Id { get; set; }

    /// <summary>Tenant scope on every logical call.</summary>
    public Guid TeamId { get; set; }

    /// <summary>The owning workflow run, proved by a composite foreign key as the model-call plane's is.</summary>
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

    /// <summary>The durable execution attempt (Agent Run today, generic attempt identity for future harnesses).</summary>
    public Guid? ExecutionAttemptId { get; set; }

    /// <summary>One-based server authorization order of the execution attempt within its unit.</summary>
    public int? ExecutionAttemptOrdinal { get; set; }

    /// <summary>The generation the execution attempt was authorized under; null when genuinely unavailable.</summary>
    public int? ExecutionGeneration { get; set; }

    /// <summary>One-based order of this logical tool call within its execution scope.</summary>
    public long CallOrdinal { get; set; } = 1;

    /// <summary>
    /// The model call whose turn emitted this tool use — the join that answers which model decision caused a side
    /// effect. A SOFT reference for the same reason artifact ids are: model-call rows arrive through a bounded
    /// sweeper, so a foreign key would refuse a tool call whose causing model call is not projected yet.
    /// </summary>
    public Guid? ModelCallId { get; set; }

    /// <summary>Versioned semantic purpose of the invocation, e.g. agent.edit/v1 or supervisor.probe/v1.</summary>
    public string Purpose { get; set; } = "unknown/v1";

    /// <summary>
    /// Versioned tool-contract key, <c>&lt;kind&gt;/v&lt;major&gt;</c>, for the reason a harness type key is
    /// versioned: a row read a year from now must be interpretable against the tool contract that produced it rather
    /// than against whatever that name has since come to mean.
    /// </summary>
    public string ToolKind { get; set; } = string.Empty;

    /// <summary>The fabric or server that published the tool (an MCP server identity); null for a harness builtin.</summary>
    public string? ToolNamespace { get; set; }

    /// <summary>The wire name actually invoked, unnormalized, so a tool no adapter recognized is still identifiable.</summary>
    public string ToolName { get; set; } = string.Empty;

    /// <summary>Whether the tool could mutate anything. Three-valued because an unobserved effect class defaulted either way is a lie: read-only understates the risk and side-effecting overstates the evidence.</summary>
    public ToolCallEffectClass EffectClass { get; set; } = ToolCallEffectClass.Unknown;

    /// <summary>Artifact holding the canonical REDACTED arguments. Never inline: large content in a hot row is how this plane falls over, and a tool argument can carry a credential.</summary>
    public Guid? ArgumentsArtifactId { get; set; }

    /// <summary>Canonical lowercase SHA-256 of the referenced argument bytes, under <see cref="WorkflowRunDataContract.Sha256Algorithm"/>. Present exactly when the artifact is.</summary>
    public string? ArgumentsDigest { get; set; }

    /// <summary>
    /// How the referenced argument bytes relate to the wire. NULL is the honest birth state — redaction is a property
    /// of captured bytes, so with no bytes there is none to claim — and it may be filled exactly once. Once stated,
    /// this and the three columns around it are immutable: a fill happens once, and a
    /// <see cref="NativeRecordRedaction.Withheld"/> decision is never quietly upgraded into bytes. What forbids an
    /// undeclared credential-bearing payload is the redaction CHECK, which admits a referenced artifact only under a
    /// stated redaction — never a non-nullability this column deliberately does not have.
    /// </summary>
    public NativeRecordRedaction? ArgumentsRedaction { get; set; }

    /// <summary>The named pass that produced the referenced bytes. Required whenever an artifact is referenced, so a writer that skipped redaction has no legal row to write. It proves a redactor RAN, never that it was correct.</summary>
    public string? RedactionPolicy { get; set; }

    /// <summary>Open, versioned source adapter identity when this row is a projection, e.g. <c>tool-call-ledger/v1</c>. Null together with <see cref="SourceCorrelationId"/> for native producers.</summary>
    public string? SourceKind { get; set; }

    /// <summary>Stable logical identity in <see cref="SourceKind"/>; for <c>tool-call-ledger/v1</c> the ledger row id. Immutable once set, or the same source fact is admissible twice under two identities.</summary>
    public Guid? SourceCorrelationId { get; set; }

    /// <summary>How the observation was obtained, e.g. in-process, harness-native or controlled-proxy.</summary>
    public string CaptureSource { get; set; } = "unknown";

    /// <summary>The shared six-state capture vocabulary applied to the ARGUMENTS; evidence quality, never call success. Mutable while the call is LIVE, so a downgrade found before it closes can still be recorded; the terminal row is frozen entirely, so a corruption discovered after close is a new observation rather than an edit to this one.</summary>
    public WorkflowRunCaptureCompleteness CaptureCompleteness { get; set; } = WorkflowRunCaptureCompleteness.Unavailable;

    public ToolCallState State { get; set; } = ToolCallState.Pending;

    /// <summary>Physical attempts recorded so far. Advanced by the database when an attempt is appended, never by a writer.</summary>
    public int AttemptCount { get; set; }

    /// <summary>The only ordinal the next appended attempt may carry, which is what makes attempt ordinals contiguous from one.</summary>
    public int NextAttemptOrdinal { get; set; } = 1;

    public long Revision { get; set; } = 1;

    /// <summary>The persisted data-contract version, independent of any tool or fabric protocol version.</summary>
    public int SchemaVersion { get; set; } = WorkflowRunDataContract.CurrentVersion;

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset LastModifiedAt { get; set; }

    /// <summary>When this invocation reached a terminal lifecycle state. NULL while live.</summary>
    public DateTimeOffset? TerminalAt { get; set; }

    public string? ErrorCode { get; set; }
    public string? ErrorMessage { get; set; }
    public uint Xmin { get; set; }

    public ICollection<WorkflowRunToolCallAttempt> Attempts { get; set; } = new List<WorkflowRunToolCallAttempt>();
}

/// <summary>Whether a tool could mutate anything. The audit's first question about any recorded call, and the one a boolean cannot answer honestly.</summary>
public enum ToolCallEffectClass
{
    /// <summary>Observing only: re-running it changes nothing outside this system's own telemetry.</summary>
    ReadOnly,

    /// <summary>It can change state outside this system, so a retry is a second effect until proven otherwise.</summary>
    SideEffecting,

    /// <summary>Not established. Retained so it stays visible, and never silently read as either of the above.</summary>
    Unknown,
}

/// <summary>
/// Lifecycle of a logical tool invocation, never the task's verdict. Both terminal states are immutable, and a
/// terminal call has no attempt in flight.
/// </summary>
public enum ToolCallState
{
    /// <summary>The invocation is recorded and no attempt has been dispatched — which makes a tool call that never ran a durable fact rather than a missing row.</summary>
    Pending,

    /// <summary>At least one attempt has been appended.</summary>
    Running,

    /// <summary>Every attempt reached a terminal outcome and the invocation is closed cleanly. Requires at least one attempt, so a call that never ran cannot be closed as a clean one, and refuses to close over an <see cref="ToolCallAttemptStatus.Indeterminate"/> try, so neither can a call whose effect may or may not have landed.</summary>
    Completed,

    /// <summary>No further attempt will be made and the real outcome is unknown. Requires an error code, so an unknown outcome never reads as a clean one.</summary>
    Abandoned,
}
