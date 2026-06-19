namespace CodeSpace.Messages.Decisions;

/// <summary>
/// The typed envelope for a generic, durable DECISION (Rule 18.1 — a pure data noun in Messages). ANY raiser —
/// an <c>agent.code</c> mid-run via an MCP tool, an <c>agent.supervisor</c>, a workflow node (<c>flow.decision</c>),
/// a future native loop — produces this; the substrate uniformly handles park / policy / queue / resume. It is the
/// ONE logical contract carried as the <c>workflow_run_wait.payload_jsonb</c> (node backend) or the tool-ledger row
/// (agent backend) while parked, distinct from chat (chat is a notify channel, never the source of truth).
///
/// <para>The six hard guarantees ride this envelope: <see cref="DedupeKey"/> makes <c>decision.request</c> idempotent
/// (the same key returns the same decision, never a new question — AC1); <see cref="TimeoutAt"/> +
/// <see cref="DefaultAction"/> make it never-hang (mandatory, AC4); <see cref="RootTraceId"/> + the soft-FK ids make
/// the run/node/agent/tool chain a one-line trace (AC5); <see cref="Policy"/> + <see cref="RiskLevel"/> +
/// <see cref="RecommendedOption"/> + <see cref="BlockingReason"/> drive the fail-closed policy floor.</para>
/// </summary>
public sealed record DecisionRequest
{
    /// <summary>The decision's own id (also the human-queue handle).</summary>
    public required Guid Id { get; init; }

    /// <summary>The root run that owns the whole tree — the denormalized key that makes ask → policy → answer → resume one indexed query (AC5). For a top-level workflow run this is the run id; a nested child inherits the root's.</summary>
    public required Guid RootTraceId { get; init; }

    // ── The waiting point (the soft-FK chain — only the relevant ones are set per raiser) ──
    public Guid? WorkflowRunId { get; init; }
    public string? NodeId { get; init; }
    public Guid? AgentRunId { get; init; }
    public string? ToolCallId { get; init; }
    public Guid? SupervisorDecisionId { get; init; }
    public string? PhaseId { get; init; }

    /// <summary>The grain this decision parks at — see <see cref="DecisionScopes"/>.</summary>
    public required string Scope { get; init; }

    /// <summary>Who raised it — see <see cref="DecisionRequesterTypes"/>.</summary>
    public required string RequesterType { get; init; }

    /// <summary>The shape of the ask — see <see cref="DecisionTypes"/>.</summary>
    public required string DecisionType { get; init; }

    /// <summary>The question shown to the answerer.</summary>
    public required string Question { get; init; }

    /// <summary>The selectable options (for confirm / choose_one / choose_many / approve_action). Empty for free_text.</summary>
    public IReadOnlyList<DecisionOption> Options { get; init; } = Array.Empty<DecisionOption>();

    /// <summary>The raiser's recommended option id — REQUIRED for any auto-answerable request (the floor rejects auto-answer without one).</summary>
    public string? RecommendedOption { get; init; }

    /// <summary>Why the raiser is blocked (the context an arbiter / human needs) — REQUIRED for any auto-answerable request.</summary>
    public string? BlockingReason { get; init; }

    /// <summary>A short, self-contained summary of the situation (so the answerer needn't read the whole run).</summary>
    public string? ContextSummary { get; init; }

    /// <summary>Optional JSON Schema the free-text / structured answer must conform to.</summary>
    public string? AnswerSchema { get; init; }

    /// <summary>The raiser-declared risk — see <see cref="DecisionRiskLevels"/>. The server floor can only raise it.</summary>
    public required string RiskLevel { get; init; }

    /// <summary>Who may answer — see <see cref="DecisionPolicies"/>. Clamped by the server-side fail-closed floor.</summary>
    public required string Policy { get; init; }

    /// <summary>Optional minimum confidence (0..1) an arbiter must clear to auto-answer; below it → escalate.</summary>
    public double? ConfidenceRequired { get; init; }

    /// <summary>The option id (or sentinel) applied on timeout — the never-hang default. Null ⇒ the timeout surfaces a <c>_timedOut</c> answer the downstream must handle.</summary>
    public string? DefaultAction { get; init; }

    /// <summary>The mandatory deadline — a decision can never hang forever (AC4). The bounded wait applies <see cref="DefaultAction"/> when it passes.</summary>
    public required DateTimeOffset TimeoutAt { get; init; }

    /// <summary>The idempotency key: a re-raise with the same key returns the SAME decision, never a new question (AC1). Maps to the durable unique index of the chosen backend.</summary>
    public required string DedupeKey { get; init; }

    /// <summary>Which durable park backend holds the suspension — see <see cref="DecisionResumeBackends"/>.</summary>
    public required string ResumeBackend { get; init; }

    /// <summary>The opaque correlation token the answer presents to resolve exactly this waiting point.</summary>
    public string? ResumeToken { get; init; }

    /// <summary>Lifecycle — see <see cref="DecisionStatuses"/>. Pending while parked.</summary>
    public string Status { get; init; } = DecisionStatuses.Pending;
}

/// <summary>One selectable option on a <see cref="DecisionRequest"/> — a stable id + a human label, optionally flagged as a side-effecting / irreversible choice (which the policy floor treats as human-required).</summary>
public sealed record DecisionOption
{
    public required string Id { get; init; }

    public required string Label { get; init; }

    /// <summary>True if choosing this has an irreversible / side-effecting outcome — the floor forbids auto-answering it.</summary>
    public bool IsSideEffecting { get; init; }
}
