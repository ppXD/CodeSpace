using System.Text.Json;
using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Messages.Agents;

namespace CodeSpace.Core.Services.Supervisor.Executors;

/// <summary>
/// The E2 STUB action executor (Rule 18.3: an <see cref="ISupervisorActionExecutor"/> impl in the
/// <c>Executors/</c> variant sub-folder). It performs NO real side effect — it just produces a deterministic
/// outcome JSON per decision kind so the exactly-once claim hop + the terminal record can be proven without
/// spawning agents: <c>plan</c> → a fixed planned-subtask-list outcome, <c>stop</c> → a completion marker.
/// Pure + stateless (singleton). E3 replaces THIS class with real executors (spawn agent.code child, fan
/// out subtasks) behind the same interface.
/// </summary>
public sealed class StubSupervisorActionExecutor : ISupervisorActionExecutor, ISingletonDependency
{
    public Task<string> ExecuteAsync(SupervisorDecision decision, SupervisorTurnContext context, CancellationToken cancellationToken)
    {
        var outcome = decision.Kind switch
        {
            SupervisorDecisionKinds.Plan => JsonSerializer.Serialize(new { planned = StubSupervisorDeciderSubtasks(), stub = true }, AgentJson.Options),
            SupervisorDecisionKinds.Stop => JsonSerializer.Serialize(new { stopped = true, stub = true }, AgentJson.Options),
            _ => JsonSerializer.Serialize(new { stub = true }, AgentJson.Options),
        };

        return Task.FromResult(outcome);
    }

    // The stub plan's recorded outcome mirrors the decider's fixed subtasks, so the replay tape is legible.
    private static IReadOnlyList<string> StubSupervisorDeciderSubtasks() => Deciders.StubSupervisorDecider.StubPlannedSubtasks;
}
