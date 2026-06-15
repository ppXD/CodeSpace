using System.Text.Json;
using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Messages.Agents;
using Microsoft.Extensions.Logging;

namespace CodeSpace.Core.Services.Supervisor.Executors;

/// <summary>
/// The PR-E E3 REAL action executor (Rule 18.3 — an <see cref="ISupervisorActionExecutor"/> impl in the
/// <c>Executors/</c> variant folder), replacing the E2 stub behind the SAME seam. It drives each decision's
/// real side effect + returns a <see cref="SupervisorExecution"/> classifying the suspend path (the dual
/// resume path):
/// <list type="bullet">
///   <item><c>plan</c> → fold the decider's planned subtasks into the recorded outcome (the decider IS the
///         planner-in-the-loop; the executor records the plan so later spawn/merge can read it). SYNCHRONOUS.</item>
///   <item><c>spawn</c> / <c>retry</c> → create K real <c>agent.code</c> child runs (through the admission
///         gate) + stage K <c>AgentRun</c> waits keyed <c>&lt;nodeId&gt;#turn{N}#{k}</c>; the node parks on
///         them + the wait-for-all barrier resumes once all complete. ASYNC (see <c>.Spawn.cs</c>).</item>
///   <item><c>merge</c> → read the recorded prior-Attempt agent results by id + reduce them into a synthesis
///         outcome. SYNCHRONOUS (see <c>.Merge.cs</c>).</item>
///   <item><c>ask_human</c> → a clean "not supported until E4" outcome (the decider may emit it; the executor
///         degrades gracefully — real HITL parks in E4). SYNCHRONOUS.</item>
///   <item><c>stop</c> → a terminal marker (the turn service finishes the loop; this never re-suspends).</item>
/// </list>
///
/// <para>Scoped (Rule 16 — it owns its DB writes via the agent-run service + the wait staging). Exactly-once
/// is the turn service's claim hop's job (Pending→Running before this runs once); the executor's spawn staging
/// records the agent-run ids in the outcome so a replay re-derives the SAME park classification WITHOUT
/// re-staging.</para>
/// </summary>
public sealed partial class RealSupervisorActionExecutor : ISupervisorActionExecutor, IScopedDependency
{
    private readonly CodeSpaceDbContext _db;
    private readonly IAgentRunService _agentRuns;
    private readonly ILogger<RealSupervisorActionExecutor> _logger;

    public RealSupervisorActionExecutor(CodeSpaceDbContext db, IAgentRunService agentRuns, ILogger<RealSupervisorActionExecutor> logger)
    {
        _db = db;
        _agentRuns = agentRuns;
        _logger = logger;
    }

    public Task<SupervisorExecution> ExecuteAsync(SupervisorDecision decision, SupervisorTurnContext context, CancellationToken cancellationToken) => decision.Kind switch
    {
        SupervisorDecisionKinds.Plan => Task.FromResult(ExecutePlan(decision)),
        SupervisorDecisionKinds.Spawn => ExecuteSpawnAsync(decision, context, cancellationToken),
        SupervisorDecisionKinds.Retry => ExecuteRetryAsync(decision, context, cancellationToken),
        SupervisorDecisionKinds.Merge => Task.FromResult(ExecuteMerge(decision, context)),
        SupervisorDecisionKinds.AskHuman => Task.FromResult(ExecuteAskHuman()),
        SupervisorDecisionKinds.Stop => Task.FromResult(ExecuteStop(decision)),
        _ => Task.FromResult(SupervisorExecution.Synchronous(JsonSerializer.Serialize(new { unsupported = decision.Kind }, AgentJson.Options))),
    };

    /// <summary>Record the decider's planned subtasks as the decision outcome (the spawn/merge read them back). The decider already produced the plan — the executor does not re-plan. SYNCHRONOUS → self-advance.</summary>
    private SupervisorExecution ExecutePlan(SupervisorDecision decision)
    {
        var plan = Deserialize<SupervisorPlanPayload>(decision.PayloadJson) ?? new SupervisorPlanPayload();

        var outcome = JsonSerializer.Serialize(new { planned = plan.Subtasks, count = plan.Subtasks.Count }, AgentJson.Options);

        _logger.LogInformation("Supervisor plan recorded {Count} subtask(s)", plan.Subtasks.Count);

        return SupervisorExecution.Synchronous(outcome);
    }

    /// <summary>E3 degrades ask_human to a clean "not supported until E4" outcome — the decider may emit it; the executor must NOT crash. SYNCHRONOUS → self-advance (E4 wires the real HITL park).</summary>
    private static SupervisorExecution ExecuteAskHuman() =>
        SupervisorExecution.Synchronous(JsonSerializer.Serialize(new { askHuman = "not-supported-until-e4" }, AgentJson.Options));

    /// <summary>A terminal stop records its outcome marker; the turn service finishes the loop (this never re-suspends).</summary>
    private static SupervisorExecution ExecuteStop(SupervisorDecision decision)
    {
        var stop = Deserialize<SupervisorStopPayload>(decision.PayloadJson);

        return SupervisorExecution.Synchronous(JsonSerializer.Serialize(new { stopped = true, outcome = stop?.Outcome, summary = stop?.Summary }, AgentJson.Options));
    }

    private static T? Deserialize<T>(string json) where T : class
    {
        try { return JsonSerializer.Deserialize<T>(json, AgentJson.Options); }
        catch (JsonException) { return null; }
    }
}
