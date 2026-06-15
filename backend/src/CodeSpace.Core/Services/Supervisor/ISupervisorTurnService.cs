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
    /// <summary>Fold the run's decision ledger (team-scoped, <c>Sequence</c> order) into a turn context: terminal decisions replayed (outcome only), the one in-flight decision identified, <c>TurnNumber</c> = decided count. Pure-ish (reads the ledger, no writes).</summary>
    Task<SupervisorTurnContext> RehydrateFromDecisionLogAsync(Guid supervisorRunId, Guid teamId, string goal, CancellationToken cancellationToken);

    /// <summary>Run one turn for the run: rehydrate → budget → decide → claim + execute exactly-once → record terminal → return finish/park. The exactly-once decision step.</summary>
    Task<SupervisorTurnResult> RunTurnAsync(Guid supervisorRunId, Guid teamId, string goal, CancellationToken cancellationToken);
}
