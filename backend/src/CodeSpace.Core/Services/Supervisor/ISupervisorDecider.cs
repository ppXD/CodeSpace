using CodeSpace.Messages.Agents;

namespace CodeSpace.Core.Services.Supervisor;

/// <summary>
/// The supervisor's decision brain (PR-E E2 seam, Rule 7): given the folded <see cref="SupervisorTurnContext"/>
/// it emits the NEXT <see cref="SupervisorDecision"/>. This is the swap point — E2 ships a deterministic
/// STUB (<c>StubSupervisorDecider</c>); E3 plugs a real model-backed decider (<c>IStructuredLLMClient</c>)
/// behind the SAME interface, so the turn loop, the ledger claim, and the wait machinery never change.
///
/// <para>The decider is PURE + DETERMINISTIC: same context → same decision. It does NOT touch the DB or the
/// ledger (the turn service owns those) and emits a CANONICAL payload (stable bytes → stable idempotency
/// key → exactly-once on replay). It is stateless → registered as a singleton.</para>
/// </summary>
public interface ISupervisorDecider
{
    /// <summary>Emit the next decision for the turn the context describes. Pure: no side effects, deterministic.</summary>
    SupervisorDecision Decide(SupervisorTurnContext context);
}
