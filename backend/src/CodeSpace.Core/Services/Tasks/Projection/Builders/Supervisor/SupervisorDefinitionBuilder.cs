using System.Text.Json;
using CodeSpace.Core.DependencyInjection;
using CodeSpace.Messages.Dtos.Workflows;
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
///       node clamps each to the <c>SupervisorLane</c> ceiling at EXECUTION (this build never clamps).</item>
///     <item><c>approvalPolicy</c> = <c>"spawns"</c> when <c>Caps.RequiresApproval</c>, else <c>"none"</c>.</item>
///   </list>
/// </para>
///
/// <para><b>Build stays PURE.</b> No lane check (the node fails closed if the lane is off; the router degrades
/// <c>deep</c> away from this projection when the supervisor-lane capability is unavailable — see
/// <c>SupervisorRecipe</c>), no clamp (the turn service clamps at execution), no DB / no LLM. The graph is a
/// fixed three-node skeleton, so the output ALWAYS passes <c>DefinitionValidator</c>. Self-registers via
/// <see cref="ISingletonDependency"/>; a new projection is a sibling builder folder, never an edit here.</para>
///
/// <para><b>Deferred (not built this PR):</b> the <c>conversationId</c> + mid-loop ask_human / approval HITL
/// surface — the deep bounds preset sets <c>Caps.RequiresApproval</c> false, so deep runs autonomously
/// (approvalPolicy <c>"none"</c>) to Success. Approval-gated deep is a follow-up.</para>
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
            ["goal"] = context.Seed.Goal,
            ["approvalPolicy"] = context.Route.Caps.RequiresApproval ? "spawns" : "none",
        };

        AddIfPresent(config, "maxParallelism", context.Route.Caps.MaxParallelism);
        AddIfPresent(config, "maxRounds", context.Route.Caps.MaxRounds);
        AddIfPresent(config, "maxTotalSpawns", context.Route.Caps.MaxTotalSpawns);
        AddIfPresent(config, "agentProfile", BuildAgentProfile(context.AgentProfile));

        return JsonSerializer.SerializeToElement(config);
    }

    /// <summary>The nested agentProfile object — the resolved profile field-for-field onto the ConfigSchema keys. Null (omitted) when the profile is absent or all-null, so a bare supervisor spawns the codex-cli / Standard / no-repo default.</summary>
    private static Dictionary<string, object?>? BuildAgentProfile(ResolvedAgentProfile? profile)
    {
        if (profile == null) return null;

        var map = new Dictionary<string, object?>();

        AddIfPresent(map, "repositoryId", profile.RepositoryId?.ToString());
        AddIfPresent(map, "harness", NullIfBlank(profile.Harness));
        AddIfPresent(map, "model", NullIfBlank(profile.Model));
        AddIfPresent(map, "agentDefinitionId", profile.AgentDefinitionId?.ToString());
        AddIfPresent(map, "modelCredentialId", profile.ModelCredentialId?.ToString());
        AddIfPresent(map, "runnerKind", NullIfBlank(profile.RunnerKind));
        AddIfPresent(map, "enableMcp", profile.EnableMcp);
        AddIfPresent(map, "autonomyLevel", NullIfBlank(profile.AutonomyLevel));

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
