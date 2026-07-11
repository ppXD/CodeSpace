using CodeSpace.Messages.Agents;

namespace CodeSpace.Core.Services.Supervisor;

/// <summary>
/// Executes the SIDE EFFECT of a claimed supervisor decision (PR-E E2 seam, Rule 7) — the action half of a
/// turn, run EXACTLY ONCE behind the ledger's Pending → Running claim. E2 shipped a STUB; E3 swaps in the
/// real <c>RealSupervisorActionExecutor</c> behind the SAME interface (spawn fans out real agent.run child
/// runs, plan calls the real planner, merge synthesizes prior results), so the turn loop + claim hop never
/// change.
///
/// <para>Pure-of-ledger: the executor performs the action and returns a <see cref="SupervisorExecution"/>
/// (the outcome JSON recorded as the ledger row's terminal outcome + the DUAL-PATH classification the node
/// reads to decide how to suspend). The turn service owns the claim + the terminal record. SYNCHRONOUS verbs
/// (plan / merge / stop / ask_human) settle in-process (<see cref="SupervisorExecution.ParkedAgentWaitCount"/>
/// == 0 → the node self-advances or finishes); ASYNC verbs (spawn / retry) stage K real <c>AgentRun</c>
/// <c>WorkflowRunWait</c> rows themselves (count &gt; 0 → the node parks on THOSE waits, the barrier resumes
/// it once all K finish). The real executor is scoped (it touches the DB + the agent-run service).</para>
/// </summary>
public interface ISupervisorActionExecutor
{
    /// <summary>Run the decision's side effect and return its outcome + suspend classification. Called ONCE per decision, after the caller won the Pending → Running claim.</summary>
    Task<SupervisorExecution> ExecuteAsync(SupervisorDecision decision, SupervisorTurnContext context, CancellationToken cancellationToken);
}

/// <summary>
/// What executing one decision resolved to (PR-E E3/E4 — a data noun): the terminal outcome JSON (recorded on
/// the ledger row) plus the suspend classification (the THREE resume paths, E4):
/// <list type="bullet">
///   <item>SYNCHRONOUS (<see cref="ParkedAgentWaitCount"/> == 0 &amp;&amp; <see cref="HumanWaitToken"/> == null) —
///         the node self-advances to the next turn (plan / merge) or finishes (stop).</item>
///   <item>PARK-ON-AGENTS (<see cref="ParkedAgentWaitCount"/> &gt; 0) — the executor staged that many real
///         <c>AgentRun</c> waits (spawn / retry); the node SUSPENDS on them and the wait-for-all barrier
///         (resumed by the agents' completion) drives the next turn.</item>
///   <item>PARK-ON-HUMAN (<see cref="HumanWaitToken"/> != null) — the executor posted a question card carrying
///         that correlation token (ask_human); the node SUSPENDS on a SINGLE <c>Action</c> wait keyed to the
///         token, and the human's answer (the existing <c>ResumeByActionTokenAsync</c> path) resumes the turn.</item>
/// </list>
/// </summary>
public sealed record SupervisorExecution
{
    /// <summary>The terminal outcome JSON recorded as the decision's ledger outcome (e.g. the planned subtasks, the spawned agent-run ids, the merge synthesis, the ask_human question + token, the stop summary).</summary>
    public required string OutcomeJson { get; init; }

    /// <summary>How many real <c>AgentRun</c> waits the executor staged for this decision. 0 = synchronous (self-advance / finish); &gt; 0 = async (the node parks on these waits; the barrier resumes once all complete).</summary>
    public int ParkedAgentWaitCount { get; init; }

    /// <summary>The correlation token of the SINGLE <c>Action</c> wait an ask_human turn posted its question card on (E4). Non-null ⇒ the node parks on the human's answer (one answer resumes the turn — NOT the wait-for-all barrier). Null for every other verb.</summary>
    public string? HumanWaitToken { get; init; }

    /// <summary>A synchronous execution — the node self-advances (plan / merge) or finishes (stop). The common shape for the in-process verbs.</summary>
    public static SupervisorExecution Synchronous(string outcomeJson) => new() { OutcomeJson = outcomeJson, ParkedAgentWaitCount = 0 };

    /// <summary>An async execution that staged <paramref name="agentWaitCount"/> real AgentRun waits — the node parks on them (the barrier drives the resume).</summary>
    public static SupervisorExecution ParkedOnAgents(string outcomeJson, int agentWaitCount) => new() { OutcomeJson = outcomeJson, ParkedAgentWaitCount = agentWaitCount };

    /// <summary>An ask_human execution that posted a question card on the Action wait <paramref name="humanWaitToken"/> — the node parks on the SINGLE human answer (E4).</summary>
    public static SupervisorExecution ParkedOnHuman(string outcomeJson, string humanWaitToken) => new() { OutcomeJson = outcomeJson, ParkedAgentWaitCount = 0, HumanWaitToken = humanWaitToken };
}
