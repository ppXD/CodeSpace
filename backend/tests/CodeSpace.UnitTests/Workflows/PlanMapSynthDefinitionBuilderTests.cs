using System.Text.Json;
using CodeSpace.Core.Services.Tasks.Projection.Builders.PlanMapSynth;
using CodeSpace.Core.Services.Workflows.Engine;
using CodeSpace.Core.Services.Workflows.Llm;
using CodeSpace.Core.Services.Workflows.Nodes;
using CodeSpace.Core.Services.Workflows.Nodes.Builtin;
using CodeSpace.Messages.Dtos.Workflows;
using CodeSpace.Messages.Tasks;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

/// <summary>
/// Pins the plan-map-synth projection builder: the emitted graph MIRRORS the proven HeadlineFlow shape
/// (<c>trigger.manual → llm.complete(planner, responseSchema) → flow.map(items=planner.json.subtasks) →
/// flow.map_start → agent.code(body, {{item}}) → builtin.terminal(synth, results[i].summary)</c>), it ALWAYS
/// passes the REAL <see cref="DefinitionValidator"/> over the real node manifests (so the planner's json output,
/// the map items binding, and the synth's results refs all validate), and the
/// <see cref="ResolvedAgentProfile"/> + seed goal map onto the planner model + the agent.code body via the
/// SAME shared mapping the single-agent builder uses.
/// </summary>
[Trait("Category", "Unit")]
public class PlanMapSynthDefinitionBuilderTests
{
    private static readonly PlanMapSynthDefinitionBuilder Builder = new();

    /// <summary>The REAL validator over the REAL node runtimes the builder emits — the planner json output, the map structure, and the synth result refs all validate against the actual manifests. LlmCompleteNode needs a registry; the validator only reads its manifest, so an empty client list suffices.</summary>
    private static DefinitionValidator RealValidator() => new(new NodeRegistry(new INodeRuntime[]
    {
        new TriggerManualNode(),
        new LlmCompleteNode(new LLMClientRegistry(Array.Empty<ILLMClient>())),
        new FlowMapNode(),
        new FlowMapStartNode(),
        new AgentCodeNode(),
        new TerminalNode(),
    }));

    private static TaskBuildContext Context(ResolvedAgentProfile? profile = null, Guid? seedRepo = null, RouteCaps? caps = null) => new()
    {
        Seed = new TaskLaunchSeed { Goal = "Improve the onboarding module", SurfaceKind = "chat", TeamId = Guid.NewGuid(), RepositoryId = seedRepo },
        Route = new RoutePlan { ProjectionKind = TaskProjectionKinds.PlanMapSynth, Caps = caps ?? new RouteCaps() },
        AgentProfile = profile,
    };

    [Fact]
    public void Reports_the_plan_map_synth_projection_kind()
    {
        Builder.ProjectionKind.ShouldBe(TaskProjectionKinds.PlanMapSynth);
    }

    [Fact]
    public void Emits_the_planner_map_agent_synth_graph()
    {
        var def = Builder.Build(Context());

        var byId = def.Nodes.ToDictionary(n => n.Id, n => n.TypeKey);
        byId["start"].ShouldBe("trigger.manual");
        byId["planner"].ShouldBe("llm.complete");
        byId["map"].ShouldBe("flow.map");
        byId["ms"].ShouldBe("flow.map_start");
        byId["agent"].ShouldBe("agent.code");
        byId["synth"].ShouldBe("builtin.terminal");

        // The body nodes are parented to the map so the engine fans them out per subtask.
        def.Nodes.Single(n => n.Id == "ms").ParentId.ShouldBe("map");
        def.Nodes.Single(n => n.Id == "agent").ParentId.ShouldBe("map");

        def.Edges.Select(e => (e.From, e.To)).ShouldBe(
            new[] { ("start", "planner"), ("planner", "map"), ("map", "synth"), ("ms", "agent") }, ignoreOrder: true);
    }

    [Fact]
    public void Output_passes_the_real_validator_for_a_bare_profile()
    {
        var result = RealValidator().Validate(Builder.Build(Context()));

        result.IsValid.ShouldBeTrue(customMessage: "a bare-profile plan-map-synth definition must pass DefinitionValidator: " + string.Join(" | ", result.Errors));
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
    public void Planner_emits_a_subtasks_response_schema_and_frames_the_seed_goal()
    {
        var def = Builder.Build(Context());
        var planner = def.Nodes.Single(n => n.Id == "planner");

        // The responseSchema constrains the model to { subtasks: string[] } — the EXACT shape the map binds.
        var schema = planner.Config.GetProperty("responseSchema");
        schema.GetProperty("properties").GetProperty("subtasks").GetProperty("type").GetString().ShouldBe("array");
        schema.GetProperty("required").EnumerateArray().Select(e => e.GetString()).ShouldContain("subtasks");

        // The seed goal is framed into the decompose instruction (the userPrompt the structured LLM completes).
        planner.Inputs.GetProperty("userPrompt").GetString().ShouldContain("Improve the onboarding module");
    }

    [Fact]
    public void Map_binds_items_to_the_planner_subtasks_output()
    {
        var map = Builder.Build(Context()).Nodes.Single(n => n.Id == "map");

        map.Inputs.GetProperty("items").GetString().ShouldBe("{{nodes.planner.outputs.json.subtasks}}",
            "the map fans out over the planner's typed subtasks array — the exact headline binding");
    }

    [Fact]
    public void Map_config_carries_the_route_parallelism_cap_so_the_fanout_is_bounded()
    {
        var map = Builder.Build(Context(caps: new RouteCaps { MaxParallelism = 3 })).Nodes.Single(n => n.Id == "map");

        // The engine reads this maxParallelism into the branch SemaphoreSlim (MapConfig → MapPlan); without it the
        // fan-out ran unbounded-parallel, defeating Standard's MaxParallelism=3 bound.
        map.Config.GetProperty("maxParallelism").GetInt32().ShouldBe(3,
            customMessage: "the route's RouteCaps.MaxParallelism must reach the flow.map Config or the fan-out ignores the cap");
    }

    [Fact]
    public void Map_config_is_empty_when_no_parallelism_cap_so_an_absent_cap_stays_unbounded()
    {
        var map = Builder.Build(Context(caps: new RouteCaps())).Nodes.Single(n => n.Id == "map");

        // Absent cap ⇒ the prior behaviour: no key, the map inherits the engine-wide default (no config/hash change).
        map.Config.TryGetProperty("maxParallelism", out _).ShouldBeFalse(
            "no cap set must leave the map unbounded — only write the key when the route actually caps parallelism");
        map.Config.ValueKind.ShouldBe(JsonValueKind.Object);
        map.Config.EnumerateObject().ShouldBeEmpty("a capless map Config stays an empty object, byte-identical to the prior Empty()");
    }

    [Fact]
    public void Agent_body_goal_binds_to_the_per_branch_item()
    {
        var agent = Builder.Build(Context()).Nodes.Single(n => n.Id == "agent");

        agent.Config.GetProperty("goal").GetString().ShouldBe("Work on {{item}}",
            "each branch's goal carries its own subtask via the map's {{item}} — matching the headline body");
    }

    [Fact]
    public void Synth_reduces_the_whole_results_array_generic_over_subtask_count()
    {
        var synth = Builder.Build(Context()).Nodes.Single(n => n.Id == "synth");

        // Binds the WHOLE results array — generic over ANY subtask count, NOT a fixed element-indexed width.
        synth.Inputs.GetProperty("combined").GetString().ShouldBe("{{nodes.map.outputs.results}}",
            "the synth reduces the whole map results array (the headline / WorkflowPlanProjector reduce), so the run output carries every fanned-out branch regardless of how many subtasks the planner emits");
    }

    [Fact]
    public void Profile_maps_onto_the_planner_model_and_the_agent_body()
    {
        var repoId = Guid.NewGuid();
        var profile = new ResolvedAgentProfile { RepositoryId = repoId, Harness = "claude-code", Model = "claude-sonnet" };

        var def = Builder.Build(Context(profile));

        def.Nodes.Single(n => n.Id == "planner").Config.GetProperty("model").GetString().ShouldBe("claude-sonnet",
            "the profile's model maps onto the planner llm.complete");

        var agent = def.Nodes.Single(n => n.Id == "agent");
        agent.Config.GetProperty("harness").GetString().ShouldBe("claude-code", "the agent body uses the shared profile mapping");
        agent.Inputs.GetProperty("repositoryId").GetString().ShouldBe(repoId.ToString(), "the body's repositoryId binds from the profile");
    }

    [Fact]
    public void Bare_profile_planner_omits_the_model_inheriting_the_node_default()
    {
        var planner = Builder.Build(Context()).Nodes.Single(n => n.Id == "planner");

        planner.Config.TryGetProperty("model", out _).ShouldBeFalse("an absent model inherits the node/deployment default");
        Builder.Build(Context()).Nodes.Single(n => n.Id == "agent").Config.GetProperty("harness").GetString().ShouldBe("codex-cli",
            "a null harness folds to the agent.code catalog default");
    }
}
