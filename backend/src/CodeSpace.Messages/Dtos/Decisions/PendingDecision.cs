using CodeSpace.Messages.Decisions;

namespace CodeSpace.Messages.Dtos.Decisions;

/// <summary>
/// One PENDING decision in the cross-grain "Needs decision" queue (Decision substrate D3) — the UNIFIED projection over
/// BOTH park backends, team-scoped: an <c>agent.code</c> mid-run <c>decision.request</c> parked as a tool-ledger row,
/// AND a <c>flow.decision</c> node parked as a workflow-run wait. The display fields are projected from the parked
/// <c>DecisionRequest</c> envelope (stashed at park on both backends), so the queue needs no grain-specific knowledge
/// beyond the envelope. A Rule 18.1 pure data noun.
///
/// <para>REDACTION POSTURE differs by grain (this row is a human surface): the AGENT-grain envelope is redacted at park
/// by the run's <c>SecretRedactor</c> (see <c>McpRequestHandler.ParkDecisionAsync</c>). The NODE-grain envelope is the
/// workflow's RESOLVED suspend payload — it inherits the engine's existing suspend-payload posture, which does NOT yet
/// additionally redact (the same plaintext already visible via the run-detail Pending-wait payload), so a workflow
/// author must not place secret refs in human-facing decision text until the engine suspend-payload redaction lands.</para>
/// </summary>
public sealed record PendingDecision
{
    /// <summary>The decision's durable id — the tool-ledger row id (agent grain) or the workflow-run-wait id (node grain). The queue's stable handle.</summary>
    public required Guid Id { get; init; }

    /// <summary>Which park backend holds it — a <see cref="DecisionResumeBackends"/> value (<c>tool_ledger</c> | <c>workflow_wait</c>). Lets the UI pick the right answer affordance.</summary>
    public required string Grain { get; init; }

    /// <summary>The root run that owns the whole tree — the one-query trace key (AC5).</summary>
    public required Guid RootTraceId { get; init; }

    public Guid? WorkflowRunId { get; init; }
    public Guid? AgentRunId { get; init; }
    public string? NodeId { get; init; }

    public required string DecisionType { get; init; }
    public required string Question { get; init; }
    public IReadOnlyList<DecisionOption> Options { get; init; } = Array.Empty<DecisionOption>();
    public string? RecommendedOption { get; init; }
    public string? BlockingReason { get; init; }
    public string? ContextSummary { get; init; }
    public required string RiskLevel { get; init; }
    public required string Policy { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>The bounded-wait deadline — a decision is always bounded (AC4); past it the reaper resolves it, so the queue can show a countdown / expiry risk.</summary>
    public DateTimeOffset? DeadlineAt { get; init; }

    /// <summary>The posted card message an operator answers through (agent grain). NULL for a node-grain decision, which has no chat card yet (answered via the resume API) — surfacing it in the queue is the first step to making it answerable (D3b).</summary>
    public Guid? AnswerMessageId { get; init; }
}
