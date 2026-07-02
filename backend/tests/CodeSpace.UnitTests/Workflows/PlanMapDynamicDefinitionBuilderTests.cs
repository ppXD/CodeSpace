using System.Text.Json;
using CodeSpace.Core.Services.Tasks.Projection.Builders.PlanMapDynamic;
using CodeSpace.Core.Services.Workflows.Engine;
using CodeSpace.Core.Services.Workflows.Llm;
using CodeSpace.Core.Services.Workflows.Nodes;
using CodeSpace.Core.Services.Workflows.Nodes.Builtin;
using CodeSpace.Messages.Dtos.Workflows;
using CodeSpace.Messages.Tasks;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

/// <summary>
/// Pins the plan-map-dynamic projection builder — the MODEL-AUTHORED sibling of plan-map-synth. The emitted graph
/// is STRUCTURALLY identical (<c>trigger.manual → llm.complete(planner) → flow.map(items=planner.json.subtasks) →
/// flow.map_start → agent.code(body) → llm.complete(synth) → builtin.terminal(done)</c>) and ALWAYS passes the
/// REAL <see cref="DefinitionValidator"/>, with TWO differences from plan-map-synth: the planner's responseSchema
/// is an OBJECT-ARRAY of per-agent specs (<c>{ name?, goal, mode }</c>, mode the enum research|code), and the
/// body agent binds <c>goal={{item.instruction}}</c> + <c>mode={{item.kind}}</c> so the MODEL decides each agent's intent.
/// </summary>
[Trait("Category", "Unit")]
public class PlanMapDynamicDefinitionBuilderTests
{
    private static readonly PlanMapDynamicDefinitionBuilder Builder = new();

    /// <summary>The REAL validator over the REAL node runtimes the builder emits — the planner json output, the map structure, the {{item.instruction}}/{{item.kind}} config refs, and the synth result refs all validate against the actual manifests (mirrors PlanMapSynthDefinitionBuilderTests).</summary>
    private static DefinitionValidator RealValidator() => new(new NodeRegistry(new INodeRuntime[]
    {
        new TriggerManualNode(),
        new LlmCompleteNode(new LLMClientRegistry(Array.Empty<ILLMClient>()), null!),
        new PlanAuthorNode(null!),
        new FlowMapNode(),
        new FlowMapStartNode(),
        new AgentCodeNode(),
        new TerminalNode(),
    }));

    private static TaskBuildContext Context(ResolvedAgentProfile? profile = null, Guid? seedRepo = null, RouteCaps? caps = null) => new()
    {
        Seed = new TaskLaunchSeed { Goal = "Improve the onboarding module", SurfaceKind = "chat", TeamId = Guid.NewGuid(), RepositoryId = seedRepo },
        Route = new RoutePlan { ProjectionKind = TaskProjectionKinds.PlanMapDynamic, Caps = caps ?? new RouteCaps() },
        AgentProfile = profile,
    };

    [Fact]
    public void Reports_the_plan_map_dynamic_projection_kind()
    {
        Builder.ProjectionKind.ShouldBe(TaskProjectionKinds.PlanMapDynamic);
    }

    [Fact]
    public void Emits_the_planner_map_agent_synth_graph()
    {
        var def = Builder.Build(Context());

        var byId = def.Nodes.ToDictionary(n => n.Id, n => n.TypeKey);
        byId["start"].ShouldBe("trigger.manual");
        byId["planner"].ShouldBe("plan.author");
        byId["map"].ShouldBe("flow.map");
        byId["ms"].ShouldBe("flow.map_start");
        byId["agent"].ShouldBe("agent.code");
        byId["synth"].ShouldBe("llm.complete");
        byId["done"].ShouldBe("builtin.terminal");

        // The body nodes are parented to the map so the engine fans them out per subtask.
        def.Nodes.Single(n => n.Id == "ms").ParentId.ShouldBe("map");
        def.Nodes.Single(n => n.Id == "agent").ParentId.ShouldBe("map");

        def.Edges.Select(e => (e.From, e.To)).ShouldBe(
            new[] { ("start", "planner"), ("planner", "map"), ("map", "synth"), ("synth", "done"), ("ms", "agent") }, ignoreOrder: true);
    }

    [Fact]
    public void Output_passes_the_real_validator_for_a_bare_profile()
    {
        var result = RealValidator().Validate(Builder.Build(Context()));

        result.IsValid.ShouldBeTrue(customMessage: "a bare-profile plan-map-dynamic definition must pass DefinitionValidator: " + string.Join(" | ", result.Errors));
    }

    [Fact]
    public void Output_passes_the_real_validator_for_a_full_profile()
    {
        var profile = new ResolvedAgentProfile
        {
            RepositoryId = Guid.NewGuid(),
            Harness = "claude-code",
            Model = "claude-sonnet",
            RunnerKind = "local",
            AutonomyLevel = "Trusted",
            AllowedTools = new[] { "Read", "Grep" },
        };

        RealValidator().Validate(Builder.Build(Context(profile))).IsValid.ShouldBeTrue();
    }

    [Fact]
    public void Planner_is_a_flat_plan_author_bound_to_the_seed_goal()
    {
        var def = Builder.Build(Context());
        var planner = def.Nodes.Single(n => n.Id == "planner");

        planner.Config.GetProperty("flatPlan").GetBoolean().ShouldBeTrue("the parallel map cannot honor ordering");
        planner.Inputs.GetProperty("goal").GetString().ShouldBe("Improve the onboarding module");
    }

    [Fact]
    public void Map_binds_items_to_the_planner_subtasks_output()
    {
        var map = Builder.Build(Context()).Nodes.Single(n => n.Id == "map");

        map.Inputs.GetProperty("items").GetString().ShouldBe("{{nodes.planner.outputs.json.subtasks}}",
            "the map fans out over the planner's typed subtasks array — an object array binds the same as a string array");
    }

    [Fact]
    public void Agent_body_goal_binds_to_the_per_branch_item_goal()
    {
        var agent = Builder.Build(Context()).Nodes.Single(n => n.Id == "agent");

        agent.Config.GetProperty("goal").GetString().ShouldBe("{{item.instruction}}",
            "each branch's goal is its own plan item's authored instruction");
    }

    [Fact]
    public void Agent_body_mode_binds_to_the_per_branch_item_mode()
    {
        var agent = Builder.Build(Context()).Nodes.Single(n => n.Id == "agent");

        agent.Config.GetProperty("mode").GetString().ShouldBe("{{item.kind}}",
            "each branch's mode carries the MODEL's chosen intent via the map's {{item.mode}} — the node maps it to permissions + push");
    }

    [Fact]
    public void Map_config_carries_the_route_parallelism_cap_so_the_fanout_is_bounded()
    {
        var map = Builder.Build(Context(caps: new RouteCaps { MaxParallelism = 3 })).Nodes.Single(n => n.Id == "map");

        map.Config.GetProperty("maxParallelism").GetInt32().ShouldBe(3,
            customMessage: "the route's RouteCaps.MaxParallelism must reach the flow.map Config or the fan-out ignores the cap");
    }

    [Fact]
    public void Synth_is_a_real_llm_reduce_over_the_whole_results_array_generic_over_subtask_count()
    {
        var def = Builder.Build(Context());
        var synth = def.Nodes.Single(n => n.Id == "synth");

        synth.TypeKey.ShouldBe("llm.complete");
        synth.Config.GetProperty("provider").GetString().ShouldBe("Anthropic");

        var userPrompt = synth.Inputs.GetProperty("userPrompt").GetString()!;
        userPrompt.ShouldContain("{{nodes.map.outputs.results}}", customMessage: "the synth reduce binds the whole map results array (generic over any subtask count)");
        userPrompt.ShouldContain("Improve the onboarding module", customMessage: "the reduce prompt embeds the seed goal so the synthesis addresses the goal");

        var done = def.Nodes.Single(n => n.Id == "done");
        done.TypeKey.ShouldBe("builtin.terminal");
        done.Inputs.GetProperty("combined").GetString().ShouldBe("{{nodes.synth.outputs.text}}");
    }

    [Fact]
    public void Profile_maps_onto_the_planner_model_and_the_agent_body()
    {
        var repoId = Guid.NewGuid();
        var profile = new ResolvedAgentProfile { RepositoryId = repoId, Harness = "claude-code", Model = "claude-sonnet" };

        var def = Builder.Build(Context(profile));

        // The planner is plan.author: its model pins by ROW at launch, never by the profile's model NAME.
        def.Nodes.Single(n => n.Id == "planner").Config.TryGetProperty("model", out _).ShouldBeFalse();

        var agent = def.Nodes.Single(n => n.Id == "agent");
        agent.Config.GetProperty("harness").GetString().ShouldBe("claude-code", "the agent body uses the shared profile mapping");
        agent.Inputs.GetProperty("repositoryId").GetString().ShouldBe(repoId.ToString(), "the body's repositoryId binds from the profile");
    }
}
