using System.Text.Json;
using CodeSpace.Core.Services.Tasks.Projection.Builders.PlanMapSynth;
using CodeSpace.Core.Services.Workflows.Llm;
using CodeSpace.Messages.Dtos.Workflows;
using CodeSpace.Messages.Tasks;
using CodeSpace.Messages.Tasks.Effort;

namespace CodeSpace.IntegrationTests.Workflows.Infrastructure;

/// <summary>
/// The single source of truth for the EXACT <see cref="StructuredLLMCompletionRequest"/> the plan-map-synth
/// planner node sends — reconstructed by running the REAL <see cref="PlanMapSynthDefinitionBuilder"/> and
/// reading its emitted planner node, so it tracks the production prompt + responseSchema automatically. The
/// real-model test, the cassette-key lookup, and the drift detector all derive the request from here, so they
/// agree by construction. A planner prompt/schema change flows straight through, moving the
/// <see cref="RecordReplayStructuredLLMClient.CassetteKey"/> — which is what makes the drift detector bite.
/// </summary>
public static class PlanMapSynthPlannerRequest
{
    /// <summary>The fixed goal the real-model test launches with — the input the planner decomposes. Stable so the recorded cassette stays valid run-to-run.</summary>
    public const string Goal = "Improve the onboarding module across the codebase";

    /// <summary>The node default model an absent-profile-model planner resolves to (mirrors <c>LlmCompleteNode.DefaultModelFor("Anthropic")</c>).</summary>
    public const string DefaultModel = "claude-sonnet-4-5";

    public static TaskLaunchSeed Seed(Guid teamId) => new() { Goal = Goal, SurfaceKind = "test", TeamId = teamId };

    /// <summary>Reconstruct the planner's structured request EXACTLY as the engine's llm.complete node would, from the production builder's emitted planner node.</summary>
    public static StructuredLLMCompletionRequest Build()
    {
        var planner = PlannerNode();

        var config = planner.Config.Deserialize<Dictionary<string, JsonElement>>() ?? new();
        var inputs = planner.Inputs.Deserialize<Dictionary<string, JsonElement>>() ?? new();

        return new StructuredLLMCompletionRequest
        {
            Model = config.TryGetValue("model", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString()! : DefaultModel,
            SystemPrompt = inputs.TryGetValue("systemPrompt", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString()! : "",
            UserPrompt = inputs.TryGetValue("userPrompt", out var u) && u.ValueKind == JsonValueKind.String ? u.GetString()! : "",
            JsonSchema = config["responseSchema"],
        };
    }

    /// <summary>The planner node as the production builder emits it — for a build context with NO profile model (so the request resolves the node-default model).</summary>
    private static NodeDefinition PlannerNode()
    {
        var context = new TaskBuildContext
        {
            Seed = Seed(Guid.Empty),
            Route = StandardRoute(),
            AgentProfile = new ResolvedAgentProfile { Harness = "codex-cli", RunnerKind = "local", AutonomyLevel = "Confined" },
        };

        return new PlanMapSynthDefinitionBuilder().Build(context).Nodes.Single(n => n.TypeKey == "llm.complete");
    }

    /// <summary>A standard-effort route with no parallelism cap — matches what <c>EffortRouter.RouteAsync</c> produces for the real-model test, and what the builder reads (only <c>Caps.MaxParallelism</c> touches the graph).</summary>
    private static RoutePlan StandardRoute() => new()
    {
        RecipeKind = TaskRecipeKinds.MapFanout,
        ProjectionKind = TaskProjectionKinds.PlanMapSynth,
        Caps = new RouteCaps(),
    };
}
