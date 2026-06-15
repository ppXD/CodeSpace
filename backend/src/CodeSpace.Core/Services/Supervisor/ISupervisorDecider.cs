using CodeSpace.Messages.Agents;

namespace CodeSpace.Core.Services.Supervisor;

/// <summary>
/// The supervisor's decision brain (PR-E E2 seam, Rule 7): given the folded <see cref="SupervisorTurnContext"/>
/// it emits the NEXT <see cref="SupervisorDecision"/>. This is the swap point — E2 shipped a deterministic
/// STUB (<c>StubSupervisorDecider</c>); E3 plugs the real model-backed <c>LlmSupervisorDecider</c>
/// (<c>IStructuredLLMClient</c> + <see cref="Deciders.SupervisorDecisionSchema"/>) behind the SAME interface,
/// so the turn loop, the ledger claim, and the wait machinery never change.
///
/// <para>The decider emits a CANONICAL payload (stable bytes for a given decision → stable hash). It is the
/// LEDGER, not the decider, that owns exactly-once: the server-derived per-turn idempotency key
/// (<c>turn{N}</c> + canonical payload) + the Pending→Running claim make a replay of an already-settled turn
/// consume the prior outcome rather than re-deciding. So the decider need NOT be deterministic across calls —
/// a real LLM is non-deterministic — only the SERVER-side claim makes the turn exactly-once. The decider does
/// NOT touch the DB or the ledger (the turn service owns those). E3 widens the method to async for the network
/// call to a real model; the stub answers synchronously via <c>Task.FromResult</c>.</para>
/// </summary>
public interface ISupervisorDecider
{
    /// <summary>Emit the next decision for the turn the context describes. No DB / ledger side effects (the turn service owns those).</summary>
    Task<SupervisorDecision> DecideAsync(SupervisorTurnContext context, CancellationToken cancellationToken);
}
