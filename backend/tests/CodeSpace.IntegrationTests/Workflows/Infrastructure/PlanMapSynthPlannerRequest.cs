using System.Text.Json;
using Autofac;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Agents.ModelCredentials;
using CodeSpace.Core.Services.Tasks.Projection.Builders.PlanMapSynth;
using CodeSpace.Core.Services.Workflows.Llm;
using CodeSpace.Core.Services.Workflows.Nodes.Builtin;
using CodeSpace.Core.Services.Workflows.Planning;
using CodeSpace.Core.Services.Workflows.Planning.Planners;
using CodeSpace.Messages.Dtos.Workflows;
using CodeSpace.Messages.Tasks;
using CodeSpace.Messages.Tasks.Effort;

namespace CodeSpace.IntegrationTests.Workflows.Infrastructure;

/// <summary>
/// The single source of truth for the EXACT <see cref="StructuredLLMCompletionRequest"/> the plan-map-synth
/// planner sends — SINCE S4b the planner node is the triad's <c>plan.author</c>, so the request is rebuilt from
/// the SAME production seams the run uses: the REAL <see cref="PlanMapSynthDefinitionBuilder"/>'s emitted planner
/// node (flatPlan config + goal input) → <see cref="PlanAuthorNode.BuildPlanRequest"/> (task text + the flat
/// constraint) → <see cref="LlmWorkflowPlanner"/>'s system prompt + user-prompt build + <see cref="PlannerSchema"/>,
/// with the capability catalog rendered from the fixture team's live pool + harness registry (the run-time
/// inputs). The real-model test, the cassette-key lookup, and the drift detector all derive the request from
/// here, so they agree by construction; ANY planner prompt/schema/catalog change moves the
/// <see cref="RecordReplayStructuredLLMClient.CassetteKey"/> — which is what makes the drift detector bite.
/// </summary>
public static class PlanMapSynthPlannerRequest
{
    /// <summary>The fixed goal the real-model test launches with — the input the planner decomposes. Stable so the recorded cassette stays valid run-to-run.</summary>
    public const string Goal = "Improve the onboarding module across the codebase";

    /// <summary>
    /// The REAL model id the planner resolves for the RecordReplay tag — a genuine Anthropic model, because in
    /// RECORD mode the engine sends it to the live API. The pool pick is pool-driven, so this is ALSO the model
    /// id seeded onto the RecordReplay credential in <c>WorkflowsTestSeed.SeedInProcessModelPool</c>: the two MUST
    /// agree or the cassette key — which hashes <c>request.Model</c> — would move out from under
    /// <c>PlannerCassetteDriftTests.ExpectedPlannerKey</c>.
    /// </summary>
    public const string DefaultModel = "claude-sonnet-4-5";

    public static TaskLaunchSeed Seed(Guid teamId) => new() { Goal = Goal, SurfaceKind = "test", TeamId = teamId };

    /// <summary>Reconstruct the planner's structured request EXACTLY as <see cref="LlmWorkflowPlanner"/> sends it at run time, for the fixture team's seeded pool (the catalog is part of the prompt, so it is part of the key).</summary>
    public static async Task<StructuredLLMCompletionRequest> BuildAsync(ILifetimeScope scope, Guid teamId, CancellationToken cancellationToken)
    {
        var planner = PlannerNode();

        var config = planner.Config.Deserialize<Dictionary<string, JsonElement>>() ?? new();
        var inputs = planner.Inputs.Deserialize<Dictionary<string, JsonElement>>() ?? new();

        var goal = inputs.TryGetValue("goal", out var g) && g.ValueKind == JsonValueKind.String ? g.GetString()! : "";
        var grounding = inputs.TryGetValue("grounding", out var gr) && gr.ValueKind == JsonValueKind.String ? gr.GetString()! : "";

        var planRequest = PlanAuthorNode.BuildPlanRequest(config, teamId, goal, grounding, feedback: "");

        var pool = await scope.Resolve<IModelPoolSelector>().ListPoolAsync(teamId, allowedRowIds: null, cancellationToken).ConfigureAwait(false);
        var catalog = CapabilityCatalog.Render(scope.Resolve<IAgentHarnessRegistry>().All, pool);

        return new StructuredLLMCompletionRequest
        {
            Model = DefaultModel,
            SystemPrompt = LlmWorkflowPlanner.SystemPrompt,
            UserPrompt = LlmWorkflowPlanner.BuildUserPromptForTest(planRequest, catalog),
            JsonSchema = PlannerSchema.ResponseSchema,
        };
    }

    /// <summary>The planner node as the production builder emits it — for a build context with NO profile model / pins (the request then resolves pool-driven).</summary>
    private static NodeDefinition PlannerNode()
    {
        var context = new TaskBuildContext
        {
            Seed = Seed(Guid.Empty),
            Route = StandardRoute(),
            AgentProfile = new ResolvedAgentProfile { Harness = "codex-cli", RunnerKind = "local", AutonomyLevel = "Confined" },
        };

        return new PlanMapSynthDefinitionBuilder().Build(context).Nodes.Single(n => n.Id == "planner");
    }

    /// <summary>A standard-effort route with no parallelism cap — matches what <c>EffortRouter.RouteAsync</c> produces for the real-model test, and what the builder reads (only <c>Caps.MaxParallelism</c> touches the graph).</summary>
    private static RoutePlan StandardRoute() => new()
    {
        RecipeKind = TaskRecipeKinds.MapFanout,
        ProjectionKind = TaskProjectionKinds.PlanMapSynth,
        Caps = new RouteCaps(),
    };
}
