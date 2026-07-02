using System.Text.Json;
using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Services.Agents.Workspace;
using CodeSpace.Messages.Dtos.Workflows;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Tasks;

namespace CodeSpace.Core.Services.Tasks.Projection.Builders.Supervisor;

/// <summary>
/// The <c>supervisor</c> projection (Rule 18.3 — one impl beside its variant folder): the DEEP effort tier's
/// shape. Emits the fixed-safe graph <c>trigger.manual → agent.supervisor → builtin.terminal</c> — the SAME
/// minimal lane graph the supervisor integration tests author — whose <c>agent.supervisor</c> node Config maps
/// from the context's <see cref="TaskLaunchSeed.Goal"/>, the resolved <see cref="ResolvedAgentProfile"/>, and
/// the route's <see cref="RouteCaps"/> onto the EXACT camelCase keys the node's ConfigSchema reads (a
/// <c>SupervisorGoalConfig</c> shape): goal · agentProfile · maxParallelism / maxRounds / maxTotalSpawns ·
/// approvalPolicy. So a snapshot supervisor run executes IDENTICALLY to an authored agent.supervisor node — the
/// turn service sees the same goal config.
///
/// <para>Config-key mapping (each emitted only when present, so a bare profile / caps stays minimal):
///   <list type="bullet">
///     <item><c>goal</c> = <c>Seed.Goal</c> — the objective the supervisor pursues.</item>
///     <item><c>agentProfile</c> = the resolved profile field-for-field (repositoryId / harness / model /
///       agentDefinitionId / modelCredentialId / runnerKind / enableMcp / autonomyLevel) — the default envelope
///       every spawned agent inherits.</item>
///     <item><c>maxParallelism</c> / <c>maxRounds</c> / <c>maxTotalSpawns</c> = the route's
///       <see cref="RouteCaps"/> (the RouteCaps→SupervisorGoalConfig fold) — the operator's safety bounds. The
///       node clamps each to its bounds ceiling at EXECUTION (this build never clamps).</item>
///     <item><c>approvalPolicy</c> = <c>"spawns"</c> when <c>Caps.RequiresApproval</c>, else <c>"none"</c>.</item>
///   </list>
/// </para>
///
/// <para><b>Build stays PURE.</b> The supervisor recipe always projects the durable lane (no capability gate —
/// the <c>deep</c> route always reaches this projection; see <c>SupervisorRecipe</c>), no clamp (the turn
/// service clamps at execution), no DB / no LLM. The graph is a fixed three-node skeleton, so the output ALWAYS
/// passes <c>DefinitionValidator</c>. Self-registers via <see cref="ISingletonDependency"/>; a new projection is
/// a sibling builder folder, never an edit here.</para>
///
/// <para>The <c>conversationId</c> HITL surface is wired (S4a): a supervisor launch carries its session's
/// channel, so mid-loop ask_human / plan-confirmation / approval cards post somewhere real. The deep bounds
/// preset still sets <c>Caps.RequiresApproval</c> false (deep runs autonomously to Success unless the operator
/// opts into a gate); approval-gated deep remains a follow-up.</para>
/// </summary>
public sealed class SupervisorDefinitionBuilder : IWorkflowDefinitionBuilder, ISingletonDependency
{
    public string ProjectionKind => TaskProjectionKinds.Supervisor;

    public WorkflowDefinition Build(TaskBuildContext context) => new()
    {
        SchemaVersion = WorkflowDefinition.CurrentSchemaVersion,
        Nodes = BuildNodes(context),
        Edges = BuildEdges(),
    };

    private static IReadOnlyList<NodeDefinition> BuildNodes(TaskBuildContext context) => new List<NodeDefinition>
    {
        new() { Id = "start", TypeKey = "trigger.manual", Label = "Start", Config = Empty(), Inputs = Empty() },

        new() { Id = "sup", TypeKey = "agent.supervisor", Label = "Supervise",
                Config = BuildSupervisorConfig(context), Inputs = Empty() },

        new() { Id = "end", TypeKey = "builtin.terminal", Label = "Done", Config = Empty(),
                Inputs = TerminalInputs() },
    };

    private static IReadOnlyList<EdgeDefinition> BuildEdges() => new List<EdgeDefinition>
    {
        new() { From = "start", To = "sup" },
        new() { From = "sup", To = "end" },
    };

    /// <summary>The agent.supervisor Config — the goal + bounds + approval policy + the spawned-agent profile, mapped onto the EXACT camelCase keys the node's ConfigSchema reads. Optional knobs are added only when present so a bare context stays minimal.</summary>
    private static JsonElement BuildSupervisorConfig(TaskBuildContext context)
    {
        var config = new Dictionary<string, object?>
        {
            // A continuing turn's thread-context is prepended to the supervisor's goal (its planning prompt) the SAME
            // way the agent.code projections inject it, so the supervisor plans the follow-up against prior work.
            ["goal"] = AgentNodeMapping.ComposeGoal(context.Seed.Goal, context.GroundingContext),
            ["approvalPolicy"] = context.Route.Caps.RequiresApproval ? "spawns" : "none",
        };

        AddIfPresent(config, "maxParallelism", context.Route.Caps.MaxParallelism);
        AddIfPresent(config, "maxRounds", context.Route.Caps.MaxRounds);
        AddIfPresent(config, "maxTotalSpawns", context.Route.Caps.MaxTotalSpawns);
        // Forwards the cost cap the SAME way as the sibling caps — the operator's launch-surface budget
        // (TaskCapsOverride.MaxCostUsd → the router's CapsOverride seam → Route.Caps) reaches the supervisor config
        // here, and SupervisorBounds force-stops the run when realized spend exceeds it. Omitted (null) when neither
        // the launch caps nor a bounds preset set it.
        AddIfPresent(config, "maxCostUsd", context.Route.Caps.MaxCostUsd);
        // The supervisor's OWN brain model (the decider runs on it), self-resolved at launch when the operator pinned
        // none — without it the decider stops turn-1 (NoBrainModelStop). Omitted (null) when the pool can't supply a
        // structured-capable brain, so the build stays pure + a bare supervisor still validates (it just fails closed
        // at decide time — the honest floor).
        AddIfPresent(config, "supervisorModelId", context.SupervisorBrainModelId?.ToString());
        // The decision-critic mode + its reviewer model — baked so every turn + replay reads the same critic config.
        // None (the default) ⇒ omitted ⇒ the critic decorator is a pure passthrough ⇒ byte-identical. Baked as the enum's
        // INT (the supervisor config deserializer has no string-enum converter), round-tripping to ReviewMode by default.
        AddIfPresent(config, "decisionReviewMode", context.DecisionReviewMode == ReviewMode.None ? (object?)null : (int)context.DecisionReviewMode);
        // The tier-generic PLAN critic (S4e) — on Deep it scopes to the supervisor's plan decisions, so "critique
        // the plan before it runs" works identically on every tier. Omitted when off (byte-identical).
        AddIfPresent(config, "planReviewMode", context.PlannerReviewMode == ReviewMode.None ? (object?)null : (int)context.PlannerReviewMode);
        AddIfPresent(config, "reviewerModelId", context.ReviewerModelId?.ToString());
        // The operator's allowed model pool (credentialed-model row ids) for the agents this supervisor dispatches —
        // baked as a string-uuid array onto the node's allowedModelIds, where a dispatched model out of the pool fails
        // closed. Omitted (null) when empty, so a bare supervisor draws from all the team's models — byte-identical.
        AddIfPresent(config, "allowedModelIds", context.AllowedModelIds is { Count: > 0 } pool ? pool.Select(id => id.ToString()).ToList() : null);
        // The operator's allowed AGENT (persona) pool — baked as a string-uuid array onto the node's
        // allowedAgentDefinitionIds, where a dispatched persona out of the pool fails closed. Omitted when empty
        // (all the team's personas — byte-identical).
        AddIfPresent(config, "allowedAgentDefinitionIds", context.AllowedAgentDefinitionIds is { Count: > 0 } agents ? agents.Select(id => id.ToString()).ToList() : null);
        // The operator's free-text acceptance CRITERIA — rendered into the decider prompt as the definition of done (the
        // model targets them; NOT executed, distinct from the acceptanceChecks argv floor). Omitted when empty (byte-identical).
        AddIfPresent(config, "acceptanceCriteria", context.AcceptanceCriteria is { Count: > 0 } criteria ? criteria.ToList() : null);
        // The operator's EXECUTABLE acceptance floor (S4b) — an argv run against the reviewable head at the terminal
        // stop; a non-zero exit fails the stop + withholds the branch. Blank entries dropped; omitted when empty
        // (byte-identical). The free-text criteria above steer the model; this floor VERIFIES the result.
        AddIfPresent(config, "acceptanceChecks", context.AcceptanceChecks?.Where(c => !string.IsNullOrWhiteSpace(c)).ToList() is { Count: > 0 } checks ? checks : null);
        // The S3 plan-confirmation gate — every authored plan version parks for the operator's confirmation before any
        // agent is created. Omitted when off (byte-identical).
        AddIfPresent(config, "requirePlanConfirmation", context.RequirePlanConfirmation ? (object)true : null);
        // The session's chat surface (S4a) — where the run's HITL cards (ask_human, plan confirmation, approvals) post.
        // Omitted when absent (byte-identical; the executor then degrades asks to no-surface and the confirm gate
        // force-stops fail-closed rather than bypassing).
        AddIfPresent(config, "conversationId", context.ConversationId?.ToString());
        // The operator's tool allow-list lives on the parent supervisor config (NOT the nested agentProfile — it threads
        // into each spawned AgentTask.Tools via SpawnedAgentTools, the same place an authored allowedTools lands). Read
        // off the resolved profile (the same source the single-agent path's "tools" key uses). Omitted when empty ⇒ the
        // harness default ⇒ byte-identical. Closes the gap where a launched pool was dropped on the supervisor lane.
        AddIfPresent(config, "allowedTools", context.AgentProfile?.AllowedTools is { Count: > 0 } tools ? tools.ToList() : null);
        AddIfPresent(config, "agentProfile", BuildAgentProfile(context.AgentProfile));

        return JsonSerializer.SerializeToElement(config);
    }

    /// <summary>The nested agentProfile object — the resolved profile field-for-field onto the ConfigSchema keys. Null (omitted) when the profile is absent or all-null, so a bare supervisor spawns the codex-cli / Standard / no-repo default.</summary>
    private static Dictionary<string, object?>? BuildAgentProfile(ResolvedAgentProfile? profile)
    {
        if (profile == null) return null;

        var map = new Dictionary<string, object?>();

        AddIfPresent(map, "repositoryId", profile.RepositoryId?.ToString());
        // Multi-repo: each spawned agent ALSO clones these (the supervisor config's relatedRepositories — the SAME
        // {repositoryId, alias?, access?} shape the agent.code node gets, via the ONE shared serializer). Omitted
        // when none, so a single-repo / analysis-only supervisor spawn is byte-identical.
        AddIfPresent(map, "relatedRepositories", AgentWorkspaceAuthoring.SerializeRelatedRepositories(profile.RelatedRepositories));
        AddIfPresent(map, "harness", NullIfBlank(profile.Harness));
        AddIfPresent(map, "model", NullIfBlank(profile.Model));
        AddIfPresent(map, "agentDefinitionId", profile.AgentDefinitionId?.ToString());
        AddIfPresent(map, "modelCredentialId", profile.ModelCredentialId?.ToString());
        AddIfPresent(map, "runnerKind", NullIfBlank(profile.RunnerKind));
        AddIfPresent(map, "enableMcp", profile.EnableMcp);
        AddIfPresent(map, "pushBranch", profile.PushBranch);
        AddIfPresent(map, "integrateBranches", profile.IntegrateBranches);
        AddIfPresent(map, "autonomyLevel", NullIfBlank(profile.AutonomyLevel));
        AddIfPresent(map, "cwdMode", profile.CwdMode?.ToString());
        // The output-review critic for each spawned agent — None ⇒ omitted (the enum int) ⇒ byte-identical.
        AddIfPresent(map, "outputReviewMode", profile.OutputReviewMode == ReviewMode.None ? (object?)null : (int)profile.OutputReviewMode);
        AddIfPresent(map, "reviewerModelId", profile.ReviewerModelId?.ToString());

        return map.Count > 0 ? map : null;
    }

    /// <summary>The terminal surfaces the supervisor's outputs as the run's outputs — the SAME output keys agent.supervisor emits (status / decision / reason / turns), wired via {{ref}}.</summary>
    private static JsonElement TerminalInputs() => JsonSerializer.SerializeToElement(new
    {
        status = "{{nodes.sup.outputs.status}}",
        decision = "{{nodes.sup.outputs.decision}}",
        reason = "{{nodes.sup.outputs.reason}}",
        turns = "{{nodes.sup.outputs.turns}}",
    });

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    /// <summary>Add a key only when the value is non-null — an absent key inherits the node's own default, keeping a bare config minimal.</summary>
    private static void AddIfPresent(Dictionary<string, object?> bag, string key, object? value)
    {
        if (value != null) bag[key] = value;
    }

    private static JsonElement Empty() => JsonDocument.Parse("{}").RootElement.Clone();
}
