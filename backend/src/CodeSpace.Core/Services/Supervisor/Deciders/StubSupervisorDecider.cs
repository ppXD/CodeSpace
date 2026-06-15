using System.Text.Json;
using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Messages.Agents;

namespace CodeSpace.Core.Services.Supervisor.Deciders;

/// <summary>
/// The E2 deterministic STUB decider (Rule 18.3: an <see cref="ISupervisorDecider"/> impl in the
/// <c>Deciders/</c> variant sub-folder). It emits a FIXED, replayable script so the turn loop + ledger +
/// wait machinery can be proven end-to-end WITHOUT a real model: turn 1 → <c>plan</c> (a fixed planned
/// subtask list), every later turn → <c>stop</c>. Pure + stateless (singleton); reads ONLY the context's
/// <see cref="SupervisorTurnContext.TurnNumber"/> so the same turn always yields the same decision (the
/// exactly-once-on-replay property the ledger relies on).
///
/// <para>E3 replaces THIS class with a real model-backed decider behind the same interface — the turn loop
/// never changes.</para>
/// </summary>
public sealed class StubSupervisorDecider : ISupervisorDecider, ISingletonDependency
{
    /// <summary>The fixed subtasks turn 1's <c>plan</c> records — a stable list so the payload (and thus the idempotency key) is deterministic.</summary>
    public static readonly IReadOnlyList<string> StubPlannedSubtasks = new[] { "subtask-1", "subtask-2" };

    public SupervisorDecision Decide(SupervisorTurnContext context)
    {
        // Turn 0 (the first turn) plans; every later turn stops. Deterministic in TurnNumber alone.
        if (context.TurnNumber == 0) return Plan();

        return Stop("plan complete");
    }

    private static SupervisorDecision Plan() => new()
    {
        Kind = SupervisorDecisionKinds.Plan,
        PayloadJson = JsonSerializer.Serialize(new { subtasks = StubPlannedSubtasks }, AgentJson.Options),
    };

    private static SupervisorDecision Stop(string reason) => new()
    {
        Kind = SupervisorDecisionKinds.Stop,
        PayloadJson = JsonSerializer.Serialize(new { reason }, AgentJson.Options),
    };
}
