using CodeSpace.Messages.Agents;

namespace CodeSpace.Core.Services.Supervisor;

/// <summary>
/// Runs ONE supervisor turn (PR-E E2) — the durable, exactly-once decision step the <c>agent.supervisor</c>
/// node delegates to (Rule 16: the node stays a thin shell; this scoped service owns the ledger + the
/// decider + the executor). A turn:
/// <list type="number">
///   <item>REHYDRATES the run's decision tape from the durable ledger (<see cref="RehydrateFromDecisionLogAsync"/>)
///         — terminal decisions replayed (outcome only), the one in-flight decision identified;</item>
///   <item>BUDGET-gates fail-closed: if the decided-decision count meets <c>SupervisorLane.DecisionBudget</c>,
///         forces a terminal <c>stop</c> ("budget exhausted") rather than asking the decider;</item>
///   <item>otherwise asks the injected <see cref="ISupervisorDecider"/> for the next decision;</item>
///   <item>CLAIMS it exactly-once (server-derived per-turn idempotency key → <c>TryClaimAsync</c> →
///         <c>TryBeginExecutionAsync</c>), runs the <see cref="ISupervisorActionExecutor"/> side effect ONCE,
///         records the terminal — or, on a replay/duplicate, consumes the prior outcome WITHOUT re-running;</item>
///   <item>returns a <see cref="SupervisorTurnResult"/> telling the node to FINISH (terminal) or PARK
///         (carry the next-turn context onto the self-advance wait).</item>
/// </list>
/// Idempotent under replay: a forced restart mid-turn re-enters here, the rehydrate replays the settled
/// decisions (no double side effect), and the in-flight/next decision continues from the durable ledger.
/// </summary>
public interface ISupervisorTurnService
{
    /// <summary>Fold the run's decision ledger (team-scoped, <c>Sequence</c> order) into a turn context: terminal decisions replayed (outcome only), the one in-flight decision identified, <c>TurnNumber</c> = decided count. <paramref name="nodeId"/> stamps the per-turn-per-spawn AgentRun wait key (<c>&lt;nodeId&gt;#turn{N}#{k}</c>). Pure-ish (reads the ledger, no writes).</summary>
    Task<SupervisorTurnContext> RehydrateFromDecisionLogAsync(Guid supervisorRunId, Guid teamId, string nodeId, string goal, CancellationToken cancellationToken);

    /// <summary>Run one turn for the run: rehydrate → budget → decide → claim + execute exactly-once → record terminal → return finish / self-advance / park-on-agent-waits / park-on-human. The exactly-once decision step. <paramref name="conversationId"/> is the run's own team conversation an ask_human turn posts its question card into (null = no HITL surface authored → ask_human degrades to a no-surface self-advance).</summary>
    Task<SupervisorTurnResult> RunTurnAsync(Guid supervisorRunId, Guid teamId, string nodeId, string goal, Guid? conversationId, CancellationToken cancellationToken);

    /// <summary>
    /// Count the run's still-PENDING <c>AgentRun</c> waits a PRIOR async turn (spawn/retry) staged for this
    /// node. Read FIRST on re-entry: a restart between staging the agents + their completion would otherwise let
    /// the rehydrate advance past the (already-terminal) spawn decision and skip ahead — but those agents are
    /// still running, so the node must re-park on THEM, never advance. &gt; 0 → re-suspend on the existing waits;
    /// 0 → no in-flight async turn, run the next turn normally.
    /// </summary>
    Task<int> CountPendingAgentWaitsAsync(Guid supervisorRunId, string nodeId, CancellationToken cancellationToken);

    /// <summary>
    /// The correlation token of the run's still-PENDING ask_human <c>Action</c> wait this node parked on
    /// (null when none). The HUMAN-park analogue of <see cref="CountPendingAgentWaitsAsync"/>: an ask_human
    /// decision is a SETTLED ledger row, so a restart-while-parked would otherwise let the rehydrate advance
    /// past it — but the human hasn't answered, so the node must re-park on the EXISTING Action wait, never
    /// advance + never re-post the question. Non-null → re-suspend on that token; null → no parked question.
    /// </summary>
    Task<string?> PendingHumanWaitTokenAsync(Guid supervisorRunId, string nodeId, CancellationToken cancellationToken);
}
