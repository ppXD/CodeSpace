using System.Text.Json;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Messages.Agents;

namespace CodeSpace.Core.Services.Supervisor.Executors;

/// <summary>
/// A deterministic TEST action executor (Rule 18.3: an <see cref="ISupervisorActionExecutor"/> impl in the
/// <c>Executors/</c> variant sub-folder). NOT auto-registered — E3 made <c>RealSupervisorActionExecutor</c>
/// the production executor, so this stub carries NO DI marker and serves ONLY unit tests that want a no-side-
/// effect executor: <c>plan</c> → a fixed planned-subtask-list outcome, <c>stop</c> → a completion marker.
/// Every outcome is SYNCHRONOUS (no staged agent waits), so a test driving it sees the self-advance path.
/// </summary>
public sealed class StubSupervisorActionExecutor : ISupervisorActionExecutor
{
    public Task<SupervisorExecution> ExecuteAsync(SupervisorDecision decision, SupervisorTurnContext context, CancellationToken cancellationToken)
    {
        var outcome = decision.Kind switch
        {
            SupervisorDecisionKinds.Plan => JsonSerializer.Serialize(new { planned = StubSupervisorDeciderSubtasks(), stub = true }, AgentJson.Options),
            SupervisorDecisionKinds.Stop => JsonSerializer.Serialize(new { stopped = true, stub = true }, AgentJson.Options),
            _ => JsonSerializer.Serialize(new { stub = true }, AgentJson.Options),
        };

        return Task.FromResult(SupervisorExecution.Synchronous(outcome));
    }

    // The stub plan's recorded outcome mirrors the decider's fixed subtasks, so the replay tape is legible.
    private static IReadOnlyList<string> StubSupervisorDeciderSubtasks() => Deciders.StubSupervisorDecider.StubPlannedSubtasks;
}
