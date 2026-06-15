using System.Text.Json;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Messages.Agents;

namespace CodeSpace.Core.Services.Supervisor.Deciders;

/// <summary>
/// A deterministic TEST decider (Rule 18.3: an <see cref="ISupervisorDecider"/> impl in the <c>Deciders/</c>
/// variant sub-folder). NOT auto-registered — E3 made <c>LlmSupervisorDecider</c> the production decider, so
/// this stub carries NO DI marker and serves ONLY tests that want a fixed, model-free script: turn 0 →
/// <c>plan</c> (a fixed planned subtask list), every later turn → <c>stop</c>. Deterministic in
/// <see cref="SupervisorTurnContext.TurnNumber"/> alone (the exactly-once-on-replay property the ledger relies
/// on), answering synchronously via <c>Task.FromResult</c>.
/// </summary>
public sealed class StubSupervisorDecider : ISupervisorDecider
{
    /// <summary>The fixed subtasks turn 0's <c>plan</c> records — a stable list so the payload (and thus the idempotency key) is deterministic.</summary>
    public static readonly IReadOnlyList<string> StubPlannedSubtasks = new[] { "subtask-1", "subtask-2" };

    public Task<SupervisorDecision> DecideAsync(SupervisorTurnContext context, CancellationToken cancellationToken)
    {
        // Turn 0 (the first turn) plans; every later turn stops. Deterministic in TurnNumber alone.
        var decision = context.TurnNumber == 0 ? Plan() : Stop("plan complete");

        return Task.FromResult(decision);
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
