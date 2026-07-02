using System.Text.Json;
using CodeSpace.Messages.Dtos.Workflows;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Tasks;

namespace CodeSpace.Core.Services.Tasks.Projection.Builders.PlanMap;

/// <summary>
/// The shared skeleton of the plan→map→agent→synth→done projection FAMILY (Rule 18 — the structure both
/// plan-map variants share, specialized only where they actually differ). The planner is the TRIAD's
/// <c>plan.author</c> node (S4b): it authors a structured, DURABLE plan — a versioned WorkPlan row the run's
/// checklist renders — and its <c>json.subtasks</c> output binds the map exactly like the structured
/// <c>llm.complete</c> it replaced (the node's <c>json</c> output is binding-compatible by contract). The
/// operator's planner critic (<c>reviewMode</c> None|Gate|Improve → the CriticPlannerDecorator) and pinned
/// planner model ride the node config. The plan is FLAT (<c>flatPlan</c>): the map fans every subtask out in
/// parallel, so the planner is constrained to independent items (authored dependsOn is stripped, logged).
///
/// <para>The graph shape + edges, the parallelism cap, the synth reduce, and the done terminal are IDENTICAL
/// across variants; the ONLY divergence left is the body agent's goal binding + optional per-branch mode —
/// the two hooks below. A fix to the shared spine lands once, here, for every variant. <see cref="Build"/>
/// stays PURE — the planner is a NODE that runs at execution, not a build-time LLM call — so the output always
/// passes the real <c>DefinitionValidator</c>. The base is abstract + not <c>ISingletonDependency</c>, so only
/// the concrete variants self-register.</para>
/// </summary>
public abstract class PlanMapBuilderBase : IWorkflowDefinitionBuilder
{
    /// <summary>The projection kind this variant registers under (the key <c>ITaskProjectionRegistry</c> resolves by).</summary>
    public abstract string ProjectionKind { get; }

    /// <summary>The body agent's goal binding over the planner's subtask objects — e.g. <c>"{{item.instruction}}"</c>.</summary>
    protected abstract string BranchGoal { get; }

    /// <summary>The body agent's per-branch mode binding (e.g. <c>"{{item.kind}}"</c> — the plan item's open kind), or null when the variant authors no mode — then <see cref="AgentNodeMapping.BuildAgentConfig"/> omits it (byte-identical to a no-mode node).</summary>
    protected virtual string? BranchMode => null;

    public WorkflowDefinition Build(TaskBuildContext context) => new()
    {
        SchemaVersion = WorkflowDefinition.CurrentSchemaVersion,
        Nodes = BuildNodes(context),
        Edges = BuildEdges(context),
    };

    private IReadOnlyList<NodeDefinition> BuildNodes(TaskBuildContext context)
    {
        var nodes = new List<NodeDefinition>
        {
            new() { Id = "start", TypeKey = "trigger.manual", Label = "Start", Config = Empty(), Inputs = Empty() },

            new() { Id = "planner", TypeKey = "plan.author", Label = "Plan",
                    Config = PlannerConfig(context), Inputs = PlannerInputs(context) },
        };

        // The confirm gate (S4d): the operator opted into confirm-plan-first, so the run PARKS on the authored
        // plan and the map binds the CONFIRM node's outputs — always the APPROVED version, never a rejected one.
        // The gate node carries the SAME planner config so a revision re-plans under the same model + critic.
        if (context.RequirePlanConfirmation)
            nodes.Add(new() { Id = "confirm", TypeKey = "plan.confirm", Label = "Confirm plan",
                              Config = PlannerConfig(context), Inputs = PlannerInputs(context) });

        nodes.AddRange(new NodeDefinition[]
        {
            new() { Id = "map", TypeKey = "flow.map", Label = "Fan out", Config = MapConfigJson(context), Inputs = MapInputs(context) },

            new() { Id = "ms", TypeKey = "flow.map_start", Label = "Subtask", ParentId = "map", Config = Empty(), Inputs = Empty() },

            new() { Id = "agent", TypeKey = "agent.code", Label = "Work the subtask", ParentId = "map",
                    Config = AgentNodeMapping.BuildAgentConfig(BranchGoal, context.AgentProfile, BranchMode, grounding: context.GroundingContext, acceptance: "{{item.acceptance}}"), Inputs = AgentNodeMapping.BuildAgentInputs(context) },

            new() { Id = "synth", TypeKey = "llm.complete", Label = "Synthesize",
                    Config = SynthConfig(context), Inputs = SynthInputs(context) },

            new() { Id = "done", TypeKey = "builtin.terminal", Label = "Done", Config = Empty(), Inputs = DoneInputs() },
        });

        return nodes;
    }

    private static IReadOnlyList<EdgeDefinition> BuildEdges(TaskBuildContext context) => context.RequirePlanConfirmation
        ? new List<EdgeDefinition>
        {
            new() { From = "start", To = "planner" },
            new() { From = "planner", To = "confirm" },
            new() { From = "confirm", To = "map" },
            new() { From = "map", To = "synth" },
            new() { From = "synth", To = "done" },
            new() { From = "ms", To = "agent" },
        }
        : new List<EdgeDefinition>
        {
            new() { From = "start", To = "planner" },
            new() { From = "planner", To = "map" },
            new() { From = "map", To = "synth" },
            new() { From = "synth", To = "done" },
            new() { From = "ms", To = "agent" },
        };

    /// <summary>The plan.author Config — always a FLAT plan (the parallel map cannot honor ordering), plus the launch's pinned planner model row + the operator's planner critic (reviewMode / reviewerModelId, omitted when off — byte-identical).</summary>
    private static JsonElement PlannerConfig(TaskBuildContext context)
    {
        var config = new Dictionary<string, object?>
        {
            ["flatPlan"] = true,
        };

        AddIfPresent(config, "plannerModelId", context.PlannerModelRowId?.ToString());
        AddIfPresent(config, "reviewMode", context.PlannerReviewMode != ReviewMode.None ? (int)context.PlannerReviewMode : null);
        AddIfPresent(config, "reviewerModelId", context.PlannerReviewMode != ReviewMode.None ? context.ReviewerModelId?.ToString() : null);

        return JsonSerializer.SerializeToElement(config);
    }

    /// <summary>The plan.author Inputs — the seed goal (+ the launch grounding when present, so a continue's prior-turn digest steers the plan; + the operator's acceptance criteria, so the plan and its per-item contracts target the definition of done — S5b).</summary>
    private static JsonElement PlannerInputs(TaskBuildContext context)
    {
        var inputs = new Dictionary<string, object?>
        {
            ["goal"] = context.Seed.Goal,
        };

        AddIfPresent(inputs, "grounding", NullIfBlank(context.GroundingContext));
        AddIfPresent(inputs, "criteria", context.AcceptanceCriteria is { Count: > 0 } criteria ? criteria.ToList() : null);

        return JsonSerializer.SerializeToElement(inputs);
    }

    /// <summary>The map Inputs — fan out over the plan's typed subtasks array. Under the confirm gate the map binds the CONFIRM node (always the APPROVED version); ungated it binds the planner directly (byte-identical to pre-gate).</summary>
    private static JsonElement MapInputs(TaskBuildContext context) => JsonSerializer.SerializeToElement(new
    {
        items = context.RequirePlanConfirmation ? "{{nodes.confirm.outputs.json.subtasks}}" : "{{nodes.planner.outputs.json.subtasks}}",
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
