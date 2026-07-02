using System.Text.Json;
using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Agents.ModelCredentials;
using CodeSpace.Core.Services.Agents.Workspace;
using CodeSpace.Core.Services.Chat;
using CodeSpace.Core.Services.Chat.Interactions;
using CodeSpace.Core.Services.Plans;
using CodeSpace.Core.Services.Workflows.Artifacts;
using CodeSpace.Core.Services.Workflows.Llm;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Plans;
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
///   <item><c>ask_human</c> → post a question CARD to the supervisor run's conversation (a single <c>Action</c>
///         wait, token-correlated) + park on the human's answer; the answer is folded into the next turn. ASYNC
///         (PARK-ON-HUMAN, see <c>.AskHuman.cs</c>). Degrades to a no-surface stop when no conversation is
///         authored or the tenancy check fails.</item>
///   <item><c>stop</c> → a terminal marker (the turn service finishes the loop; this never re-suspends).</item>
/// </list>
///
/// <para>Scoped (Rule 16 — it owns its DB writes via the agent-run service + the wait staging + the bot post).
/// Exactly-once is the turn service's claim hop's job (Pending→Running before this runs once); the executor's
/// spawn staging + the ask_human card post both record their state in the outcome so a replay re-derives the
/// SAME park classification WITHOUT re-staging / re-posting.</para>
/// </summary>
public sealed partial class RealSupervisorActionExecutor : ISupervisorActionExecutor, IScopedDependency
{
    private readonly CodeSpaceDbContext _db;
    private readonly IAgentRunService _agentRuns;
    private readonly IAgentDefinitionResolver _agentDefinitionResolver;
    private readonly IChatBotService _bot;
    private readonly IInteractionComponentRegistry _components;
    private readonly IArtifactOffloader _offloader;
    private readonly IBranchIntegrator _integrator;
    private readonly IAgentWorkspaceResolver _workspaces;
    private readonly ILLMClientRegistry _llm;
    private readonly IModelPoolSelector _modelSelector;
    private readonly IAgentHarnessRegistry _harnesses;
    private readonly IWorkPlanService _workPlans;
    private readonly ILogger<RealSupervisorActionExecutor> _logger;

    public RealSupervisorActionExecutor(CodeSpaceDbContext db, IAgentRunService agentRuns, IAgentDefinitionResolver agentDefinitionResolver, IChatBotService bot, IInteractionComponentRegistry components, IArtifactOffloader offloader, IBranchIntegrator integrator, IAgentWorkspaceResolver workspaces, ILLMClientRegistry llm, IModelPoolSelector modelSelector, IAgentHarnessRegistry harnesses, IWorkPlanService workPlans, ILogger<RealSupervisorActionExecutor> logger)
    {
        _db = db;
        _agentRuns = agentRuns;
        _agentDefinitionResolver = agentDefinitionResolver;
        _bot = bot;
        _components = components;
        _offloader = offloader;
        _integrator = integrator;
        _workspaces = workspaces;
        _llm = llm;
        _modelSelector = modelSelector;
        _harnesses = harnesses;
        _workPlans = workPlans;
        _logger = logger;
    }

    public Task<SupervisorExecution> ExecuteAsync(SupervisorDecision decision, SupervisorTurnContext context, CancellationToken cancellationToken) => decision.Kind switch
    {
        SupervisorDecisionKinds.Plan => ExecutePlanAsync(decision, context, cancellationToken),
        SupervisorDecisionKinds.Spawn => ExecuteSpawnAsync(decision, context, cancellationToken),
        SupervisorDecisionKinds.Retry => ExecuteRetryAsync(decision, context, cancellationToken),
        SupervisorDecisionKinds.Merge => ExecuteMergeAsync(decision, context, cancellationToken),
        SupervisorDecisionKinds.Resolve => ExecuteResolveAsync(decision, context, cancellationToken),
        SupervisorDecisionKinds.AskHuman => ExecuteAskHumanAsync(decision, context, cancellationToken),
        SupervisorDecisionKinds.Stop => Task.FromResult(ExecuteStop(decision)),
        _ => Task.FromResult(SupervisorExecution.Synchronous(JsonSerializer.Serialize(new { unsupported = decision.Kind }, AgentJson.Options))),
    };

    /// <summary>
    /// Record the decider's planned subtasks as the decision outcome (the spawn/merge read them back) AND
    /// persist them as the run's next durable <c>work_plan</c> version — the same producer-agnostic artifact
    /// the <c>plan.author</c> node writes, so the confirmation/checklist surfaces read ONE store. The decider
    /// already produced the plan — the executor does not re-plan. SYNCHRONOUS → self-advance.
    /// </summary>
    private async Task<SupervisorExecution> ExecutePlanAsync(SupervisorDecision decision, SupervisorTurnContext context, CancellationToken cancellationToken)
    {
        var plan = Deserialize<SupervisorPlanPayload>(decision.PayloadJson) ?? new SupervisorPlanPayload();

        await PersistWorkPlanAsync(plan, context, cancellationToken).ConfigureAwait(false);

        // L4 arc C: a plan that authored semantic phases records them alongside the subtasks (the scorecard / tasks-phases
        // surface projects them); a flat plan records only planned/count → byte-identical to before.
        var outcome = plan.Phases is { Count: > 0 }
            ? JsonSerializer.Serialize(new { planned = plan.Subtasks, count = plan.Subtasks.Count, phases = plan.Phases }, AgentJson.Options)
            : JsonSerializer.Serialize(new { planned = plan.Subtasks, count = plan.Subtasks.Count }, AgentJson.Options);

        _logger.LogInformation("Supervisor plan recorded {Count} subtask(s) in {PhaseCount} phase(s)", plan.Subtasks.Count, plan.Phases?.Count ?? 0);

        return SupervisorExecution.Synchronous(outcome);
    }

    /// <summary>
    /// The loop-tier work-plan write. Exactly-once per decision via the per-turn origin key — a crash-replayed
    /// in-flight plan decision re-executes this and lands on the SAME row (the store returns the existing
    /// version); a LATER re-plan is a different turn → a new key → the run's next version. A pure-unit context
    /// (no run/team — nothing to own the row) skips, keeping unit-tier executors DB-free.
    /// </summary>
    private async Task PersistWorkPlanAsync(SupervisorPlanPayload plan, SupervisorTurnContext context, CancellationToken cancellationToken)
    {
        if (context.SupervisorRunId == Guid.Empty || context.TeamId == Guid.Empty) return;

        await _workPlans.SaveVersionAsync(new WorkPlanDraft
        {
            TeamId = context.TeamId,
            WorkflowRunId = context.SupervisorRunId,
            OriginKind = WorkPlanOrigins.Supervisor,
            OriginKey = $"{(string.IsNullOrEmpty(context.NodeId) ? "sup" : context.NodeId)}#turn{context.TurnNumber}",
            Goal = string.IsNullOrWhiteSpace(plan.Goal) ? context.Goal : plan.Goal,
            Items = plan.Subtasks.Select(WorkPlanItem.From).ToList(),
        }, cancellationToken).ConfigureAwait(false);
    }

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
