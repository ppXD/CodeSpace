using System.Text.Json;
using CodeSpace.Core.DependencyInjection;
using CodeSpace.Messages.Dtos.Workflows;
using CodeSpace.Messages.Tasks;

namespace CodeSpace.Core.Services.Tasks.Projection.Builders.PlanMapSynth;

/// <summary>
/// The <c>plan-map-synth</c> projection (Rule 18.3 — one impl beside its variant folder): the FIRST multi-agent
/// task shape. A planner decomposes the task into subtasks, a <c>flow.map</c> fans those out over one real
/// <c>agent.code</c> body per subtask, and a synthesizer reduces the per-branch results into the run's output.
/// This is the PRODUCTION HOME of the planner→map→synthesizer graph that previously lived ONLY inline in
/// <c>HeadlineFlowE2ETests.HeadlineFlowDefinition()</c> — the node shape here MIRRORS that proven graph EXACTLY
/// so it actually runs:
///
/// <para>
///   <c>trigger.manual</c> (start)
///   → <c>llm.complete</c> (planner): a <c>responseSchema</c> forces structured output, surfaced on
///     <c>json.subtasks</c> — the SAME shape the map binds; the seed goal frames a "decompose into subtasks"
///     instruction; the profile's model maps onto the node (AddIfPresent — absent ⇒ the node/deployment default).
///   → <c>flow.map</c>: <c>items = {{nodes.planner.outputs.json.subtasks}}</c> (the EXACT headline binding).
///   → <c>flow.map_start</c> (the body root, parented to the map).
///   → <c>agent.code</c> (the body, parented to the map): goal bound from <c>{{item}}</c> so each branch works
///     its OWN subtask; harness/model/credential/runner/autonomy/tools + repositoryId map from the profile via
///     the SHARED <see cref="AgentNodeMapping"/> — so a fan-out branch runs IDENTICALLY to an authored / a
///     single-agent agent.code node.
///   → <c>builtin.terminal</c> (synth): reduces the WHOLE <c>{{nodes.map.outputs.results}}</c> array into the
///     run's <c>combined</c> output — generic over ANY subtask count, the same whole-array reduce the headline
///     synth and <c>WorkflowPlanProjector</c> use (never a fixed element-indexed width).
/// </para>
///
/// <para><b>Build stays PURE.</b> The planner is a NODE that runs at EXECUTION — not a build-time LLM call — so
/// <see cref="Build"/> is a pure function of its <see cref="TaskBuildContext"/> (no DB / no LLM), exactly like
/// the single-agent builder. (This is why PR5 mirrors the headline planner-as-node graph rather than reusing
/// <c>IWorkflowPlanProjector.Project</c>'s plan-FIRST shape, which would require running the planner before
/// Build.) The graph is parameter-driven over a fixed skeleton, so the output ALWAYS passes the real
/// <c>DefinitionValidator</c>. Self-registers via <see cref="ISingletonDependency"/>; a new projection is a
/// sibling builder folder, never an edit here.</para>
/// </summary>
public sealed class PlanMapSynthDefinitionBuilder : IWorkflowDefinitionBuilder, ISingletonDependency
{
    public string ProjectionKind => TaskProjectionKinds.PlanMapSynth;

    public WorkflowDefinition Build(TaskBuildContext context) => new()
    {
        SchemaVersion = WorkflowDefinition.CurrentSchemaVersion,
        Nodes = BuildNodes(context),
        Edges = BuildEdges(),
    };

    private static IReadOnlyList<NodeDefinition> BuildNodes(TaskBuildContext context) => new List<NodeDefinition>
    {
        new() { Id = "start", TypeKey = "trigger.manual", Label = "Start", Config = Empty(), Inputs = Empty() },

        new() { Id = "planner", TypeKey = "llm.complete", Label = "Plan",
                Config = PlannerConfig(context), Inputs = PlannerInputs(context) },

        new() { Id = "map", TypeKey = "flow.map", Label = "Fan out", Config = MapConfigJson(context), Inputs = MapInputs() },

        new() { Id = "ms", TypeKey = "flow.map_start", Label = "Subtask", ParentId = "map", Config = Empty(), Inputs = Empty() },

        new() { Id = "agent", TypeKey = "agent.code", Label = "Work the subtask", ParentId = "map",
                Config = AgentNodeMapping.BuildAgentConfig(BranchGoal, context.AgentProfile), Inputs = AgentNodeMapping.BuildAgentInputs(context) },

        new() { Id = "synth", TypeKey = "builtin.terminal", Label = "Synthesize", Config = Empty(), Inputs = SynthInputs() },
    };

    private static IReadOnlyList<EdgeDefinition> BuildEdges() => new List<EdgeDefinition>
    {
        new() { From = "start", To = "planner" },
        new() { From = "planner", To = "map" },
        new() { From = "map", To = "synth" },
        new() { From = "ms", To = "agent" },
    };

    /// <summary>The planner Config — a <c>responseSchema</c> forcing a <c>{ subtasks: string[] }</c> object (surfaced on <c>json</c>, the shape the map binds) + the profile's model (AddIfPresent). The provider is the node's own default (the deployment-configured LLM); a test retargets it at the IStructuredLLMClient seam, never the builder.</summary>
    private static JsonElement PlannerConfig(TaskBuildContext context)
    {
        var config = new Dictionary<string, object?>
        {
            ["responseSchema"] = SubtasksSchema(),
        };

        AddIfPresent(config, "model", NullIfBlank(context.AgentProfile?.Model));

        return JsonSerializer.SerializeToElement(config);
    }

    /// <summary>The structured-output schema — a <c>subtasks</c> string array, the EXACT shape <c>flow.map</c>'s items binding (<c>{{nodes.planner.outputs.json.subtasks}}</c>) reads.</summary>
    private static object SubtasksSchema() => new
    {
        type = "object",
        properties = new { subtasks = new { type = "array", items = new { type = "string" } } },
        required = new[] { "subtasks" },
    };

    /// <summary>The planner Inputs — the seed goal framed as a "decompose into subtasks" instruction (the userPrompt the structured LLM completes).</summary>
    private static JsonElement PlannerInputs(TaskBuildContext context) => JsonSerializer.SerializeToElement(new
    {
        userPrompt = $"Decompose this task into a list of independent subtasks that can be worked in parallel: {context.Seed.Goal}",
    });

    /// <summary>The map Inputs — fan out over the planner's typed subtasks array (the EXACT headline binding).</summary>
    private static JsonElement MapInputs() => JsonSerializer.SerializeToElement(new
    {
        items = "{{nodes.planner.outputs.json.subtasks}}",
    });

    /// <summary>The map Config — carries the route's <see cref="RouteCaps.MaxParallelism"/> cap so the fan-out is bounded (the engine reads the <c>maxParallelism</c> key into the branch SemaphoreSlim via <c>MapConfig</c>). Only the one key is written, and only when the cap is set — an absent cap leaves the map unbounded (its prior behaviour, no config / hash change).</summary>
    private static JsonElement MapConfigJson(TaskBuildContext context) =>
        context.Route.Caps.MaxParallelism is { } cap
            ? JsonSerializer.SerializeToElement(new { maxParallelism = cap })
            : Empty();

    /// <summary>The synth Inputs — reduce ALL per-branch results into the run's <c>combined</c> output by binding the WHOLE map results array (<c>{{nodes.map.outputs.results}}</c>). Generic over ANY subtask count — the same whole-array reduce the real <c>WorkflowPlanProjector</c> synth and the headline flow use, NOT a fixed element-indexed width.</summary>
    private static JsonElement SynthInputs() => JsonSerializer.SerializeToElement(new
    {
        combined = "{{nodes.map.outputs.results}}",
    });

    /// <summary>The body agent's goal — bound from the map's per-branch <c>{{item}}</c> (this branch's subtask), so each branch works its OWN element. Matches the headline body's goal binding.</summary>
    private const string BranchGoal = "Work on {{item}}";

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static void AddIfPresent(Dictionary<string, object?> bag, string key, object? value)
    {
        if (value != null) bag[key] = value;
    }

    private static JsonElement Empty() => JsonDocument.Parse("{}").RootElement.Clone();
}
