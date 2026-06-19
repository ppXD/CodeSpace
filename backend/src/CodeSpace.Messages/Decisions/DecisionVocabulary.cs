namespace CodeSpace.Messages.Decisions;

/// <summary>
/// The shape of decision being asked. Open string vocabulary (forward-compatible across serialized envelopes,
/// same convention as <c>WorkflowWaitKinds</c>) — a new kind never breaks an old persisted <c>DecisionRequest</c>.
/// </summary>
public static class DecisionTypes
{
    /// <summary>A yes/no — the answer is a boolean.</summary>
    public const string Confirm = "confirm";

    /// <summary>Pick exactly ONE of <see cref="DecisionRequest.Options"/>.</summary>
    public const string ChooseOne = "choose_one";

    /// <summary>Pick ANY subset of <see cref="DecisionRequest.Options"/>.</summary>
    public const string ChooseMany = "choose_many";

    /// <summary>An open-text answer (optionally schema-constrained by <see cref="DecisionRequest.AnswerSchema"/>).</summary>
    public const string FreeText = "free_text";

    /// <summary>A permission gate (approve / reject) — the binary special case the MCP + workflow approvals fold into. Always hard-floored to human.</summary>
    public const string ApproveAction = "approve_action";
}

/// <summary>Who is allowed to answer a decision — the policy LADDER (auto → supervisor → human). The server-side floor can only raise the bar, never lower it (a high-risk / irreversible / un-recommended request is forced to human regardless of what the raiser declares).</summary>
public static class DecisionPolicies
{
    /// <summary>A deterministic policy may auto-answer trivially-safe decisions WITHOUT a supervisor or human (works for a standalone agent).</summary>
    public const string AutoAllowed = "auto_allowed";

    /// <summary>A supervisor arbiter tries to answer first (confident + within the risk floor); escalates to a human otherwise.</summary>
    public const string SupervisorFirst = "supervisor_first";

    /// <summary>Always goes to the human decision queue — no auto / supervisor answer.</summary>
    public const string HumanRequired = "human_required";
}

/// <summary>The raiser-declared risk. The server floor treats <see cref="High"/> (and any irreversible/side-effecting type) as human-required regardless.</summary>
public static class DecisionRiskLevels
{
    public const string Low = "low";
    public const string Medium = "medium";
    public const string High = "high";
}

/// <summary>The grain a decision is scoped to — the waiting point it parks.</summary>
public static class DecisionScopes
{
    public const string Run = "run";
    public const string Node = "node";
    public const string Agent = "agent";
    public const string Tool = "tool";
}

/// <summary>Who produced the <see cref="DecisionRequest"/>.</summary>
public static class DecisionRequesterTypes
{
    public const string Agent = "agent";
    public const string Supervisor = "supervisor";
    public const string Tool = "tool";
    public const string WorkflowNode = "workflow_node";
}

/// <summary>Which durable park backend holds the raiser's suspension — so resume targets the right exact-point mechanism.</summary>
public static class DecisionResumeBackends
{
    /// <summary>Node-grain: a <c>WorkflowRunWait</c> of kind <c>Decision</c> (resume via ledger rehydration).</summary>
    public const string WorkflowWait = "workflow_wait";

    /// <summary>Agent-mid-run: a <c>ToolCallLedger</c> row + the in-memory waiter (the CLI blocks on the MCP tool call).</summary>
    public const string ToolLedger = "tool_ledger";

    /// <summary>A future native-loop / detached agent session.</summary>
    public const string AgentSession = "agent_session";
}

/// <summary>Lifecycle of a decision.</summary>
public static class DecisionStatuses
{
    public const string Pending = "pending";
    public const string Answered = "answered";
    public const string Escalated = "escalated";
    public const string Expired = "expired";
    public const string Cancelled = "cancelled";
}

/// <summary>Who answered a resolved decision (audit + the rationale source).</summary>
public static class DecisionAnsweredByKinds
{
    public const string Policy = "policy";
    public const string Supervisor = "supervisor";
    public const string Human = "human";

    /// <summary>The bounded-wait deadline applied the default — no one answered in time.</summary>
    public const string Timeout = "timeout";
}
