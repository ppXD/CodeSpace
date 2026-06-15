namespace CodeSpace.Messages.Agents;

/// <summary>
/// The decision vocabulary a supervisor turn can emit. <see cref="SupervisorDecisionRecord"/>'s
/// <c>DecisionKind</c> is an OPEN string (a new verb adds zero schema churn), so these are CONVENTION
/// constants — the values a decider may pick from — not a closed enum. E2 shipped the minimal two-verb
/// vocabulary (<see cref="Plan"/> / <see cref="Stop"/>); E3 widens it to the full six
/// (plan · spawn · retry · ask_human · merge · stop) without touching the ledger or the wait machinery.
///
/// <para>The verbs split into TWO execution shapes (the dual resume path): SYNCHRONOUS verbs
/// (<see cref="Plan"/> / <see cref="Merge"/> / <see cref="Stop"/>) settle in-process and the node
/// self-advances (or finishes, for stop); ASYNC verbs (<see cref="Spawn"/> / <see cref="Retry"/>) stage K
/// real <c>AgentRun</c> waits and the node parks on THEM (the wait-for-all barrier resumes the supervisor
/// once every spawned agent terminates). <see cref="AskHuman"/> is recognised by the decider in E3 but the
/// executor degrades to a clean "not supported until E4" outcome (real HITL parks in E4).</para>
/// </summary>
public static class SupervisorDecisionKinds
{
    /// <summary>Decompose the goal into subtasks. SYNCHRONOUS — the executor folds the plan + the node self-advances.</summary>
    public const string Plan = "plan";

    /// <summary>Fan out K real <c>agent.code</c> child runs over prior-plan subtask ids. ASYNC — the executor stages K AgentRun waits keyed <c>&lt;nodeId&gt;#turn{N}#{k}</c> + the node parks; the barrier resumes once all K finish.</summary>
    public const string Spawn = "spawn";

    /// <summary>Re-run ONE prior subtask as a FRESH agent run (a new Attempt), optionally with a revised instruction. ASYNC — same stage-K-waits + barrier as <see cref="Spawn"/> (here K=1).</summary>
    public const string Retry = "retry";

    /// <summary>Ask a human a question. PARKED for E4 — the decider MAY emit it; in E3 the executor returns a clean "not supported until E4" outcome rather than crashing.</summary>
    public const string AskHuman = "ask_human";

    /// <summary>Synthesize the recorded prior-Attempt agent results into one outcome. SYNCHRONOUS — a thin reduce over the prior agent runs' <c>ResultJson</c>; the node self-advances.</summary>
    public const string Merge = "merge";

    /// <summary>Terminate the supervisor turn loop — the run completes via the normal walk. The fail-closed budget-exhaustion verb too.</summary>
    public const string Stop = "stop";
}
