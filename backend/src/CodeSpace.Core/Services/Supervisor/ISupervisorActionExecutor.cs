using CodeSpace.Messages.Agents;

namespace CodeSpace.Core.Services.Supervisor;

/// <summary>
/// Executes the SIDE EFFECT of a claimed supervisor decision (PR-E E2 seam, Rule 7) — the action half of a
/// turn, run EXACTLY ONCE behind the ledger's Pending → Running claim. E2 ships a STUB
/// (<c>StubSupervisorActionExecutor</c>: plan → record a fixed planned-list outcome; stop → a completion
/// marker — neither touches the real plan/spawn machinery). E3 swaps in the real executors (spawn an
/// agent.code child, fan out subtasks, …) behind the SAME interface, so the turn loop + claim hop never
/// change.
///
/// <para>Pure-of-ledger: the executor performs the action and returns the outcome JSON; the turn service
/// owns the claim + the terminal record. Deterministic for the stub (same decision → same outcome). The
/// stub is stateless → a singleton; E3's real executors pick their own lifetime.</para>
/// </summary>
public interface ISupervisorActionExecutor
{
    /// <summary>Run the decision's side effect and return its outcome JSON (recorded as the ledger row's terminal outcome). Called ONCE per decision, after the caller won the Pending → Running claim.</summary>
    Task<string> ExecuteAsync(SupervisorDecision decision, SupervisorTurnContext context, CancellationToken cancellationToken);
}
