using System.Text.Json;
using CodeSpace.Messages.Dtos.Workflows;
using CodeSpace.Messages.Tasks;

namespace CodeSpace.Core.Services.Tasks.Projection.Builders.PlanMap;

/// <summary>
/// The shared skeleton of the plan→map→agent→synth→done projection FAMILY (Rule 18 — the structure both
/// plan-map variants share, specialized only where they actually differ). The graph shape + edges, the map
/// parallelism cap, the planner-config/model wiring, the synth reduce, and the done terminal are IDENTICAL
/// across variants; the ONLY divergence is the planner's response schema, the planner prompt, and the body
/// agent's goal (+ optional per-branch mode) binding — the four hooks below.
///
/// <para>A concrete variant overrides those hooks and NOTHING else, so the two builders cannot drift: a fix to
/// the shared spine (the map items binding, the synth reduce prompt, the maxParallelism wiring, the done output
/// key) lands once, here, for every variant. This eliminates the ~120-line duplication structurally (no Rule-12.5
/// drift detector needed because there is nothing mirrored to drift). <see cref="Build"/> stays PURE — the planner
/// is a NODE that runs at execution, not a build-time LLM call — so the output always passes the real
/// <c>DefinitionValidator</c>. The base is abstract + not <c>ISingletonDependency</c>, so only the concrete
/// variants self-register; each registers <c>As&lt;IWorkflowDefinitionBuilder&gt;</c> via its inherited interface.</para>
/// </summary>
public abstract class PlanMapBuilderBase : IWorkflowDefinitionBuilder
{
    /// <summary>The projection kind this variant registers under (the key <c>ITaskProjectionRegistry</c> resolves by).</summary>
    public abstract string ProjectionKind { get; }

    /// <summary>The planner's structured-output <c>responseSchema</c> for the <c>subtasks</c> array the map fans out over — a string array (plan-map-synth) or an object-array spec (plan-map-dynamic). Surfaced on <c>json.subtasks</c>, the shape <see cref="MapInputs"/> binds.</summary>
    protected abstract object SubtasksResponseSchema();

    /// <summary>The planner node's inputs — the prompt that frames the decomposition for this variant (the SAME structured node, different framing).</summary>
    protected abstract JsonElement PlannerInputs(TaskBuildContext context);

    /// <summary>The body agent's goal binding — e.g. <c>"Work on {{item}}"</c> over a string array, or <c>"{{item.goal}}"</c> over a spec object.</summary>
    protected abstract string BranchGoal { get; }

    /// <summary>The body agent's per-branch mode binding (e.g. <c>"{{item.mode}}"</c>), or null when the variant authors no mode — then <see cref="AgentNodeMapping.BuildAgentConfig"/> omits it (byte-identical to a no-mode node).</summary>
    protected virtual string? BranchMode => null;

    public WorkflowDefinition Build(TaskBuildContext context) => new()
    {
        SchemaVersion = WorkflowDefinition.CurrentSchemaVersion,
        Nodes = BuildNodes(context),
        Edges = BuildEdges(),
    };

    private IReadOnlyList<NodeDefinition> BuildNodes(TaskBuildContext context) => new List<NodeDefinition>
    {
        new() { Id = "start", TypeKey = "trigger.manual", Label = "Start", Config = Empty(), Inputs = Empty() },

        new() { Id = "planner", TypeKey = "llm.complete", Label = "Plan",
                Config = PlannerConfig(context), Inputs = PlannerInputs(context) },

        new() { Id = "map", TypeKey = "flow.map", Label = "Fan out", Config = MapConfigJson(context), Inputs = MapInputs() },

        new() { Id = "ms", TypeKey = "flow.map_start", Label = "Subtask", ParentId = "map", Config = Empty(), Inputs = Empty() },

        new() { Id = "agent", TypeKey = "agent.code", Label = "Work the subtask", ParentId = "map",
                Config = AgentNodeMapping.BuildAgentConfig(BranchGoal, context.AgentProfile, BranchMode, grounding: context.GroundingContext), Inputs = AgentNodeMapping.BuildAgentInputs(context) },

        new() { Id = "synth", TypeKey = "llm.complete", Label = "Synthesize",
                Config = SynthConfig(context), Inputs = SynthInputs(context) },

        new() { Id = "done", TypeKey = "builtin.terminal", Label = "Done", Config = Empty(), Inputs = DoneInputs() },
    };

    private static IReadOnlyList<EdgeDefinition> BuildEdges() => new List<EdgeDefinition>
    {
        new() { From = "start", To = "planner" },
        new() { From = "planner", To = "map" },
        new() { From = "map", To = "synth" },
        new() { From = "synth", To = "done" },
        new() { From = "ms", To = "agent" },
    };

    /// <summary>The planner Config — the variant's <see cref="SubtasksResponseSchema"/> forcing structured output (surfaced on <c>json</c>) + the profile's model (AddIfPresent). The provider is the node's own default; a test retargets it at the IStructuredLLMClient seam, never the builder.</summary>
    private JsonElement PlannerConfig(TaskBuildContext context)
    {
        var config = new Dictionary<string, object?>
        {
            ["responseSchema"] = SubtasksResponseSchema(),
        };

        AddIfPresent(config, "model", NullIfBlank(context.AgentProfile?.Model));

        return JsonSerializer.SerializeToElement(config);
    }

    /// <summary>The map Inputs — fan out over the planner's typed subtasks array (a string array or an object array binds the same).</summary>
    private static JsonElement MapInputs() => JsonSerializer.SerializeToElement(new
    {
        items = "{{nodes.planner.outputs.json.subtasks}}",
    });

    /// <summary>The map Config — carries the route's <see cref="RouteCaps.MaxParallelism"/> cap so the fan-out is bounded (the engine reads the <c>maxParallelism</c> key into the branch SemaphoreSlim via <c>MapConfig</c>). Only the one key is written, and only when the cap is set — an absent cap leaves the map unbounded.</summary>
    private static JsonElement MapConfigJson(TaskBuildContext context) =>
        context.Route.Caps.MaxParallelism is { } cap
            ? JsonSerializer.SerializeToElement(new { maxParallelism = cap })
            : Empty();

    /// <summary>The synth Config — the LLM the reduce runs on. Provider defaults to Anthropic (the same default the planner node + <c>WorkflowPlanProjector</c>'s synth use); the profile's model maps on via AddIfPresent. A test retargets the provider at the ILLMClient seam, never the builder.</summary>
    private static JsonElement SynthConfig(TaskBuildContext context)
    {
        var config = new Dictionary<string, object?>
        {
            ["provider"] = "Anthropic",
        };

        AddIfPresent(config, "model", NullIfBlank(context.AgentProfile?.Model));

        return JsonSerializer.SerializeToElement(config);
    }

    /// <summary>The synth Inputs — a REAL reduce prompt: combine ALL per-branch results into one coherent answer that addresses the seed goal. The userPrompt embeds the goal AND the WHOLE map results array (<c>{{nodes.map.outputs.results}}</c>), so the reduce is generic over ANY subtask count.</summary>
    private static JsonElement SynthInputs(TaskBuildContext context) => JsonSerializer.SerializeToElement(new
    {
        systemPrompt = "Combine the per-subtask results into one coherent answer that addresses the goal.",
        userPrompt = $"Goal: {context.Seed.Goal}\n\nPer-subtask results:\n{{{{nodes.map.outputs.results}}}}",
    });

    /// <summary>The done terminal Inputs — bind the synth's reduced <c>text</c> output into the run's <c>combined</c> output (the llm.complete node's output key is <c>text</c>).</summary>
    private static JsonElement DoneInputs() => JsonSerializer.SerializeToElement(new
    {
        combined = "{{nodes.synth.outputs.text}}",
    });

    protected static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    protected static void AddIfPresent(Dictionary<string, object?> bag, string key, object? value)
    {
        if (value != null) bag[key] = value;
    }

    protected static JsonElement Empty() => JsonDocument.Parse("{}").RootElement.Clone();
}
