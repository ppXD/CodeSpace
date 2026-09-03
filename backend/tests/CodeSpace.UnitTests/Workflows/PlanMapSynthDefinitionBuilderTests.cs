using System.Text.Json;
using CodeSpace.Core.Services.Tasks.Projection.Builders.PlanMap;
using CodeSpace.Core.Services.Tasks.Projection.Builders.PlanMapSynth;
using CodeSpace.Core.Services.Workflows.Engine;
using CodeSpace.Messages.Constants;
using CodeSpace.Core.Services.Workflows.Llm;
using CodeSpace.Core.Services.Workflows.Nodes;
using CodeSpace.Core.Services.Workflows.Nodes.Builtin;
using CodeSpace.Core.Services.Workflows.Runtime;
using CodeSpace.Messages.Dtos.Workflows;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Tasks;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

/// <summary>
/// Pins the plan-map-synth projection builder: the emitted graph is
/// <c>trigger.manual → llm.complete(planner, responseSchema) → flow.map(items=planner.json.subtasks) →
/// flow.map_start → agent.run(body, {{item}}) → llm.complete(synth, REAL reduce over the results array) →
/// builtin.terminal(done, combined=synth.text)</c>, it ALWAYS passes the REAL <see cref="DefinitionValidator"/>
/// over the real node manifests (so the planner's json output, the map items binding, the synth's prompt refs,
/// and the done node's synth-text ref all validate), and the <see cref="ResolvedAgentProfile"/> + seed goal map
/// onto the planner model + the agent.run body via the SAME shared mapping the single-agent builder uses.
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
        new GitIntegrateRunNode(null!, null!, null!, null!),
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
        byId["agent"].ShouldBe("agent.run");
        byId["synth"].ShouldBe("llm.complete");   // the synth is a REAL llm.complete reduce now, not a builtin.terminal raw-bind
        byId["done"].ShouldBe("builtin.terminal");

        // The body nodes are parented to the map so the engine fans them out per subtask.
        def.Nodes.Single(n => n.Id == "ms").ParentId.ShouldBe("map");
        def.Nodes.Single(n => n.Id == "agent").ParentId.ShouldBe("map");

        def.Edges.Select(e => (e.From, e.To)).ShouldBe(
            new[] { ("start", "planner"), ("planner", "map"), ("map", "synth"), ("synth", "done"), ("ms", "agent") }, ignoreOrder: true);
    }

    [Fact]
    public void A_repo_bound_graph_integrates_before_the_narration_reduce()
    {
        // P4 (the plan-map integrated candidate): a repo-bound fan-out gains the run-sourced integrate step
        // sequenced map → integrate → synth, and the done terminal surfaces the candidate (branch + status)
        // as run outputs beside the narrated `combined`.
        var repositoryId = Guid.NewGuid();
        var def = Builder.Build(Context(new ResolvedAgentProfile { RepositoryId = repositoryId, Harness = "claude-code" }));

        var integrate = def.Nodes.Single(n => n.Id == "integrate");
        integrate.TypeKey.ShouldBe("git.integrate_run");
        integrate.Inputs.GetProperty("repositoryId").GetString().ShouldBe(repositoryId.ToString());
        integrate.Config.GetProperty("parkOnConflict").GetBoolean().ShouldBeTrue("a conflicted candidate parks for review — fragments never narrate past a human silently");

        def.Edges.Select(e => (e.From, e.To)).ShouldContain(("map", "integrate"));
        def.Edges.Select(e => (e.From, e.To)).ShouldContain(("integrate", "synth"));
        def.Edges.Select(e => (e.From, e.To)).ShouldNotContain(("map", "synth"));

        var done = def.Nodes.Single(n => n.Id == "done").Inputs;
        done.GetProperty("integrationStatus").GetString().ShouldBe("{{nodes.integrate.outputs.status}}");
        done.GetProperty("integratedBranch").GetString().ShouldBe("{{nodes.integrate.outputs.integratedBranch}}");
    }

    [Fact]
    public void A_repo_less_graph_stays_byte_identical_with_no_integrate_step()
    {
        var def = Builder.Build(Context());

        def.Nodes.ShouldNotContain(n => n.Id == "integrate", customMessage: "a repo-less task has nothing to integrate — the graph must not change hash");
        def.Edges.Select(e => (e.From, e.To)).ShouldContain(("map", "synth"));
        def.Nodes.Single(n => n.Id == "done").Inputs.TryGetProperty("integrationStatus", out _).ShouldBeFalse();
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
    public void Map_config_carries_the_route_cost_cap_so_the_fanout_is_bounded_by_budget_too()
    {
        var map = Builder.Build(Context(caps: new RouteCaps { MaxCostUsd = 7.5m })).Nodes.Single(n => n.Id == "map");

        // The router computes a spend ceiling for every lane and the supervisor builder has always passed its own
        // through. This one dropped it, so a Standard operator could set a budget the engine then ignored: the deep
        // lane refused work over its cap while the map lane fanned out until the model plane or the clock stopped it.
        map.Config.GetProperty("maxCostUsd").GetDecimal().ShouldBe(7.5m,
            customMessage: "the route's RouteCaps.MaxCostUsd must reach the flow.map Config or the fan-out spends past the operator's budget");
    }

    [Fact]
    public void Map_config_writes_no_cap_keys_when_the_route_sets_none_so_an_absent_cap_stays_unbounded()
    {
        var map = Builder.Build(Context(caps: new RouteCaps())).Nodes.Single(n => n.Id == "map");

        // Absent cap ⇒ the prior behaviour: no key, the map inherits the engine-wide default.
        map.Config.TryGetProperty("maxParallelism", out _).ShouldBeFalse(
            "no cap set must leave the map unbounded — only write the key when the route actually caps parallelism");
        map.Config.TryGetProperty("maxCostUsd", out _).ShouldBeFalse(
            "same for the budget: an absent cap must not write the key");

        // The Config is no longer the empty object it was before the reduce gained an input bound: promptBudgetChars
        // and the error policy ride on EVERY plan-map graph, because a capless route is exactly the common case whose
        // reduce prompt used to grow without limit — and whose fan-out must survive one branch's death. Those two are
        // the ONLY unconditional keys — a cap the route did not set still writes nothing.
        map.Config.EnumerateObject().Select(p => p.Name).ShouldBe(new[] { "errorHandling", "promptBudgetChars" },
            customMessage: "a capless map Config carries the error policy + the reduce's input budget and nothing else");
    }

    /// <summary>
    /// One subtask's death must not destroy its siblings' work. The <c>flow.map</c> schema DEFAULTS to
    /// <c>terminate</c>: a branch that exhausted its attempts failed the whole map, which skips
    /// <c>git.integrate_run</c> AND the synth reduce — so N-1 successful subtasks survived only as per-agent
    /// branches nobody was told about, and the run's own outputs came out empty. The projection must therefore
    /// declare the policy explicitly; inheriting the node default is the defect.
    /// </summary>
    [Fact]
    public void Map_config_declares_continue_on_error_so_one_dead_branch_never_discards_its_siblings_work()
    {
        var map = Builder.Build(Context()).Nodes.Single(n => n.Id == "map");

        map.Config.GetProperty("errorHandling").GetString().ShouldBe("continue",
            customMessage: "the plan-map fan-out must keep going and mark failures — under the schema's 'terminate' default one failed subtask skips integrate + synth and the run loses every sibling's work");
    }

    /// <summary>
    /// Continue-on-error only pays off if the reduce TELLS THE TRUTH about it: the run now reaches Success with a
    /// failed subtask inside it, so a reduce that read an <c>{"error": ...}</c> entry as just another result would
    /// narrate a partial run as a whole one. The instruction names the marker and demands the failures be named.
    /// </summary>
    [Fact]
    public void The_reduce_is_told_what_a_failed_subtask_looks_like_so_a_partial_answer_cannot_read_as_a_whole_one()
    {
        var systemPrompt = Builder.Build(Context()).Nodes.Single(n => n.Id == "synth").Inputs.GetProperty("systemPrompt").GetString()!;

        systemPrompt.ShouldBe(PlanMapBuilderBase.SynthSystemPrompt);
        systemPrompt.ShouldContain("error", Case.Insensitive, customMessage: "the reduce must know a failed branch appears as an {\"error\": ...} entry, not as a result");
        systemPrompt.ShouldContain("failed", Case.Insensitive, customMessage: "the reduce is instructed to name the failed subtasks and what is missing because of them");
    }

    /// <summary>
    /// The other arm: the per-run DATA half of the reduce is byte-identical to the pre-continue prompt — the goal
    /// and the bounded results projection, nothing else. A run in which nothing failed must not be handed a
    /// "Subtasks that failed: 0" line or any other failure furniture; the failure instruction is generic and lives
    /// in the system prompt, and the per-run FACT lives where a reader can check it (the map bag + the run row).
    /// A build-time conditional cannot do this job: the failure count is a run-time fact and the prompt is frozen
    /// into the definition at build time.
    /// </summary>
    [Fact]
    public void The_reduces_user_prompt_carries_no_failure_furniture_so_a_clean_run_is_byte_identical()
    {
        var userPrompt = Builder.Build(Context()).Nodes.Single(n => n.Id == "synth").Inputs.GetProperty("userPrompt").GetString()!;

        userPrompt.ShouldBe($"Goal: Improve the onboarding module\n\nPer-subtask results:\n{{{{nodes.map.outputs.{WorkflowOutputKeys.MapResultsPrompt}}}}}",
            customMessage: "the data half of the reduce prompt must not drift for a run that failed nothing");

        userPrompt.ShouldNotContain(WorkflowOutputKeys.MapFailed,
            customMessage: "the failed count is a persisted fact on the map bag + the run row — it is not furniture on every happy run's prompt");
    }

    /// <summary>The other half of the same fact: the failed-branch count reaches the RUN ROW beside the combined answer, so a partial result is legible from the run's outcome and not only from the map node's bag (the coverage binding's reasoning, applied to the second way an answer can be less than whole).</summary>
    [Fact]
    public void The_done_terminal_surfaces_the_failed_branch_count_beside_the_combined_answer()
    {
        var done = Builder.Build(Context()).Nodes.Single(n => n.Id == "done");

        done.Inputs.GetProperty(WorkflowOutputKeys.MapFailed).GetString()
            .ShouldBe($"{{{{nodes.map.outputs.{WorkflowOutputKeys.MapFailed}}}}}");
    }

    /// <summary>
    /// The planner types each item with an open kind, and the DEFAULT lane must read it too: <c>agent.run</c> maps a
    /// recognised <c>research</c> kind to read-only + no produced branch under the autonomy ceiling. Privilege only
    /// lowers, so this is safe on the standard projection — the opt-in dynamic sibling was never the only place a
    /// plan item's kind meant something.
    /// </summary>
    [Fact]
    public void Agent_body_mode_binds_to_the_per_branch_item_kind()
    {
        var agent = Builder.Build(Context()).Nodes.Single(n => n.Id == "agent");

        agent.Config.GetProperty("mode").GetString().ShouldBe("{{item.kind}}",
            customMessage: "the plan item's kind must reach the body, or the planner's per-item posture decision is thrown away");
    }

    /// <summary>
    /// The planner is invited to pick each subtask's best-fit model from the team's catalog; on the Launch path that
    /// pick had no binding at all, so every branch ran the profile's model. It now binds as a FALLBACK — an
    /// operator-pinned profile model still wins outright, because an operator's choice is never overridden by a
    /// model-authored one.
    /// </summary>
    [Fact]
    public void The_body_model_binds_the_plan_items_pick_only_when_the_profile_pins_none()
    {
        Builder.Build(Context()).Nodes.Single(n => n.Id == "agent").Config.GetProperty("model").GetString()
            .ShouldBe("{{item.model}}", customMessage: "with no operator pin, each branch runs the model its own plan item asked for");

        Builder.Build(Context(new ResolvedAgentProfile { Model = "claude-sonnet" })).Nodes.Single(n => n.Id == "agent").Config.GetProperty("model").GetString()
            .ShouldBe("claude-sonnet", customMessage: "an operator-pinned model wins on every branch — a model-authored pick never overrides it");
    }

    /// <summary>
    /// The per-item binding resolved through the REAL <see cref="VariableResolver"/>, both ways: an item that named a
    /// model resolves to that name, and an item that named none resolves to JSON null — which <c>AgentCodeNode</c>
    /// reads as an ABSENT model (<c>ReadOptionalString</c> returns null for any non-string kind), i.e. exactly the
    /// task a bare profile built before this binding existed. That second arm is the whole safety of the change.
    /// </summary>
    [Fact]
    public void The_item_model_binding_resolves_per_branch_and_an_item_that_named_none_resolves_to_no_model()
    {
        var config = Builder.Build(Context()).Nodes.Single(n => n.Id == "agent").Config;

        var authored = VariableResolver.ResolveBag(config, BranchScope("""{ "instruction": "do it", "model": "gpt-5-codex" }"""));
        authored["model"].GetString().ShouldBe("gpt-5-codex");

        var bare = VariableResolver.ResolveBag(config, BranchScope("""{ "instruction": "do it" }"""));
        bare["model"].ValueKind.ShouldBe(JsonValueKind.Null,
            customMessage: "an item with no model must resolve to null — AgentCodeNode reads that as no model at all, so the branch runs the harness/credential default exactly as it did before");

        bare["mode"].ValueKind.ShouldBe(JsonValueKind.Null,
            customMessage: "same for an untyped item: a null mode is AgentMode.Unset, the tier-derived posture (byte-identical to a no-mode node)");
    }

    /// <summary>A map-branch scope carrying ONE plan item as <c>{{item}}</c> — the same Iteration slot the engine's BuildMapBranchScope fills per element.</summary>
    private static NodeRunScope BranchScope(string itemJson) => new()
    {
        Trigger = new Dictionary<string, JsonElement>(),
        Iteration = new Dictionary<string, JsonElement> { ["item"] = JsonDocument.Parse(itemJson).RootElement.Clone() },
    };

    [Fact]
    public void Agent_body_goal_binds_to_the_per_branch_item()
    {
        var agent = Builder.Build(Context()).Nodes.Single(n => n.Id == "agent");

        agent.Config.GetProperty("goal").GetString().ShouldBe("{{item.instruction}}",
            "each branch's goal is its own plan item's authored instruction");
    }

    [Fact]
    public void Synth_is_a_real_llm_reduce_over_the_bounded_results_projection_generic_over_subtask_count()
    {
        var def = Builder.Build(Context());
        var synth = def.Nodes.Single(n => n.Id == "synth");

        // The synth is a REAL llm.complete reduce (provider inherited from the team pool), NOT a builtin.terminal raw-bind.
        synth.TypeKey.ShouldBe("llm.complete");
        synth.Config.TryGetProperty("provider", out _).ShouldBeFalse("omission inherits the team's ranked provider without inventing a pseudo-wire enum member");

        // The userPrompt embeds the seed goal AND the map's results — generic over ANY subtask count, NOT a fixed
        // element-indexed width — through the map's BUDGET-BOUNDED projection, so the reduce sees every fanned-out
        // branch at a small count and a bounded, self-declared excerpt of them at a large one.
        var userPrompt = synth.Inputs.GetProperty("userPrompt").GetString()!;
        userPrompt.ShouldContain($"{{{{nodes.map.outputs.{WorkflowOutputKeys.MapResultsPrompt}}}}}",
            customMessage: "the synth reduce binds the map's bounded results projection (generic over any subtask count)");
        userPrompt.ShouldContain("Improve the onboarding module",
            customMessage: "the reduce prompt embeds the seed goal so the synthesis addresses the goal, not just the branch results");

        // The done terminal binds the synth's reduced text into the run's combined output.
        var done = def.Nodes.Single(n => n.Id == "done");
        done.TypeKey.ShouldBe("builtin.terminal");
        done.Inputs.GetProperty("combined").GetString().ShouldBe("{{nodes.synth.outputs.text}}",
            "the done node surfaces the synth's reduced text as the run's combined output");
    }

    /// <summary>
    /// The reduce's input bound, pinned at both ends: the prompt must NOT bind the raw array (that binding is what
    /// let a wide fan-out build a request past the model's context window and kill the run at its last node), and the
    /// map must declare the budget that makes the bounded projection exist at all. Either half alone is inert — a
    /// prompt bound to a key no map emits resolves to empty, and a budget nothing binds is dead weight.
    /// </summary>
    [Fact]
    public void The_reduce_binds_the_bounded_projection_and_the_map_declares_the_budget_that_produces_it()
    {
        var def = Builder.Build(Context());

        var userPrompt = def.Nodes.Single(n => n.Id == "synth").Inputs.GetProperty("userPrompt").GetString()!;

        userPrompt.ShouldNotContain("{{nodes.map.outputs.results}}",
            customMessage: "binding the raw array is the defect — the prompt would grow without limit with branch count and branch size");

        var mapConfig = def.Nodes.Single(n => n.Id == "map").Config;

        mapConfig.GetProperty("promptBudgetChars").GetInt32()
            .ShouldBe(PlanMapBuilderBase.SynthPromptBudgetChars,
                customMessage: "the budget is resolved at BUILD time and recorded in the definition, so a replay projects against its own snapshot's number");
    }

    /// <summary>
    /// The reduce's input coverage reaches the RUN ROW, not just the map node's bag. <c>combined</c> is one prose
    /// answer that reads as though it addressed every subtask; when the reduce was handed an excerpt it did not, and
    /// the only in-band signal of that was a sentence in the prompt. Binding the coverage the map recorded into a run
    /// output puts the fact beside the answer it qualifies, where anyone reading the run's outcome sees it.
    /// </summary>
    [Fact]
    public void The_done_terminal_surfaces_the_reduce_input_coverage_beside_the_combined_answer()
    {
        var done = Builder.Build(Context()).Nodes.Single(n => n.Id == "done");

        done.Inputs.GetProperty(WorkflowOutputKeys.MapResultsCoverage).GetString()
            .ShouldBe($"{{{{nodes.map.outputs.{WorkflowOutputKeys.MapResultsCoverage}}}}}",
                customMessage: "a sole-placeholder binding resolves to the whole recorded object, so the run output carries the fact intact");
    }

    /// <summary>Rule 8 pin: an operator whose pool serves a narrower-context model pins the reduce's input budget by env. A rename would silently restore the unbounded-in-practice behaviour for them, so the literal is pinned here.</summary>
    [Fact]
    public void SynthPromptBudgetCharsEnvVar_constant_name_pinned()
    {
        PlanMapBuilderBase.SynthPromptBudgetCharsEnvVar.ShouldBe("CODESPACE_PLAN_MAP_SYNTH_PROMPT_BUDGET_CHARS");
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
            "a null harness folds to the agent.run catalog default");
    }
}
