using System.Text.Json;
using CodeSpace.Core.Services.Tasks.Projection.Builders.PlanMapSynth;
using CodeSpace.Core.Services.Workflows.Engine;
using CodeSpace.Core.Services.Workflows.Llm;
using CodeSpace.Core.Services.Workflows.Nodes;
using CodeSpace.Core.Services.Workflows.Nodes.Builtin;
using CodeSpace.Messages.Dtos.Workflows;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Tasks;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

/// <summary>
/// Pins the plan-map-synth projection builder: the emitted graph is
/// <c>trigger.manual → llm.complete(planner, responseSchema) → flow.map(items=planner.json.subtasks) →
/// flow.map_start → agent.code(body, {{item}}) → llm.complete(synth, REAL reduce over the results array) →
/// builtin.terminal(done, combined=synth.text)</c>, it ALWAYS passes the REAL <see cref="DefinitionValidator"/>
/// over the real node manifests (so the planner's json output, the map items binding, the synth's prompt refs,
/// and the done node's synth-text ref all validate), and the <see cref="ResolvedAgentProfile"/> + seed goal map
/// onto the planner model + the agent.code body via the SAME shared mapping the single-agent builder uses.
/// </summary>
[Trait("Category", "Unit")]
[Collection("DefaultHarnessEnvMutation")]   // an absent-harness build reads the unset default harness — serialize with the env-mutating AgentHarnessDefaultsTests
public class PlanMapSynthDefinitionBuilderTests
{
    private static readonly PlanMapSynthDefinitionBuilder Builder = new();

    /// <summary>The REAL validator over the REAL node runtimes the builder emits — the planner json output, the map structure, and the synth result refs all validate against the actual manifests. LlmCompleteNode needs a registry; the validator only reads its manifest, so an empty client list suffices.</summary>
    private static DefinitionValidator RealValidator() => new(new NodeRegistry(new INodeRuntime[]
    {
        new TriggerManualNode(),
        new LlmCompleteNode(new LLMClientRegistry(Array.Empty<ILLMClient>()), null!),
        new PlanAuthorNode(null!),
        new PlanConfirmNode(null!),
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
        byId["planner"].ShouldBe("plan.author");
        byId["map"].ShouldBe("flow.map");
        byId["ms"].ShouldBe("flow.map_start");
        byId["agent"].ShouldBe("agent.code");
        byId["synth"].ShouldBe("llm.complete");   // the synth is a REAL llm.complete reduce now, not a builtin.terminal raw-bind
        byId["done"].ShouldBe("builtin.terminal");

        // The body nodes are parented to the map so the engine fans them out per subtask.
        def.Nodes.Single(n => n.Id == "ms").ParentId.ShouldBe("map");
        def.Nodes.Single(n => n.Id == "agent").ParentId.ShouldBe("map");

        def.Edges.Select(e => (e.From, e.To)).ShouldBe(
            new[] { ("start", "planner"), ("planner", "map"), ("map", "synth"), ("synth", "done"), ("ms", "agent") }, ignoreOrder: true);
    }

    [Fact]
    public void The_map_body_agent_node_carries_the_default_transient_retry()
    {
        // The fan-out body inherits the same respawn budget as the single-agent lane: one transient branch death
        // re-stages a fresh agent for THAT branch instead of sinking the whole map.
        var retry = Builder.Build(Context()).Nodes.Single(n => n.Id == "agent").Retry;

        retry.ShouldNotBeNull();
        retry.MaxAttempts.ShouldBe(3);
        retry.BackoffSeconds.ShouldBe(30);
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
    public void Planner_is_a_flat_plan_author_bound_to_the_seed_goal()
    {
        var def = Builder.Build(Context());
        var planner = def.Nodes.Single(n => n.Id == "planner");

        // The plan is FLAT: the parallel map cannot honor ordering, so the planner is constrained + stripped.
        planner.Config.GetProperty("flatPlan").GetBoolean().ShouldBeTrue();

        // No critic / no pin by default — the config carries ONLY the flat constraint (byte-stable baseline).
        planner.Config.EnumerateObject().Select(o => o.Name).ShouldBe(new[] { "flatPlan" });

        planner.Inputs.GetProperty("goal").GetString().ShouldBe("Improve the onboarding module");
        planner.Inputs.TryGetProperty("grounding", out _).ShouldBeFalse("no launch grounding ⇒ the key is omitted");
    }

    [Fact]
    public void The_planner_critic_and_pinned_model_ride_the_plan_author_config()
    {
        var context = Context() with { PlannerModelRowId = Guid.Parse("99999999-9999-9999-9999-999999999999"), PlannerReviewMode = ReviewMode.Improve, ReviewerModelId = Guid.Parse("88888888-8888-8888-8888-888888888888") };

        var planner = Builder.Build(context).Nodes.Single(n => n.Id == "planner");

        planner.Config.GetProperty("plannerModelId").GetString().ShouldBe("99999999-9999-9999-9999-999999999999");
        planner.Config.GetProperty("reviewMode").GetInt32().ShouldBe((int)ReviewMode.Improve);
        planner.Config.GetProperty("reviewerModelId").GetString().ShouldBe("88888888-8888-8888-8888-888888888888");
    }

    [Fact]
    public void The_reviewer_model_is_omitted_when_the_planner_critic_is_off()
    {
        var context = Context() with { PlannerReviewMode = ReviewMode.None, ReviewerModelId = Guid.NewGuid() };

        var planner = Builder.Build(context).Nodes.Single(n => n.Id == "planner");

        planner.Config.TryGetProperty("reviewMode", out _).ShouldBeFalse("None ⇒ omitted ⇒ byte-identical");
        planner.Config.TryGetProperty("reviewerModelId", out _).ShouldBeFalse("a reviewer without a review would not be byte-identical");
    }

    [Fact]
    public void The_launch_base_pin_rides_the_planner_config_only_with_a_grounded_reviewer()
    {
        // S1: the grounded plan reviewer must clone the SAME commit the fan-out agents materialize.
        var repoId = Guid.NewGuid();
        var profile = new ResolvedAgentProfile { RepositoryId = repoId, ReviewerAgent = true };
        var pins = new Dictionary<Guid, string> { [repoId] = "abc123def456" };

        var grounded = Context(profile) with { PlannerReviewMode = ReviewMode.Gate, PinnedShas = pins };
        Builder.Build(grounded).Nodes.Single(n => n.Id == "planner").Config.GetProperty("pinnedSha").GetString().ShouldBe("abc123def456");

        var noReviewer = Context(profile with { ReviewerAgent = false }) with { PlannerReviewMode = ReviewMode.Gate, PinnedShas = pins };
        Builder.Build(noReviewer).Nodes.Single(n => n.Id == "planner").Config.TryGetProperty("pinnedSha", out _)
            .ShouldBeFalse("no grounded reviewer ⇒ nothing clones at plan time ⇒ the key is omitted (byte-identical)");

        var noPin = Context(profile) with { PlannerReviewMode = ReviewMode.Gate };
        Builder.Build(noPin).Nodes.Single(n => n.Id == "planner").Config.TryGetProperty("pinnedSha", out _)
            .ShouldBeFalse("no vector ⇒ no pin key — the reviewer clones the default tip (legacy)");
    }

    [Fact]
    public void The_confirm_gate_inserts_the_park_and_rebinds_the_map_to_the_approved_outputs()
    {
        var def = Builder.Build(Context() with { RequirePlanConfirmation = true, PlannerModelRowId = Guid.Parse("99999999-9999-9999-9999-999999999999") });

        var confirm = def.Nodes.Single(n => n.Id == "confirm");
        confirm.TypeKey.ShouldBe("plan.confirm");
        confirm.Config.GetProperty("flatPlan").GetBoolean().ShouldBeTrue("revisions are as flat as the original — the parallel map cannot honor ordering");
        confirm.Config.GetProperty("plannerModelId").GetString().ShouldBe("99999999-9999-9999-9999-999999999999", "revisions re-plan on the SAME pinned model as the planner");

        def.Edges.Select(e => (e.From, e.To)).ShouldBe(
            new[] { ("start", "planner"), ("planner", "confirm"), ("confirm", "map"), ("map", "synth"), ("synth", "done"), ("ms", "agent") }, ignoreOrder: true);

        def.Nodes.Single(n => n.Id == "map").Inputs.GetProperty("items").GetString()
            .ShouldBe("{{nodes.confirm.outputs.json.subtasks}}", "the map binds the CONFIRM node — always the APPROVED version, never a rejected one");

        RealValidator().Validate(def).IsValid.ShouldBeTrue(customMessage: "the gated graph passes the real validator");
    }

    [Fact]
    public void Without_the_gate_the_graph_has_no_confirm_node_and_binds_the_planner_directly()
    {
        var def = Builder.Build(Context());

        def.Nodes.Any(n => n.Id == "confirm").ShouldBeFalse("gate off ⇒ byte-identical pre-gate graph");
        def.Nodes.Single(n => n.Id == "map").Inputs.GetProperty("items").GetString().ShouldBe("{{nodes.planner.outputs.json.subtasks}}");
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

        agent.Config.GetProperty("goal").GetString().ShouldBe("{{item.instruction}}",
            "each branch's goal is its own plan item's authored instruction");
    }

    [Fact]
    public void Synth_is_a_real_llm_reduce_over_the_whole_results_array_generic_over_subtask_count()
    {
        var def = Builder.Build(Context());
        var synth = def.Nodes.Single(n => n.Id == "synth");

        // The synth is a REAL llm.complete reduce (provider default Anthropic), NOT a builtin.terminal raw-bind.
        synth.TypeKey.ShouldBe("llm.complete");
        synth.Config.GetProperty("provider").GetString().ShouldBe("Anthropic");

        // The userPrompt embeds the seed goal AND the WHOLE results array — generic over ANY subtask count, NOT a
        // fixed element-indexed width — so the reduce sees every fanned-out branch regardless of subtask count.
        var userPrompt = synth.Inputs.GetProperty("userPrompt").GetString()!;
        userPrompt.ShouldContain("{{nodes.map.outputs.results}}",
            customMessage: "the synth reduce binds the whole map results array (generic over any subtask count)");
        userPrompt.ShouldContain("Improve the onboarding module",
            customMessage: "the reduce prompt embeds the seed goal so the synthesis addresses the goal, not just the branch results");

        // The done terminal binds the synth's reduced text into the run's combined output.
        var done = def.Nodes.Single(n => n.Id == "done");
        done.TypeKey.ShouldBe("builtin.terminal");
        done.Inputs.GetProperty("combined").GetString().ShouldBe("{{nodes.synth.outputs.text}}",
            "the done node surfaces the synth's reduced text as the run's combined output");
    }

    [Fact]
    public void Profile_maps_onto_the_planner_model_and_the_agent_body()
    {
        var repoId = Guid.NewGuid();
        var profile = new ResolvedAgentProfile { RepositoryId = repoId, Harness = "claude-code", Model = "claude-sonnet" };

        var def = Builder.Build(Context(profile));

        // The planner is plan.author: its model is pinned by ROW at launch (PlannerModelRowId), never by the
        // profile's model NAME — the name still drives the agent body + synth via the shared mapping.
        def.Nodes.Single(n => n.Id == "planner").Config.TryGetProperty("model", out _).ShouldBeFalse();

        var agent = def.Nodes.Single(n => n.Id == "agent");
        agent.Config.GetProperty("harness").GetString().ShouldBe("claude-code", "the agent body uses the shared profile mapping");
        agent.Inputs.GetProperty("repositoryId").GetString().ShouldBe(repoId.ToString(), "the body's repositoryId binds from the profile");
    }

    [Fact]
    public void Bare_profile_planner_omits_the_model_pin_inheriting_the_node_auto_pick()
    {
        var planner = Builder.Build(Context()).Nodes.Single(n => n.Id == "planner");

        planner.Config.TryGetProperty("plannerModelId", out _).ShouldBeFalse("no launch pin ⇒ plan.author auto-picks the team's strongest structured model");
        Builder.Build(Context()).Nodes.Single(n => n.Id == "agent").Config.GetProperty("harness").GetString().ShouldBe("codex-cli",
            "a null harness folds to the agent.code catalog default");
    }
}
