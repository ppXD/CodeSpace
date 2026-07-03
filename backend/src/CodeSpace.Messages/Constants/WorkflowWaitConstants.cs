namespace CodeSpace.Messages.Constants;

/// <summary>
/// How a suspended run will be woken. Stored as <c>workflow_run_wait.wait_kind</c> (CHECK-
/// constrained at the DB layer) and carried as <c>SuspensionToken.Kind</c> from the node.
/// </summary>
public static class WorkflowWaitKinds
{
    /// <summary>Self-waking after a delay — the engine schedules a resume at <c>wake_at</c>.</summary>
    public const string Timer = "Timer";

    /// <summary>Waits for a human to approve/reject via the API + UI (Phase 1.2).</summary>
    public const string Approval = "Approval";

    /// <summary>Waits for an external system to POST to a tokened callback URL (Phase 1.2).</summary>
    public const string Callback = "Callback";

    /// <summary>
    /// Waits for a child workflow run to finish (Phase 3 — <c>flow.subworkflow</c>). The wait's
    /// <c>Token</c> is the child run's id; the engine resumes the parent when the child reaches a
    /// terminal state, mapping the child's outputs back onto the node.
    /// </summary>
    public const string Subworkflow = "Subworkflow";

    /// <summary>
    /// Waits for a person to act on an interactive chat affordance (a card button — approve /
    /// request-changes / …). The structured sibling of <see cref="Callback"/>: the click resolves
    /// the wait with a <c>{ action, by, comment }</c> payload (vs the callback's opaque body) and
    /// is woken by an authenticated, team-scoped resume rather than an anonymous URL.
    /// </summary>
    public const string Action = "Action";

    /// <summary>
    /// Waits for an agent run (<c>agent.code</c>) to reach a terminal state. The wait's <c>Token</c> is
    /// the agent-run id; the executor's completion resumes the node with the <c>AgentRunResult</c>
    /// mapped onto <c>{ status, summary, changedFiles, branch, error }</c>.
    /// </summary>
    public const string AgentRun = "AgentRun";

    /// <summary>
    /// A <c>agent.supervisor</c> turn parking itself to advance to the NEXT turn (PR-E E2). Unlike every
    /// other wait kind this has NO external work item to wait on — it is a SELF-ADVANCE: the engine, once
    /// the run commits Suspended, durably schedules the run to resume (resolve this wait + re-dispatch) so
    /// the supervisor node re-enters and emits its next decision from the durable ledger. The wait's
    /// <c>Token</c> is a per-turn marker; the per-turn <c>IterationKey</c> (<c>&lt;nodeId&gt;#turn{N}</c>)
    /// keeps each turn's row distinct. Survives a restart via the reconciler's supervisor self-advance sweep.
    /// </summary>
    public const string SupervisorDecision = "SupervisorDecision";

    /// <summary>
    /// A <c>agent.supervisor</c> turn that PARKED ON the K real <c>AgentRun</c> waits a spawn/retry staged
    /// (PR-E E3). A SUSPEND MARKER, NOT a wait the engine stages: the executor already created the K
    /// <see cref="AgentRun"/> waits (keyed <c>&lt;nodeId&gt;#turn{N}#{k}</c>), so the engine, on seeing this
    /// kind, records node.suspended + flips the run Suspended WITHOUT adding another wait row and WITHOUT
    /// scheduling a self-advance. The wait-for-all barrier (the agents' completion notifier resolving each
    /// AgentRun wait → re-dispatch once the last completes) drives the supervisor's next turn — the agent
    /// completions, not a self-advance, resume it. The per-turn <c>IterationKey</c> keeps the marker row
    /// distinct from a later turn's.
    /// </summary>
    public const string SupervisorAgentWaits = "SupervisorAgentWaits";

    /// <summary>
    /// A generic DECISION the run is parked on (the durable Decision substrate, D1). The node raised a typed
    /// <c>DecisionRequest</c> and suspends until it is answered — by a human (the resume API / the "Needs decision"
    /// queue), a policy auto-answer, a supervisor arbiter, or the bounded-wait deadline applying the default. The wait's
    /// <c>Payload</c> carries the <c>DecisionRequest</c> envelope while parked, then the <c>DecisionAnswer</c> on
    /// resolve (overwrite-on-resolve, same as every other wait). Always BOUNDED (a mandatory <c>DeadlineAt</c> +
    /// default-on-timeout) so a decision can never hang forever. The structured, policy-gated sibling of
    /// <see cref="Approval"/> — that is the binary special case (<c>decisionType=approve_action</c>).
    /// </summary>
    public const string Decision = "Decision";

    /// <summary>
    /// The wait kinds an operator may FORCE-REISSUE via the reissue verb — the SIGNAL-driven waits that can strand with
    /// no backstop: a <see cref="Timer"/> whose scheduled wake was dropped (a lost Hangfire job — there is no reconciler
    /// sweep for it, unlike <see cref="SupervisorDecision"/>), and a <see cref="Callback"/> whose external system never
    /// posts. Everything else is deliberately EXCLUDED: the decision-driven waits (<see cref="Approval"/> /
    /// <see cref="Action"/> / <see cref="Decision"/>) carry a human decision and resolve via their own verbs (a blind
    /// reissue would feed the node a decision-less payload); the completion-driven waits (<see cref="Subworkflow"/> /
    /// <see cref="AgentRun"/>) and the supervisor self-waits resolve only when their real work completes — faking their
    /// result payload here would corrupt the node — so none is operator-reissuable. (All of them ALSO have a reconciler
    /// backstop that re-fires the real completion: <see cref="AgentRun"/> + the supervisor self-waits, and — closing the
    /// last un-backstopped strand — a stranded <see cref="Subworkflow"/> parent whose child already went terminal.)
    /// Pinned by a unit test so widening the set is a conscious, reviewed decision (Rule 8).
    /// </summary>
    public static bool IsOperatorReissuable(string waitKind) => waitKind is Timer or Callback;
}

/// <summary>Lifecycle of a <c>workflow_run_wait</c> row. CHECK-constrained at the DB layer.</summary>
public static class WorkflowWaitStatuses
{
    public const string Pending = "Pending";
    public const string Resolved = "Resolved";
}
