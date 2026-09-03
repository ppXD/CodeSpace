using CodeSpace.Core.Services.Tasks.Projection.Builders.Supervisor;
using CodeSpace.Core.Services.Workflows.Engine;
using CodeSpace.Core.Services.Workflows.Nodes;
using CodeSpace.Core.Services.Workflows.Nodes.Builtin;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Dtos.Workflows;
using CodeSpace.Messages.Tasks;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

/// <summary>
/// Pins the supervisor projection builder: the emitted graph is the minimal lane shape
/// (<c>trigger.manual → agent.supervisor → builtin.terminal</c>), it ALWAYS passes the REAL
/// <see cref="DefinitionValidator"/> over the real node manifests, and the <c>agent.supervisor</c> node Config
/// carries the goal + the mapped agentProfile + the caps→bounds (maxParallelism / maxTotalSpawns
/// from <c>Route.Caps</c>) + the approvalPolicy on the EXACT camelCase keys the node's ConfigSchema reads. The
/// build is PURE (no lane check, no clamp) — the builder lifts bytes; the node clamps + gates at execution.
/// </summary>
[Trait("Category", "Unit")]
public class SupervisorDefinitionBuilderTests
{
    private static readonly SupervisorDefinitionBuilder Builder = new();

    /// <summary>The REAL validator over the REAL node runtimes the builder emits. AgentSupervisorNode's scope factory is unused for manifest reading, so a null is safe.</summary>
    private static DefinitionValidator RealValidator() => new(new NodeRegistry(new INodeRuntime[]
    {
        new TriggerManualNode(),
        new AgentSupervisorNode(null!),
        new TerminalNode(),
    }));

    private static TaskBuildContext Context(ResolvedAgentProfile? profile = null, RouteCaps? caps = null, Guid? brainModelId = null, bool brainModelPinIneligible = false, IReadOnlyList<Guid>? allowedModelIds = null, IReadOnlyList<Guid>? allowedAgentDefinitionIds = null, IReadOnlyList<string>? acceptanceChecks = null, bool brainModelPinned = false) => new()
    {
        Seed = new TaskLaunchSeed { Goal = "Ship the whole feature", SurfaceKind = "chat", TeamId = Guid.NewGuid() },
        Route = new RoutePlan { ProjectionKind = TaskProjectionKinds.Supervisor, Caps = caps ?? new RouteCaps() },
        AgentProfile = profile,
        SupervisorBrainModelId = brainModelId,
        SupervisorBrainModelPinIneligible = brainModelPinIneligible,
        SupervisorBrainModelPinned = brainModelPinned,
        AllowedModelIds = allowedModelIds,
        AllowedAgentDefinitionIds = allowedAgentDefinitionIds,
        AcceptanceChecks = acceptanceChecks,
    };

    [Fact]
    public void The_launch_completion_mode_stamps_the_built_definition_verbatim()
    {
        // P2b: the tasks lane's Enforced opt-in — the built definition carries the launch's mode so the snapshot
        // starter stamps the run Enforced; absent stays null (platform default), byte-identical.
        Builder.Build(Context() with { CompletionMode = WorkflowDefinition.CompletionModeEnforced })
            .CompletionMode.ShouldBe(WorkflowDefinition.CompletionModeEnforced);
        Builder.Build(Context()).CompletionMode.ShouldBeNull();
    }

    [Fact]
    public void The_plan_critic_bakes_as_the_plan_scoped_review_and_omits_when_off()
    {
        var config = Builder.Build(Context() with { PlannerReviewMode = ReviewMode.Improve }).Nodes.Single(n => n.Id == "sup").Config;

        config.GetProperty("planReviewMode").GetInt32().ShouldBe((int)ReviewMode.Improve,
            customMessage: "the launch's tier-generic plan critic reaches Deep as the PLAN-scoped supervisor review");

        Builder.Build(Context()).Nodes.Single(n => n.Id == "sup").Config.TryGetProperty("planReviewMode", out _)
            .ShouldBeFalse("off ⇒ omitted (byte-identical)");
    }

    [Fact]
    public void The_acceptance_checks_floor_bakes_with_blanks_dropped_and_omits_when_empty()
    {
        // The floor is a VERIFICATION control — it must not silently not-arrive (S4b review finding).
        var config = Builder.Build(Context(acceptanceChecks: new[] { "sh", " ", "check.sh", "" })).Nodes.Single(n => n.Id == "sup").Config;

        config.GetProperty("acceptanceChecks").EnumerateArray().Select(e => e.GetString()).ShouldBe(new[] { "sh", "check.sh" },
            customMessage: "the argv floor bakes verbatim with blank entries dropped");

        Builder.Build(Context()).Nodes.Single(n => n.Id == "sup").Config.TryGetProperty("acceptanceChecks", out _).ShouldBeFalse("no floor ⇒ key omitted (byte-identical)");
        Builder.Build(Context(acceptanceChecks: new[] { " ", "" })).Nodes.Single(n => n.Id == "sup").Config.TryGetProperty("acceptanceChecks", out _).ShouldBeFalse("an all-blank floor collapses to omitted");
    }

    [Fact]
    public void Reports_the_supervisor_projection_kind()
    {
        Builder.ProjectionKind.ShouldBe(TaskProjectionKinds.Supervisor);
    }

    [Fact]
    public void Emits_the_manual_supervisor_terminal_graph()
    {
        var def = Builder.Build(Context());

        var byId = def.Nodes.ToDictionary(n => n.Id, n => n.TypeKey);
        byId["start"].ShouldBe("trigger.manual");
        byId["sup"].ShouldBe("agent.supervisor");
        byId["end"].ShouldBe("builtin.terminal");

        def.Edges.Select(e => (e.From, e.To)).ShouldBe(new[] { ("start", "sup"), ("sup", "end") }, ignoreOrder: true);
    }

    [Fact]
    public void Output_passes_the_real_validator_for_a_bare_context()
    {
        var result = RealValidator().Validate(Builder.Build(Context()));

        result.IsValid.ShouldBeTrue(customMessage: "a bare supervisor definition must pass DefinitionValidator: " + string.Join(" | ", result.Errors));
    }

    [Fact]
    public void Output_passes_the_real_validator_for_a_full_context()
    {
        var profile = new ResolvedAgentProfile { RepositoryId = Guid.NewGuid(), Harness = "claude-code", Model = "claude-sonnet", RunnerKind = "local", AutonomyLevel = "Trusted", EnableMcp = true };
        var caps = new RouteCaps { MaxParallelism = 5, MaxTotalSpawns = 20, RequiresApproval = true };

        RealValidator().Validate(Builder.Build(Context(profile, caps))).IsValid.ShouldBeTrue();
    }

    [Fact]
    public void Supervisor_config_carries_the_seed_goal()
    {
        var sup = Builder.Build(Context()).Nodes.Single(n => n.Id == "sup");

        sup.Config.GetProperty("goal").GetString().ShouldBe("Ship the whole feature", "the seed goal maps onto the supervisor's goal config");
    }

    [Fact]
    public void Supervisor_config_bakes_the_synthesis_prompt_budget_for_replay()
    {
        var config = Builder.Build(Context()).Nodes.Single(n => n.Id == "sup").Config;

        config.GetProperty("synthesisPromptBudgetChars").GetInt32().ShouldBe(SupervisorDefinitionBuilder.SynthesisPromptBudgetChars);
        SupervisorDefinitionBuilder.SynthesisPromptBudgetCharsEnvVar.ShouldBe("CODESPACE_SUPERVISOR_SYNTH_PROMPT_BUDGET_CHARS");
    }

    [Fact]
    public void Supervisor_config_folds_the_caps_into_the_bounds()
    {
        var caps = new RouteCaps { MaxParallelism = 5, MaxTotalSpawns = 20, MaxCostUsd = 12.50m };

        var sup = Builder.Build(Context(caps: caps)).Nodes.Single(n => n.Id == "sup");

        sup.Config.GetProperty("maxParallelism").GetInt32().ShouldBe(5, "the route's parallelism cap folds onto the supervisor bound");
        sup.Config.GetProperty("maxTotalSpawns").GetInt32().ShouldBe(20);
        sup.Config.GetProperty("maxCostUsd").GetDecimal().ShouldBe(12.50m, "the route's cost cap folds onto the supervisor bound as a JSON number (SOTA #4)");
    }

    [Fact]
    public void Supervisor_config_bakes_the_allowed_model_pool_as_a_uuid_string_array()
    {
        var rowA = Guid.NewGuid();
        var rowB = Guid.NewGuid();

        var sup = Builder.Build(Context(allowedModelIds: new[] { rowA, rowB })).Nodes.Single(n => n.Id == "sup");

        sup.Config.GetProperty("allowedModelIds").EnumerateArray().Select(e => e.GetString()).ShouldBe(
            new[] { rowA.ToString(), rowB.ToString() }, "the operator's model pool rows bake onto the supervisor's allowedModelIds in order");
    }

    [Fact]
    public void Supervisor_config_omits_the_allowed_model_pool_when_empty_or_unset()
    {
        Builder.Build(Context(allowedModelIds: null)).Nodes.Single(n => n.Id == "sup")
            .Config.TryGetProperty("allowedModelIds", out _).ShouldBeFalse("a null pool omits the key — the supervisor draws from all the team's models (byte-identical)");

        Builder.Build(Context(allowedModelIds: Array.Empty<Guid>())).Nodes.Single(n => n.Id == "sup")
            .Config.TryGetProperty("allowedModelIds", out _).ShouldBeFalse("an EMPTY pool also omits the key — empty means 'all models', not 'no models'");
    }

    [Fact]
    public void Supervisor_config_bakes_the_allowed_agent_persona_pool_in_order()
    {
        var personaA = Guid.NewGuid();
        var personaB = Guid.NewGuid();

        var sup = Builder.Build(Context(allowedAgentDefinitionIds: new[] { personaA, personaB })).Nodes.Single(n => n.Id == "sup");

        sup.Config.GetProperty("allowedAgentDefinitionIds").EnumerateArray().Select(e => e.GetString()).ShouldBe(
            new[] { personaA.ToString(), personaB.ToString() }, "the operator's persona pool rows bake onto the supervisor's allowedAgentDefinitionIds in order");
    }

    [Fact]
    public void Supervisor_config_omits_the_allowed_agent_pool_when_empty_or_unset()
    {
        Builder.Build(Context(allowedAgentDefinitionIds: null)).Nodes.Single(n => n.Id == "sup")
            .Config.TryGetProperty("allowedAgentDefinitionIds", out _).ShouldBeFalse("a null pool omits the key — the supervisor draws from all the team's personas (byte-identical)");

        Builder.Build(Context(allowedAgentDefinitionIds: Array.Empty<Guid>())).Nodes.Single(n => n.Id == "sup")
            .Config.TryGetProperty("allowedAgentDefinitionIds", out _).ShouldBeFalse("an EMPTY pool also omits the key — empty means 'all personas', not 'no personas'");
    }

    [Theory]
    [InlineData(true, "spawns")]    // RequiresApproval → spawns are human-gated
    [InlineData(false, "none")]     // the deep preset's default → autonomous
    public void Approval_policy_reflects_the_caps_requires_approval(bool requiresApproval, string expectedPolicy)
    {
        var sup = Builder.Build(Context(caps: new RouteCaps { RequiresApproval = requiresApproval })).Nodes.Single(n => n.Id == "sup");

        sup.Config.GetProperty("approvalPolicy").GetString().ShouldBe(expectedPolicy);
    }

    [Fact]
    public void Supervisor_config_maps_the_agent_profile_field_for_field()
    {
        var repoId = Guid.NewGuid();
        var credId = Guid.NewGuid();
        var profile = new ResolvedAgentProfile { RepositoryId = repoId, Harness = "claude-code", Model = "claude-sonnet", ModelCredentialId = credId, RunnerKind = "local", TimeoutSeconds = 7200, EnableMcp = true, AutonomyLevel = "Trusted" };

        var agentProfile = Builder.Build(Context(profile)).Nodes.Single(n => n.Id == "sup").Config.GetProperty("agentProfile");

        agentProfile.GetProperty("repositoryId").GetString().ShouldBe(repoId.ToString());
        agentProfile.GetProperty("harness").GetString().ShouldBe("claude-code");
        agentProfile.GetProperty("model").GetString().ShouldBe("claude-sonnet");
        agentProfile.GetProperty("modelCredentialId").GetString().ShouldBe(credId.ToString());
        agentProfile.GetProperty("runnerKind").GetString().ShouldBe("local");
        agentProfile.GetProperty("timeoutSeconds").GetInt32().ShouldBe(7200, "the Launch timeout override must reach the spawned agents, not die at the projection");
        agentProfile.GetProperty("enableMcp").GetBoolean().ShouldBeTrue();
        agentProfile.GetProperty("autonomyLevel").GetString().ShouldBe("Trusted");
    }

    [Fact]
    public void Agent_profile_omits_model_and_credential_when_the_brain_pin_was_honored()
    {
        // The "Brain model" chip was AUTHORED as a pin and it resolved verbatim (SupervisorBrainModelPinned) —
        // agents draw from the model pool instead (locked design, PR #771). Model/ModelCredentialId are the SAME
        // chip's picks, so once the pin is honored they must NOT also leak in as every spawned agent's default.
        var repoId = Guid.NewGuid();
        var credId = Guid.NewGuid();
        var profile = new ResolvedAgentProfile { RepositoryId = repoId, Harness = "claude-code", Model = "claude-opus", ModelCredentialId = credId, RunnerKind = "local" };

        var agentProfile = Builder.Build(Context(profile, brainModelId: Guid.NewGuid(), brainModelPinned: true)).Nodes.Single(n => n.Id == "sup").Config.GetProperty("agentProfile");

        agentProfile.TryGetProperty("model", out _).ShouldBeFalse("the honored brain pin must not become the spawned agents' default");
        agentProfile.TryGetProperty("modelCredentialId", out _).ShouldBeFalse("the honored brain pin's credential must not become the spawned agents' default");
        // Untouched fields still bake — only the brain-only pair is withheld.
        agentProfile.GetProperty("repositoryId").GetString().ShouldBe(repoId.ToString());
        agentProfile.GetProperty("harness").GetString().ShouldBe("claude-code");
        agentProfile.GetProperty("runnerKind").GetString().ShouldBe("local");
    }

    [Fact]
    public void Agent_profile_omits_model_and_credential_when_the_authored_pin_was_ineligible()
    {
        // The operator's pin was authored but didn't resolve (SupervisorBrainModelPinIneligible) — the run falls
        // back to an auto-selected brain, but the chip was still AUTHORED as a pin, so the same withholding
        // applies: the operator's request must not become the spawned agents' default either.
        var profile = new ResolvedAgentProfile { Harness = "claude-code", Model = "claude-opus", ModelCredentialId = Guid.NewGuid() };

        var agentProfile = Builder.Build(Context(profile, brainModelId: Guid.NewGuid(), brainModelPinIneligible: true)).Nodes.Single(n => n.Id == "sup").Config.GetProperty("agentProfile");

        agentProfile.TryGetProperty("model", out _).ShouldBeFalse();
        agentProfile.TryGetProperty("modelCredentialId", out _).ShouldBeFalse();
    }

    [Fact]
    public void Agent_profile_keeps_an_explicit_model_when_the_brain_was_only_auto_selected_with_no_pin_authored()
    {
        // ResolveStructuredBrainRowAsync auto-selects SOME brain on every deep launch whenever a structured client
        // exists at all — SupervisorBrainModelId is non-null on nearly every real launch, pinned or not. Gating the
        // withholding on that alone would silently swallow an explicit agent model an operator (or API caller) set
        // directly via Overrides.Model without ever authoring a brain pin. Regression coverage for that exact bug.
        var repoId = Guid.NewGuid();
        var credId = Guid.NewGuid();
        var profile = new ResolvedAgentProfile { RepositoryId = repoId, Harness = "claude-code", Model = "claude-opus", ModelCredentialId = credId };

        var agentProfile = Builder.Build(Context(profile, brainModelId: Guid.NewGuid())).Nodes.Single(n => n.Id == "sup").Config.GetProperty("agentProfile");

        agentProfile.GetProperty("model").GetString().ShouldBe("claude-opus");
        agentProfile.GetProperty("modelCredentialId").GetString().ShouldBe(credId.ToString());
    }

    [Fact]
    public void Agent_profile_stays_byte_identical_on_an_unpinned_deep_launch_with_no_brain_resolved()
    {
        // No brain resolved at all (e.g. the pool can't supply a structured-capable model) and no explicit model
        // was set either ⇒ the byte-identical pre-existing shape: nothing to bake, nothing to withhold.
        var profile = new ResolvedAgentProfile { Harness = "claude-code" };

        var agentProfile = Builder.Build(Context(profile, brainModelId: null)).Nodes.Single(n => n.Id == "sup").Config.GetProperty("agentProfile");

        agentProfile.TryGetProperty("model", out _).ShouldBeFalse();
        agentProfile.TryGetProperty("modelCredentialId", out _).ShouldBeFalse();
        agentProfile.GetProperty("harness").GetString().ShouldBe("claude-code");
    }

    [Fact]
    public void The_launch_base_pin_bakes_onto_the_agent_profile_and_each_related_entry()
    {
        // S1: every agent this supervisor spawns must materialize the SAME immutable base — the vector bakes into
        // the frozen node config (primary pinnedSha + per related entry), so every turn + replay reads the same pins.
        var repoId = Guid.NewGuid();
        var apiId = Guid.NewGuid();
        var profile = new ResolvedAgentProfile { RepositoryId = repoId, RelatedRepositories = new[] { new WorkspaceRepositorySpec { Alias = "api", RepositoryId = apiId, Access = WorkspaceAccess.Write } } };

        var context = Context(profile) with { PinnedShas = new Dictionary<Guid, string> { [repoId] = "aaa111aaa111", [apiId] = "bbb222bbb222" } };
        context = context with { Seed = context.Seed with { BaseBranch = "release/2.x" } };

        var pinned = Builder.Build(context).Nodes.Single(n => n.Id == "sup").Config.GetProperty("agentProfile");

        pinned.GetProperty("pinnedSha").GetString().ShouldBe("aaa111aaa111");
        pinned.GetProperty("baseRef").GetString().ShouldBe("release/2.x", "the pin fixes the TREE, the operator's branch fixes the CONTEXT — both must reach every spawn");
        pinned.GetProperty("relatedRepositories")[0].GetProperty("pinnedSha").GetString().ShouldBe("bbb222bbb222", "each related entry carries ITS OWN pin — no bleed");

        var unpinned = Builder.Build(Context(profile)).Nodes.Single(n => n.Id == "sup").Config.GetProperty("agentProfile");

        unpinned.TryGetProperty("pinnedSha", out _).ShouldBeFalse("no vector ⇒ no key (byte-identical to before S1)");
        unpinned.TryGetProperty("baseRef", out _).ShouldBeFalse("no operator pin ⇒ no key");
        unpinned.GetProperty("relatedRepositories")[0].TryGetProperty("pinnedSha", out _).ShouldBeFalse();
    }

    [Fact]
    public void Supervisor_config_stamps_each_related_repo_so_a_deep_multi_repo_launch_is_not_a_silent_drop()
    {
        // The deep (supervisor) path's analogue of the agent.run node's relatedRepositories — each spawned agent
        // clones these alongside the primary. Emitted in the SAME {repositoryId, alias?, access?} shape (via the ONE
        // shared serializer) the supervisor's SupervisorAgentProfile re-parses.
        var api = Guid.NewGuid();
        var web = Guid.NewGuid();
        var profile = new ResolvedAgentProfile
        {
            RepositoryId = Guid.NewGuid(),
            RelatedRepositories = new[]
            {
                new WorkspaceRepositorySpec { RepositoryId = api, Alias = "api", Access = WorkspaceAccess.Write },
                new WorkspaceRepositorySpec { RepositoryId = web, Alias = "web", Access = WorkspaceAccess.Read },
            },
        };

        var agentProfile = Builder.Build(Context(profile)).Nodes.Single(n => n.Id == "sup").Config.GetProperty("agentProfile");

        var related = agentProfile.GetProperty("relatedRepositories").EnumerateArray().ToList();
        related.Select(e => e.GetProperty("repositoryId").GetString()).ShouldBe(new[] { api.ToString(), web.ToString() }, "both related repos reach the supervisor config in authored order");
        related[0].GetProperty("access").GetString().ShouldBe("write");
        related[1].GetProperty("access").GetString().ShouldBe("read");
        related[0].GetProperty("alias").GetString().ShouldBe("api");
    }

    [Fact]
    public void A_profile_with_no_related_repos_omits_relatedRepositories_byte_identical()
    {
        var profile = new ResolvedAgentProfile { RepositoryId = Guid.NewGuid(), Harness = "codex-cli" };

        var agentProfile = Builder.Build(Context(profile)).Nodes.Single(n => n.Id == "sup").Config.GetProperty("agentProfile");

        agentProfile.TryGetProperty("relatedRepositories", out _).ShouldBeFalse(
            "no related repos ⇒ no key — a single-repo supervisor spawn is byte-identical to the pre-multi-repo-launch shape");
    }

    [Fact]
    public void A_bare_context_omits_the_agent_profile_and_the_caps_keys()
    {
        var sup = Builder.Build(Context()).Nodes.Single(n => n.Id == "sup");

        sup.Config.TryGetProperty("agentProfile", out _).ShouldBeFalse("an absent profile omits the nested object — a bare codex-cli/no-repo supervisor");
        sup.Config.TryGetProperty("maxParallelism", out _).ShouldBeFalse("an unset cap omits the key — the node falls to its SupervisorLane default");
        sup.Config.TryGetProperty("maxTotalSpawns", out _).ShouldBeFalse();
        sup.Config.TryGetProperty("maxCostUsd", out _).ShouldBeFalse("an unset cost cap omits the key — the run has no cost budget (SOTA #4 default)");
        sup.Config.TryGetProperty("supervisorModelId", out _).ShouldBeFalse("no resolved brain ⇒ no key — a hand-authored node keeps its own supervisorModelId, and an empty pool fails closed at decide time (the honest floor)");

        // approvalPolicy is always present — it defaults to 'none' (the autonomous, pre-approval posture).
        sup.Config.GetProperty("approvalPolicy").GetString().ShouldBe("none");
    }

    [Fact]
    public void Supervisor_config_bakes_the_self_resolved_brain_model_when_present()
    {
        // The Auto/Deep lane resolves a brain model at launch (the operator pins none) and threads it on the context;
        // the builder bakes it into supervisorModelId so the decider has a brain instead of stopping turn-1.
        var brain = Guid.NewGuid();

        var sup = Builder.Build(Context(brainModelId: brain)).Nodes.Single(n => n.Id == "sup");

        sup.Config.GetProperty("supervisorModelId").GetString().ShouldBe(brain.ToString(),
            "the launch-resolved brain row id is baked into the node config — the decider runs on it (no NoBrainModelStop turn-1)");
    }

    [Fact]
    public void A_bare_context_omits_the_brain_pin_ineligible_flag()
    {
        // The common case (no pin, or an honored pin): no fallback happened, so the flag must not be baked at all —
        // byte-identical to every pre-existing supervisor definition.
        Builder.Build(Context(brainModelId: Guid.NewGuid())).Nodes.Single(n => n.Id == "sup").Config
            .TryGetProperty("brainModelPinIneligible", out _).ShouldBeFalse("no fallback ⇒ no key ⇒ byte-identical");
    }

    [Fact]
    public void Supervisor_config_bakes_the_brain_pin_ineligible_flag_when_the_launch_fell_back()
    {
        // TaskLaunchService sets this when the operator's pin was ineligible and it auto-selected a DIFFERENT brain —
        // the flag must survive into the frozen definition so the swap is discoverable, not a silent id substitution.
        var fallbackBrain = Guid.NewGuid();

        var sup = Builder.Build(Context(brainModelId: fallbackBrain, brainModelPinIneligible: true)).Nodes.Single(n => n.Id == "sup");

        sup.Config.GetProperty("brainModelPinIneligible").GetBoolean().ShouldBeTrue(
            "the pin didn't resolve — the baked supervisorModelId is a fallback, and that fact must ride along with it");
    }

    [Fact]
    public void Terminal_surfaces_the_supervisor_outputs()
    {
        var end = Builder.Build(Context()).Nodes.Single(n => n.Id == "end");

        end.Inputs.GetProperty("status").GetString().ShouldBe("{{nodes.sup.outputs.status}}");
        end.Inputs.GetProperty("decision").GetString().ShouldBe("{{nodes.sup.outputs.decision}}");
        end.Inputs.GetProperty("reason").GetString().ShouldBe("{{nodes.sup.outputs.reason}}");
        end.Inputs.GetProperty("turns").GetString().ShouldBe("{{nodes.sup.outputs.turns}}");
        end.Inputs.GetProperty("integratedBranch").GetString().ShouldBe("{{nodes.sup.outputs.integratedBranch}}", "PR-5: the terminal must also carry the run's final reviewable branch, not just status/decision/reason/turns");
        end.Inputs.GetProperty("repositoryId").GetString().ShouldBe("{{nodes.sup.outputs.repositoryId}}", "PR-6: a single-repo run's Open-PR action needs the branch's owning repository, which integratedBranch alone doesn't carry");
        end.Inputs.GetProperty("repositoryBranches").GetString().ShouldBe("{{nodes.sup.outputs.repositoryBranches}}", "PR-5: and the per-repo final heads for a multi-repo run");
    }

    [Fact]
    public void An_honored_brain_pin_is_baked_so_the_decider_never_fails_over_and_an_auto_brain_omits_the_key()
    {
        var pinnedBrain = Guid.NewGuid();

        var pinned = Builder.Build(Context(brainModelId: pinnedBrain, brainModelPinned: true)).Nodes.Single(n => n.Id == "sup");
        pinned.Config.GetProperty("brainModelPinned").GetBoolean().ShouldBeTrue("an honored operator pin must reach every turn + replay — the decider resolves it verbatim, never hopping providers");

        var auto = Builder.Build(Context(brainModelId: Guid.NewGuid())).Nodes.Single(n => n.Id == "sup");
        auto.Config.TryGetProperty("brainModelPinned", out _).ShouldBeFalse("auto-selected ⇒ no key ⇒ byte-identical (and eligible for pool failover)");
    }
}
