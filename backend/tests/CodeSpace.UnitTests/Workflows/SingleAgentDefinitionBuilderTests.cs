using System.Text.Json;
using CodeSpace.Core.Services.Tasks.Projection.Builders.SingleAgent;
using CodeSpace.Messages.Agents;
using CodeSpace.Core.Services.Workflows.Engine;
using CodeSpace.Core.Services.Workflows.Nodes;
using CodeSpace.Core.Services.Workflows.Nodes.Builtin;
using CodeSpace.Messages.Dtos.Workflows;
using CodeSpace.Messages.Tasks;
using CodeSpace.Messages.Tasks.Effort;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

/// <summary>
/// Pins the single-agent projection builder: the emitted graph is <c>trigger.manual → agent.run →
/// builtin.terminal</c>, it ALWAYS passes the REAL <see cref="DefinitionValidator"/> (against the real node
/// manifests, including the agent.run output-key existence check the terminal's refs hit), and the
/// <see cref="ResolvedAgentProfile"/> + seed goal map onto the SAME agent.run config keys
/// <see cref="AgentCodeNode"/> reads — so a snapshot single-agent run executes identically to an authored
/// agent.run node. Validation is asserted across a bare profile, a fully-populated profile, and the seed-repo
/// fallback so a relaxed mapping can't slip through.
/// </summary>
[Trait("Category", "Unit")]
[Collection("DefaultHarnessEnvMutation")]   // a null-profile build reads the unset default harness — serialize with the env-mutating AgentHarnessDefaultsTests
public class SingleAgentDefinitionBuilderTests
{
    private static readonly SingleAgentDefinitionBuilder Builder = new();

    /// <summary>The REAL validator over the REAL node runtimes the builder emits — so the output-key check runs against agent.run's actual OutputSchema, not a stub.</summary>
    private static DefinitionValidator RealValidator() => new(new NodeRegistry(new INodeRuntime[]
    {
        new TriggerManualNode(),
        new AgentCodeNode(),
        new TerminalNode(),
    }));

    private static TaskBuildContext Context(TaskLaunchSeed seed, ResolvedAgentProfile? profile) => new()
    {
        Seed = seed,
        Route = new RoutePlan { ProjectionKind = TaskProjectionKinds.SingleAgent },
        AgentProfile = profile,
    };

    private static TaskLaunchSeed Seed(Guid? repositoryId = null) => new()
    {
        Goal = "Fix the failing login test",
        SurfaceKind = "chat",
        TeamId = Guid.NewGuid(),
        RepositoryId = repositoryId,
    };

    [Fact]
    public void Reports_the_single_agent_projection_kind()
    {
        Builder.ProjectionKind.ShouldBe(TaskProjectionKinds.SingleAgent);
    }

    [Fact]
    public void Emits_the_fixed_manual_to_agent_to_terminal_graph()
    {
        var def = Builder.Build(Context(Seed(), profile: null));

        def.Nodes.Select(n => n.TypeKey).ShouldBe(new[] { "trigger.manual", "agent.run", "builtin.terminal" });
        def.Edges.Select(e => (e.From, e.To)).ShouldBe(new[] { ("start", "agent"), ("agent", "done") });
    }

    [Fact]
    public void Output_passes_the_real_validator_for_a_bare_profile()
    {
        var result = RealValidator().Validate(Builder.Build(Context(Seed(), profile: null)));

        result.IsValid.ShouldBeTrue(customMessage: "a bare-profile single-agent definition must pass DefinitionValidator: " + string.Join(" | ", result.Errors));
    }

    [Fact]
    public void Bare_profile_emits_only_goal_and_the_default_harness()
    {
        var config = AgentConfigOf(Builder.Build(Context(Seed(), profile: null)));

        config.GetProperty("goal").GetString().ShouldBe("Fix the failing login test");
        config.GetProperty("harness").GetString().ShouldBe("codex-cli", customMessage: "a null harness folds to the agent.run catalog default");

        // No optional knobs leak — an absent key inherits the node's own default, matching a bare authored node.
        config.TryGetProperty("model", out _).ShouldBeFalse();
        config.TryGetProperty("autonomyLevel", out _).ShouldBeFalse();
        config.TryGetProperty("agentDefinitionId", out _).ShouldBeFalse();
        AgentInputsOf(Builder.Build(Context(Seed(), profile: null))).TryGetProperty("repositoryId", out _).ShouldBeFalse();
    }

    [Fact]
    public void Full_profile_maps_every_field_onto_the_agent_code_config()
    {
        var agentDefId = Guid.NewGuid();
        var credId = Guid.NewGuid();
        var repoId = Guid.NewGuid();

        var profile = new ResolvedAgentProfile
        {
            RepositoryId = repoId,
            Harness = "claude-code",
            Model = "claude-sonnet",
            AgentDefinitionId = agentDefId,
            ModelCredentialId = credId,
            RunnerKind = "local",
            AutonomyLevel = "Trusted",
            AllowedTools = new[] { "Read", "Grep" },
        };

        var def = Builder.Build(Context(Seed(), profile));
        var config = AgentConfigOf(def);

        config.GetProperty("harness").GetString().ShouldBe("claude-code");
        config.GetProperty("model").GetString().ShouldBe("claude-sonnet");
        config.GetProperty("agentDefinitionId").GetString().ShouldBe(agentDefId.ToString());
        config.GetProperty("modelCredentialId").GetString().ShouldBe(credId.ToString());
        config.GetProperty("runnerKind").GetString().ShouldBe("local");
        config.GetProperty("autonomyLevel").GetString().ShouldBe("Trusted");
        config.GetProperty("tools").EnumerateArray().Select(e => e.GetString()).ShouldBe(new[] { "Read", "Grep" });

        // repositoryId binds as the node's INPUT (matching agent.run's InputSchema), not config.
        AgentInputsOf(def).GetProperty("repositoryId").GetString().ShouldBe(repoId.ToString());

        RealValidator().Validate(def).IsValid.ShouldBeTrue();
    }

    [Fact]
    public void Repository_falls_back_to_the_seed_when_the_profile_has_none()
    {
        var seedRepo = Guid.NewGuid();

        // Profile names no repo; the seed does → the node's bound repositoryId comes from the seed.
        var def = Builder.Build(Context(Seed(seedRepo), new ResolvedAgentProfile { Harness = "codex-cli" }));

        AgentInputsOf(def).GetProperty("repositoryId").GetString().ShouldBe(seedRepo.ToString());
    }

    [Fact]
    public void Profile_repository_wins_over_the_seed_repository()
    {
        var profileRepo = Guid.NewGuid();
        var seedRepo = Guid.NewGuid();

        var def = Builder.Build(Context(Seed(seedRepo), new ResolvedAgentProfile { RepositoryId = profileRepo }));

        AgentInputsOf(def).GetProperty("repositoryId").GetString().ShouldBe(profileRepo.ToString());
    }

    [Fact]
    public void Profile_related_repositories_project_onto_the_agent_code_relatedRepositories_input()
    {
        var web = Guid.NewGuid();
        var api = Guid.NewGuid();

        var def = Builder.Build(Context(Seed(), new ResolvedAgentProfile
        {
            RepositoryId = web,
            RelatedRepositories = new[] { new WorkspaceRepositorySpec { Alias = "api", RepositoryId = api, Access = WorkspaceAccess.Write } },
        }));

        var inputs = AgentInputsOf(def);
        inputs.GetProperty("repositoryId").GetString().ShouldBe(web.ToString());

        var related = inputs.GetProperty("relatedRepositories");
        related.GetArrayLength().ShouldBe(1, "the projection lane carries the related repos onto the SAME input the editor + AgentCodeNode use");
        related[0].GetProperty("repositoryId").GetString().ShouldBe(api.ToString());
        related[0].GetProperty("alias").GetString().ShouldBe("api");
        related[0].GetProperty("access").GetString().ShouldBe("write");

        RealValidator().Validate(def).IsValid.ShouldBeTrue();
    }

    [Fact]
    public void No_related_repositories_omits_the_input_byte_identical()
    {
        // A profile with no related repos must NOT add a relatedRepositories key — a single-repo projection is unchanged.
        var def = Builder.Build(Context(Seed(), new ResolvedAgentProfile { RepositoryId = Guid.NewGuid() }));

        AgentInputsOf(def).TryGetProperty("relatedRepositories", out _).ShouldBeFalse();
    }

    [Fact]
    public void Related_repository_with_read_access_and_blank_alias_emits_read_and_omits_the_alias()
    {
        // The projection lane must agree with the editor + node on the defaults: a blank alias is emitted as null
        // (the node re-derives repo-N) and Read access is emitted as "read" — else the projected workspace diverges.
        var api = Guid.NewGuid();
        var def = Builder.Build(Context(Seed(), new ResolvedAgentProfile
        {
            RepositoryId = Guid.NewGuid(),
            RelatedRepositories = new[] { new WorkspaceRepositorySpec { Alias = "  ", RepositoryId = api, Access = WorkspaceAccess.Read } },
        }));

        var entry = AgentInputsOf(def).GetProperty("relatedRepositories")[0];
        entry.GetProperty("repositoryId").GetString().ShouldBe(api.ToString());
        entry.GetProperty("access").GetString().ShouldBe("read", "Read access is emitted as 'read'");
        (entry.GetProperty("alias").ValueKind == JsonValueKind.Null).ShouldBeTrue("a blank alias is emitted null so the node re-derives repo-N");
    }

    [Fact]
    public void Terminal_surfaces_the_agent_result_outputs()
    {
        var inputs = TerminalInputsOf(Builder.Build(Context(Seed(), profile: null)));

        inputs.GetProperty("status").GetString().ShouldBe("{{nodes.agent.outputs.status}}");
        inputs.GetProperty("summary").GetString().ShouldBe("{{nodes.agent.outputs.summary}}");
    }

    [Fact]
    public void Terminal_omits_repositoryResults_for_a_single_repo_run_byte_identical()
    {
        // A single-repo run never emits repositoryResults from the agent node, so the terminal must NOT bind it —
        // else EVERY single-repo run's OutputsJson would gain a repositoryResults: null key (not byte-identical).
        var inputs = TerminalInputsOf(Builder.Build(Context(Seed(), new ResolvedAgentProfile { RepositoryId = Guid.NewGuid() })));

        inputs.TryGetProperty("repositoryResults", out _).ShouldBeFalse(
            "a single-repo terminal surfaces only the scalar keys — byte-identical to the pre-S4b-2 output");
        inputs.GetProperty("branch").GetString().ShouldBe("{{nodes.agent.outputs.branch}}", "the flat branch is still surfaced");
    }

    [Fact]
    public void Terminal_surfaces_repositoryResults_for_a_multi_repo_run()
    {
        // A multi-repo run (authored related repos) surfaces the per-repo change set so a session follow-up can
        // continue each repo from its own prior branch (the resolver reads OutputsJson.repositoryResults).
        var def = Builder.Build(Context(Seed(), new ResolvedAgentProfile
        {
            RepositoryId = Guid.NewGuid(),
            RelatedRepositories = new[] { new WorkspaceRepositorySpec { Alias = "api", RepositoryId = Guid.NewGuid(), Access = WorkspaceAccess.Write } },
        }));

        TerminalInputsOf(def).GetProperty("repositoryResults").GetString().ShouldBe("{{nodes.agent.outputs.repositoryResults}}",
            "a multi-repo terminal surfaces the per-repo branches for downstream session continuity");
        RealValidator().Validate(def).IsValid.ShouldBeTrue(customMessage: "the repositoryResults ref must resolve against agent.run's real OutputSchema");
    }

    [Fact]
    public void Per_repo_base_refs_thread_onto_the_agent_inputs()
    {
        // Session branch continuity: the context's BaseRefs map sets the primary's baseRef + each related repo's ref.
        var primary = Guid.NewGuid();
        var related = Guid.NewGuid();

        var context = Context(Seed(), new ResolvedAgentProfile
        {
            RepositoryId = primary,
            RelatedRepositories = new[] { new WorkspaceRepositorySpec { Alias = "api", RepositoryId = related, Access = WorkspaceAccess.Write } },
        }) with { BaseRefs = new Dictionary<Guid, SessionStartRef> { [primary] = new() { Branch = "run-1/primary" }, [related] = new() { Branch = "run-1/api" } } };

        var inputs = AgentInputsOf(Builder.Build(context));

        inputs.GetProperty("baseRef").GetString().ShouldBe("run-1/primary", "the primary clones at its prior branch");
        inputs.GetProperty("relatedRepositories")[0].GetProperty("ref").GetString().ShouldBe("run-1/api", "the related repo clones at ITS prior branch");
    }

    [Fact]
    public void No_base_refs_omits_baseRef_and_per_repo_ref_byte_identical()
    {
        var primary = Guid.NewGuid();
        var def = Builder.Build(Context(Seed(), new ResolvedAgentProfile
        {
            RepositoryId = primary,
            RelatedRepositories = new[] { new WorkspaceRepositorySpec { Alias = "api", RepositoryId = Guid.NewGuid(), Access = WorkspaceAccess.Write } },
        }));

        var inputs = AgentInputsOf(def);
        inputs.TryGetProperty("baseRef", out _).ShouldBeFalse("no base-refs ⇒ no baseRef key (default branch, byte-identical)");
        inputs.GetProperty("relatedRepositories")[0].TryGetProperty("ref", out _).ShouldBeFalse("no base-refs ⇒ no per-repo ref key");
    }

    [Fact]
    public void The_agent_node_carries_the_default_transient_retry()
    {
        // One crashed / rate-limited agent must not kill the whole task — the launch lane authors the respawn
        // budget the engine's durable attempt ledger enforces. Pinned so a projection refactor can't silently
        // strip the resilience back to one attempt.
        var retry = Builder.Build(Context(Seed(), profile: null)).Nodes.Single(n => n.Id == "agent").Retry;

        retry.ShouldNotBeNull();
        retry.MaxAttempts.ShouldBe(3);
        retry.BackoffSeconds.ShouldBe(30);
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────────

    private static JsonElement AgentConfigOf(WorkflowDefinition def) => def.Nodes.Single(n => n.Id == "agent").Config;
    private static JsonElement AgentInputsOf(WorkflowDefinition def) => def.Nodes.Single(n => n.Id == "agent").Inputs;
    private static JsonElement TerminalInputsOf(WorkflowDefinition def) => def.Nodes.Single(n => n.Id == "done").Inputs;

    // ── S5: the quick tier's operator checks floor becomes the single agent's contract ──

    [Fact]
    public void The_operator_checks_floor_bakes_as_the_agents_acceptance_with_blanks_dropped()
    {
        var def = Builder.Build(Context(Seed(), profile: null) with { AcceptanceChecks = new[] { "sh", " ", "check.sh" } });

        var acceptance = def.Nodes.Single(n => n.TypeKey == "agent.run").Config.GetProperty("acceptance");
        acceptance.GetProperty("command").EnumerateArray().Select(e => e.GetString()).ShouldBe(new[] { "sh", "check.sh" });
        acceptance.GetProperty("kind").GetString().ShouldBe("TestsPass");
    }

    [Fact]
    public void No_checks_floor_omits_the_acceptance_key_byte_identically()
    {
        Builder.Build(Context(Seed(), profile: null)).Nodes.Single(n => n.TypeKey == "agent.run").Config.TryGetProperty("acceptance", out _)
            .ShouldBeFalse("no floor ⇒ no oracle ⇒ byte-identical");
    }

    // ── B2: the route's deliverable SHAPE decides the agent's mode + (absent an operator floor) its oracle ──

    private static TaskBuildContext Shaped(string shape, IReadOnlyList<string>? checks = null, IReadOnlyList<string>? criteria = null) => new()
    {
        Seed = Seed(),
        Route = new RoutePlan { ProjectionKind = TaskProjectionKinds.SingleAgent, DeliverableShape = shape },
        AgentProfile = null,
        AcceptanceChecks = checks,
        AcceptanceCriteria = criteria,
    };

    [Theory]
    // shape, operator argv authored?, expected mode key (null ⇒ omitted), expected acceptance kind (null ⇒ omitted)
    [InlineData(DeliverableShapes.Code, false, null, null)]                 // the status quo — no mode, no oracle
    [InlineData(DeliverableShapes.Code, true, null, "TestsPass")]           // the operator's floor, unchanged
    [InlineData(DeliverableShapes.Answer, false, "research", "LlmJudge")]   // a question: no network, judged not tested
    [InlineData(DeliverableShapes.Answer, true, "research", "TestsPass")]   // operator authority survives the shape
    [InlineData(DeliverableShapes.Research, false, "research", "LlmJudge")]
    [InlineData(DeliverableShapes.Research, true, "research", "TestsPass")]
    [InlineData(DeliverableShapes.Document, false, "code", "LlmJudge")]     // a written file IS a workspace write
    [InlineData(DeliverableShapes.Document, true, "code", "TestsPass")]
    [InlineData("a-shape-nobody-has-heard-of", false, null, null)]          // unknown ⇒ folds to code ⇒ status quo
    public void The_shape_and_the_operator_floor_decide_the_mode_and_the_acceptance_kind(string shape, bool operatorArgv, string? expectedMode, string? expectedKind)
    {
        var config = AgentConfigOf(Builder.Build(Shaped(shape, checks: operatorArgv ? new[] { "sh", "check.sh" } : null)));

        if (expectedMode is null) config.TryGetProperty("mode", out _).ShouldBeFalse("a code-shaped run emits no mode — byte-identical to before this axis existed");
        else config.GetProperty("mode").GetString().ShouldBe(expectedMode);

        if (expectedKind is null) config.TryGetProperty("acceptance", out _).ShouldBeFalse("no floor and a code shape ⇒ no oracle");
        else config.GetProperty("acceptance").GetProperty("kind").GetString().ShouldBe(expectedKind);
    }

    [Fact]
    public void A_code_shaped_launch_with_operator_argv_is_byte_identical_to_the_pre_shape_config()
    {
        // The refutation guard the other direction: the ordinary coding launch must not shift one byte.
        var withShape = AgentConfigOf(Builder.Build(Shaped(DeliverableShapes.Code, checks: new[] { "sh", " ", "check.sh" }, criteria: new[] { "no regressions" })));

        var context = Context(Seed(), profile: null) with { AcceptanceChecks = new[] { "sh", " ", "check.sh" }, AcceptanceCriteria = new[] { "no regressions" } };
        var withDefaultRoute = AgentConfigOf(Builder.Build(context));

        withShape.GetRawText().ShouldBe(withDefaultRoute.GetRawText(), "an explicit code shape and the default route must emit the SAME agent config");

        withShape.GetProperty("acceptance").GetProperty("command").EnumerateArray().Select(e => e.GetString()).ShouldBe(new[] { "sh", "check.sh" });
        withShape.GetProperty("acceptanceAuthority").GetString().ShouldBe("Operator");
        withShape.GetProperty("goal").GetString().ShouldNotContain("DELIVERABLE.md", customMessage: "a coding run is never told to write a deliverable file");
    }

    [Fact]
    public void A_shape_derived_contract_names_the_deliverable_file_in_the_goal_and_grades_that_file()
    {
        // The oracle reads DECLARED FILES, so the agent must be told which file — otherwise the contract grades
        // something nobody asked for and every answer-shaped run flunks a check it was never able to satisfy.
        var config = AgentConfigOf(Builder.Build(Shaped(DeliverableShapes.Answer)));

        config.GetProperty("acceptance").GetProperty("command").EnumerateArray().Select(e => e.GetString())
            .ShouldBe(new[] { SingleAgentDefinitionBuilder.DeliverableFileName }, "the LlmJudge command IS the deliverable path list");

        config.GetProperty("goal").GetString().ShouldContain(SingleAgentDefinitionBuilder.DeliverableFileName,
            customMessage: "the agent is told where to write what it is graded on");

        config.GetProperty("displayTitle").GetString().ShouldBe("Fix the failing login test", "the deliverable line never leaks into the card title");
    }

    [Fact]
    public void A_shape_derived_contract_is_NOT_staked_as_operator_authority()
    {
        // The server composed this contract from the shape; crediting the operator with it would inflate the claim.
        AgentConfigOf(Builder.Build(Shaped(DeliverableShapes.Research))).TryGetProperty("acceptanceAuthority", out _)
            .ShouldBeFalse("only the operator's own argv is staked as Operator authority");
    }

    [Fact]
    public void The_judge_rubric_is_the_operator_criteria_when_authored_else_the_goal()
    {
        var fromCriteria = AgentConfigOf(Builder.Build(Shaped(DeliverableShapes.Document, criteria: new[] { "cites sources", " ", "names the trade-offs" })))
            .GetProperty("acceptance").GetProperty("rubric").GetProperty("criteria");

        fromCriteria.EnumerateArray().Select(c => c.GetProperty("requirement").GetString())
            .ShouldBe(new[] { "cites sources", "names the trade-offs" }, "blank criteria are dropped; each becomes one binary requirement");
        fromCriteria.EnumerateArray().Select(c => c.GetProperty("id").GetString()).ShouldBe(new[] { "c1", "c2" });

        var fromGoal = AgentConfigOf(Builder.Build(Shaped(DeliverableShapes.Document)))
            .GetProperty("acceptance").GetProperty("rubric").GetProperty("criteria");

        fromGoal.GetArrayLength().ShouldBe(1, "with no criteria authored, the goal itself is the single binary criterion");
        fromGoal[0].GetProperty("requirement").GetString().ShouldContain("Fix the failing login test");
    }

    [Theory]
    [InlineData(DeliverableShapes.Answer)]
    [InlineData(DeliverableShapes.Document)]
    [InlineData(DeliverableShapes.Research)]
    public void Every_shape_derived_definition_still_passes_the_real_validator(string shape)
    {
        var def = Builder.Build(Shaped(shape));

        var result = RealValidator().Validate(def);

        result.IsValid.ShouldBeTrue(customMessage: $"the '{shape}' projection must satisfy agent.run's real schema: " + string.Join(" | ", result.Errors));
    }
}
