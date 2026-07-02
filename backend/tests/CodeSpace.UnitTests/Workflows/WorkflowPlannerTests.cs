using System.Text.Json;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Agents.ModelCredentials;
using CodeSpace.Core.Services.Workflows.Engine;
using CodeSpace.Messages.Agents;
using CodeSpace.Core.Services.Workflows.Llm;
using CodeSpace.Core.Services.Workflows.Nodes;
using CodeSpace.Core.Services.Workflows.Nodes.Builtin;
using CodeSpace.Core.Services.Workflows.Planning;
using CodeSpace.Core.Services.Workflows.Planning.Planners;
using CodeSpace.Messages.Commands.Workflows;
using CodeSpace.Messages.Dtos.Providers;
using CodeSpace.Messages.Dtos.Workflows;
using CodeSpace.Messages.Dtos.Workflows.Planning;
using CodeSpace.Messages.Enums;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

/// <summary>
/// Unit coverage for the task-first planner (PR-D Slice 1): the response-schema commit-contract, the
/// flag env-const (Rule 8 pin), the flag-OFF disabled path (no planner call), and the projector — its
/// emitted graph passes the REAL DefinitionValidator, its flow.map items binding resolves to the subtasks,
/// and the recommended-kind switch picks the right body node type.
/// </summary>
[Trait("Category", "Unit")]
[Collection("DefaultHarnessEnvMutation")]   // reads the unset default harness — serialize with the env-mutating AgentHarnessDefaultsTests
public class WorkflowPlannerTests
{
    // ── Flag env-const pin (Rule 8) ───────────────────────────────────────────

    [Fact]
    public void EnabledEnvVar_constant_name_is_pinned()
    {
        // Renaming this breaks every operator who flipped planning on via env. Hard-pin the literal.
        WorkflowPlanningService.EnabledEnvVar.ShouldBe("CODESPACE_WORKFLOW_PLANNER_ENABLED");
    }

    // ── Response-schema shape pin (the commit-contract) ───────────────────────

    [Fact]
    public void ResponseSchema_shape_is_pinned()
    {
        var root = PlannerSchema.ResponseSchema;

        root.GetProperty("type").GetString().ShouldBe("object");
        root.GetProperty("additionalProperties").GetBoolean().ShouldBeFalse();

        var required = root.GetProperty("required").EnumerateArray().Select(e => e.GetString()).ToList();
        required.ShouldBe(new[] { "goal", "subtasks" });

        var subtasks = root.GetProperty("properties").GetProperty("subtasks");
        subtasks.GetProperty("type").GetString().ShouldBe("array");
        subtasks.GetProperty("minItems").GetInt32().ShouldBe(1);
        subtasks.GetProperty("maxItems").GetInt32().ShouldBe(20);

        var item = subtasks.GetProperty("items");
        item.GetProperty("additionalProperties").GetBoolean().ShouldBeFalse();
        var itemRequired = item.GetProperty("required").EnumerateArray().Select(e => e.GetString()).ToList();
        // The contract fields (dependsOn/acceptance) are OPTIONAL — required is unchanged so every prior plan stays schema-valid.
        itemRequired.ShouldBe(new[] { "id", "title", "instruction" });

        // Triad S1: the plan authors its own verification contract — the DAG edges + per-subtask objective
        // acceptance (same shape as the supervisor's plan schema) + the plan-level self-bypass.
        var itemProps = item.GetProperty("properties");
        itemProps.TryGetProperty("dependsOn", out _).ShouldBeTrue();
        itemProps.TryGetProperty("kind", out _).ShouldBeTrue();
        itemProps.TryGetProperty("acceptanceCriteria", out _).ShouldBeTrue();
        var acceptance = itemProps.GetProperty("acceptance");
        acceptance.GetProperty("required").EnumerateArray().Select(e => e.GetString()).ShouldBe(new[] { "command" });
        acceptance.GetProperty("properties").GetProperty("kind").GetProperty("enum").EnumerateArray().Select(e => e.GetString())
            .ShouldBe(new[] { "TestsPass", "ArtifactPresent" }, "the acceptance oracle vocabulary matches the supervisor plan schema verbatim");
        root.GetProperty("properties").TryGetProperty("hasEnoughContext", out _).ShouldBeTrue();
        root.GetProperty("properties").TryGetProperty("assumptions", out _).ShouldBeTrue();

        // The operator-question form fodder (S2a): bounded 2-4 mutually exclusive options per question, ≤3 questions.
        var questions = root.GetProperty("properties").GetProperty("questions");
        questions.GetProperty("maxItems").GetInt32().ShouldBe(3);
        var question = questions.GetProperty("items");
        question.GetProperty("required").EnumerateArray().Select(e => e.GetString()).ShouldBe(new[] { "id", "question", "options" });
        question.GetProperty("properties").GetProperty("options").GetProperty("minItems").GetInt32().ShouldBe(2);
        question.GetProperty("properties").GetProperty("options").GetProperty("maxItems").GetInt32().ShouldBe(4);
    }

    [Fact]
    public void The_authored_contract_round_trips_from_a_schema_valid_object()
    {
        var schemaValid = JsonSerializer.SerializeToElement(new
        {
            goal = "Ship the feature",
            subtasks = new object[]
            {
                new { id = "s1", title = "Design", instruction = "Sketch the API" },
                new { id = "s2", title = "Build", instruction = "Implement it", dependsOn = new[] { "s1" }, acceptance = new { command = new[] { "dotnet", "test" }, kind = "TestsPass", description = "unit gate" } },
            },
            hasEnoughContext = true,
        });

        var plan = schemaValid.Deserialize<PlannedWorkflow>(PlannerSchema.Options)!;

        plan.HasEnoughContext.ShouldBeTrue();
        plan.Subtasks[0].DependsOn.ShouldBeNull("an uncontracted subtask stays contract-free — absent, never defaulted");
        plan.Subtasks[0].Acceptance.ShouldBeNull();
        plan.Subtasks[1].DependsOn.ShouldBe(new[] { "s1" });
        plan.Subtasks[1].Acceptance!.Command.ShouldBe(new[] { "dotnet", "test" });
        plan.Subtasks[1].Acceptance!.Kind.ShouldBe(CodeSpace.Messages.Agents.Benchmark.BenchmarkGradingKind.TestsPass, "the string kind binds through the planner options' enum converter");
    }

    [Fact]
    public void Questions_and_assumptions_round_trip_from_a_schema_valid_object()
    {
        var schemaValid = JsonSerializer.SerializeToElement(new
        {
            goal = "Ship it",
            subtasks = new object[] { new { id = "s1", title = "T", instruction = "I", kind = "research", acceptanceCriteria = new[] { "cites sources" } } },
            assumptions = new[] { "assumed the default branch" },
            questions = new object[]
            {
                new { id = "q1", question = "Which direction?", options = new object[] { new { id = "a", label = "Fast" }, new { id = "b", label = "Thorough" } }, recommendedOptionId = "a", allowFreeText = true },
            },
        });

        var plan = schemaValid.Deserialize<PlannedWorkflow>(PlannerSchema.Options)!;

        plan.Subtasks[0].Kind.ShouldBe("research");
        plan.Subtasks[0].AcceptanceCriteria.ShouldBe(new[] { "cites sources" });
        plan.Assumptions.ShouldBe(new[] { "assumed the default branch" });
        plan.Questions!.Count.ShouldBe(1);
        plan.Questions[0].Id.ShouldBe("q1");
        plan.Questions[0].Options.Select(o => o.Label).ShouldBe(new[] { "Fast", "Thorough" });
        plan.Questions[0].RecommendedOptionId.ShouldBe("a");
        plan.Questions[0].AllowFreeText.ShouldBeTrue();
    }

    [Fact]
    public void PlannedWorkflow_round_trips_from_a_schema_valid_object()
    {
        var schemaValid = JsonSerializer.SerializeToElement(new
        {
            goal = "Ship the feature",
            subtasks = new object[]
            {
                new { id = "s1", title = "Design", instruction = "Sketch the API", rationale = "Align first" },
                new { id = "s2", title = "Build", instruction = "Implement it" },
            },
            successCriteria = new[] { "tests pass" },
            risks = new[] { "scope creep" },
            recommendedWorkflowKind = "coding",
        });

        var plan = schemaValid.Deserialize<PlannedWorkflow>(PlannerSchema.Options);

        plan.ShouldNotBeNull();
        plan!.Goal.ShouldBe("Ship the feature");
        plan.Subtasks.Count.ShouldBe(2);
        plan.Subtasks[0].Title.ShouldBe("Design");
        plan.Subtasks[0].Instruction.ShouldBe("Sketch the API");
        plan.Subtasks[1].Rationale.ShouldBeNull();
        plan.RecommendedWorkflowKind.ShouldBe("coding");
    }

    // ── Flag-OFF: disabled result, planner never invoked ──────────────────────

    [Fact]
    public async Task Flag_off_returns_disabled_result_without_invoking_the_planner()
    {
        // The flag lives in the process-global env; restore it in finally so a leftover value can't bleed into
        // another test (symmetry with Flag_on). null IS the default — only this service reads this var.
        var original = Environment.GetEnvironmentVariable(WorkflowPlanningService.EnabledEnvVar);
        Environment.SetEnvironmentVariable(WorkflowPlanningService.EnabledEnvVar, null);
        try
        {
            var planner = new RecordingPlanner();
            var service = new WorkflowPlanningService(planner, Projector(), BuildValidator(), new RecordingGrounding());

            var result = await service.PlanFromTaskAsync(SampleRequest(), CancellationToken.None);

            result.PlannerEnabled.ShouldBeFalse();
            result.Plan.ShouldBeNull();
            result.Definition.ShouldBeNull();
            planner.Invocations.ShouldBe(0, "the planner must not be called when the flag is off");
        }
        finally
        {
            Environment.SetEnvironmentVariable(WorkflowPlanningService.EnabledEnvVar, original);
        }
    }

    [Fact]
    public async Task Flag_on_plans_projects_and_returns_a_validated_definition()
    {
        Environment.SetEnvironmentVariable(WorkflowPlanningService.EnabledEnvVar, "1");
        try
        {
            var planner = new RecordingPlanner(SamplePlan("analysis"));
            var service = new WorkflowPlanningService(planner, Projector(), BuildValidator(), new RecordingGrounding());

            var result = await service.PlanFromTaskAsync(SampleRequest(), CancellationToken.None);

            result.PlannerEnabled.ShouldBeTrue();
            result.Plan.ShouldNotBeNull();
            result.Definition.ShouldNotBeNull();
            planner.Invocations.ShouldBe(1);
        }
        finally
        {
            Environment.SetEnvironmentVariable(WorkflowPlanningService.EnabledEnvVar, null);
        }
    }

    // ── Projection validates + the map items binding resolves to the subtasks ──

    [Theory]
    [InlineData("analysis")]   // llm.complete body
    [InlineData("coding")]     // agent.code body — a CanSuspend node + a structurally different graph; the flagship path must also validate
    public void Projection_of_a_representative_plan_passes_DefinitionValidator(string recommendedKind)
    {
        var definition = Projector().Project(SamplePlan(recommendedKind));

        var result = BuildValidator().Validate(definition);

        result.IsValid.ShouldBeTrue($"projected {recommendedKind} definition must validate; errors: {string.Join("; ", result.Errors)}");
    }

    [Fact]
    public void Projection_binds_flow_map_items_to_the_baked_subtasks_input()
    {
        var plan = SamplePlan("analysis");

        var definition = Projector().Project(plan);

        // The map binds its items to the declared `subtasks` input...
        var map = definition.Nodes.Single(n => n.Id == "map");
        map.Inputs.GetProperty("items").GetString().ShouldBe("{{input.subtasks}}");

        // ...whose DEFAULT bakes the plan's subtasks so the engine's BuildInputScope fans out for real.
        var subtasksInput = definition.Inputs.Single(i => i.Name == "subtasks");
        subtasksInput.Default.ShouldNotBeNull();
        var baked = subtasksInput.Default!.Value;
        baked.GetArrayLength().ShouldBe(plan.Subtasks.Count);
        baked[0].GetProperty("title").GetString().ShouldBe(plan.Subtasks[0].Title);
        baked[0].GetProperty("instruction").GetString().ShouldBe(plan.Subtasks[0].Instruction);
    }

    [Fact]
    public void Projection_carries_per_subtask_harness_and_model_filling_the_default_for_unallocated()
    {
        var plan = new PlannedWorkflow
        {
            Goal = "g",
            RecommendedWorkflowKind = "coding",
            Subtasks = new[]
            {
                new PlannedSubtask { Id = "s1", Title = "t1", Instruction = "i1", Harness = "claude-code", Model = "metis-coder-max" },
                new PlannedSubtask { Id = "s2", Title = "t2", Instruction = "i2" },   // no allocation → default harness, empty model
            },
        };

        var definition = Projector().Project(plan);

        var baked = definition.Inputs.Single(i => i.Name == "subtasks").Default!.Value;
        baked[0].GetProperty("harness").GetString().ShouldBe("claude-code", "the planner's per-subtask harness is carried onto its map item");
        baked[0].GetProperty("model").GetString().ShouldBe("metis-coder-max");
        baked[1].GetProperty("harness").GetString().ShouldBe("codex-cli", "an unallocated subtask is filled with the default so {{item.harness}} always resolves");
        baked[1].GetProperty("model").GetString().ShouldBe("");

        // The single agent body runs each branch with ITS element's allocation.
        var body = definition.Nodes.Single(n => n.Id == "body");
        body.Config.GetProperty("harness").GetString().ShouldBe("{{item.harness}}");
        body.Config.GetProperty("model").GetString().ShouldBe("{{item.model}}");
    }

    [Fact]
    public void Projection_clamps_a_hallucinated_or_empty_harness_to_the_registered_default()
    {
        var plan = new PlannedWorkflow
        {
            Goal = "g",
            RecommendedWorkflowKind = "coding",
            Subtasks = new[]
            {
                new PlannedSubtask { Id = "s1", Title = "t1", Instruction = "i1", Harness = "gpt-5-cli" },   // not a registered kind
                new PlannedSubtask { Id = "s2", Title = "t2", Instruction = "i2", Harness = "  " },          // blank
                new PlannedSubtask { Id = "s3", Title = "t3", Instruction = "i3", Harness = "CLAUDE-CODE" }, // registered, wrong case
            },
        };

        var baked = Projector().Project(plan).Inputs.Single(i => i.Name == "subtasks").Default!.Value;

        baked[0].GetProperty("harness").GetString().ShouldBe("codex-cli", "a hallucinated kind the registry can't resolve falls to the default — never reaches run time to throw");
        baked[1].GetProperty("harness").GetString().ShouldBe("codex-cli", "a blank kind falls to the default");
        baked[2].GetProperty("harness").GetString().ShouldBe("claude-code", "a registered kind is canonicalised to its registered casing");
    }

    // ── Recommended-kind switch: coding → agent.code body, else → llm.complete ─

    [Theory]
    [InlineData("coding", "agent.code")]
    [InlineData("analysis", "llm.complete")]
    [InlineData("anything-else", "llm.complete")]
    public void Body_node_type_follows_recommended_workflow_kind(string kind, string expectedBodyTypeKey)
    {
        var definition = Projector().Project(SamplePlan(kind));

        var body = definition.Nodes.Single(n => n.Id == "body");
        body.TypeKey.ShouldBe(expectedBodyTypeKey);
        body.ParentId.ShouldBe("map");
    }

    // ── PR-D.5: CoordinatorSchema commit-contract pin ─────────────────────────

    [Fact]
    public void CoordinatorSchema_shape_is_pinned()
    {
        var root = CoordinatorSchema.ResponseSchema;

        root.GetProperty("type").GetString().ShouldBe("object");
        root.GetProperty("additionalProperties").GetBoolean().ShouldBeFalse();

        var required = root.GetProperty("required").EnumerateArray().Select(e => e.GetString()).ToList();
        required.ShouldBe(new[] { "decision" });

        var props = root.GetProperty("properties");

        // decision: the enum the loop's termination depends on — all four values pinned.
        var decisionEnum = props.GetProperty("decision").GetProperty("enum").EnumerateArray().Select(e => e.GetString()).ToList();
        decisionEnum.ShouldBe(new[] { "done", "rework", "ask_human", "abort" });

        // reworkSubtasks reuses the planner's {id,title,instruction} subtask shape so a rework round re-seeds the map.
        var item = props.GetProperty("reworkSubtasks").GetProperty("items");
        item.GetProperty("additionalProperties").GetBoolean().ShouldBeFalse();
        item.GetProperty("required").EnumerateArray().Select(e => e.GetString()).ToList().ShouldBe(new[] { "id", "title", "instruction" });

        props.GetProperty("riskLevel").GetProperty("enum").EnumerateArray().Select(e => e.GetString()).ToList().ShouldBe(new[] { "low", "medium", "high" });
    }

    [Fact]
    public void CoordinatorDecision_round_trips_from_a_schema_valid_object()
    {
        var schemaValid = JsonSerializer.SerializeToElement(new
        {
            decision = "rework",
            summary = "Round 1 partial; one subtask needs another pass.",
            reworkSubtasks = new object[] { new { id = "r1", title = "Retry", instruction = "Fix the gap" } },
            riskLevel = "medium",
        });

        var decision = schemaValid.Deserialize<CoordinatorDecision>(CoordinatorSchema.Options);

        decision.ShouldNotBeNull();
        decision!.Decision.ShouldBe("rework");
        decision.ReworkSubtasks.Count.ShouldBe(1);
        decision.ReworkSubtasks[0].Title.ShouldBe("Retry");
        decision.RiskLevel.ShouldBe("medium");
    }

    // ── PR-D.5: the coordinated projection validates (both body kinds) ─────────

    [Theory]
    [InlineData("analysis")]   // coordinator + map body = llm.complete
    [InlineData("coding")]     // map body = agent.code (a CanSuspend node inside a map inside a loop)
    public void Coordinated_projection_passes_DefinitionValidator(string recommendedKind)
    {
        var definition = Projector().ProjectCoordinated(SamplePlan(recommendedKind), new CoordinationOptions());

        var result = BuildValidator().Validate(definition);

        result.IsValid.ShouldBeTrue($"coordinated {recommendedKind} definition must validate; errors: {string.Join("; ", result.Errors)}");
    }

    [Fact]
    public void Coordinated_coding_body_uses_a_stable_literal_harness_not_a_per_item_ref()
    {
        // The coordinator's reworkSubtasks (rounds 2+) carry NO per-item harness, so a {{item.harness}} body would
        // resolve empty and trip the agent.code 'harness is required' guard. The coordinated body must bake a literal
        // registered kind so EVERY round stays runnable. (Per-subtask Auto-allocation is the one-shot path's job.)
        var body = Projector().ProjectCoordinated(SamplePlan("coding"), new CoordinationOptions()).Nodes.Single(n => n.Id == "body");

        body.TypeKey.ShouldBe("agent.code");
        body.Config.GetProperty("harness").GetString().ShouldBe("codex-cli", "coordinated bakes a literal harness so rework rounds (no per-item harness) still run");
        body.Config.TryGetProperty("model", out _).ShouldBeFalse("no per-item model ref on the coordinated body — there's no allocator across rework rounds");
    }

    [Fact]
    public void Coordinated_loop_wires_the_two_loop_variables_termination_and_round_cap()
    {
        var definition = Projector().ProjectCoordinated(SamplePlan("analysis"), new CoordinationOptions { MaxRounds = 7 });

        var loop = definition.Nodes.Single(n => n.Id == "loop");
        loop.TypeKey.ShouldBe("flow.loop");

        var vars = loop.Config.GetProperty("loopVariables").EnumerateArray().ToList();
        vars.Count.ShouldBe(2);

        // subtasks: seeds round 1 from the baked input, re-seeds from the coordinator's reworkSubtasks.
        var subtasksVar = vars.Single(v => v.GetProperty("name").GetString() == "subtasks");
        subtasksVar.GetProperty("ref").GetString().ShouldBe("{{input.subtasks}}");
        subtasksVar.GetProperty("update").GetString().ShouldBe("{{nodes.coordinator.outputs.json.reworkSubtasks}}");

        // decision: starts "rework", updates from the coordinator's decision — drives termination.
        var decisionVar = vars.Single(v => v.GetProperty("name").GetString() == "decision");
        decisionVar.GetProperty("value").GetString().ShouldBe("rework");
        decisionVar.GetProperty("update").GetString().ShouldBe("{{nodes.coordinator.outputs.json.decision}}");

        // termination: OR over decision eq done / abort.
        var termination = loop.Config.GetProperty("termination");
        termination.GetProperty("logic").GetString().ShouldBe("or");
        var conditions = termination.GetProperty("conditions").EnumerateArray().ToList();
        conditions.Select(c => c.GetProperty("value").GetString()).OrderBy(v => v).ShouldBe(new[] { "abort", "done" });
        conditions.ShouldAllBe(c => c.GetProperty("ref").GetString() == "{{loop.decision}}" && c.GetProperty("op").GetString() == "eq");

        // maxIterations == the operator's round cap.
        loop.Config.GetProperty("maxIterations").GetInt32().ShouldBe(7);
    }

    [Fact]
    public void Coordinated_map_binds_items_to_the_loop_subtasks_and_coordinator_carries_the_schema()
    {
        var definition = Projector().ProjectCoordinated(SamplePlan("analysis"), new CoordinationOptions());

        // The map fans over the CURRENT ROUND's subtasks (the loop var), not the static input.
        var map = definition.Nodes.Single(n => n.Id == "map");
        map.TypeKey.ShouldBe("flow.map");
        map.ParentId.ShouldBe("loop");
        map.Inputs.GetProperty("items").GetString().ShouldBe("{{loop.subtasks}}");

        // The coordinator is an llm.complete carrying the CoordinatorSchema as its responseSchema (so its decision lands on `json`).
        var coordinator = definition.Nodes.Single(n => n.Id == "coordinator");
        coordinator.TypeKey.ShouldBe("llm.complete");
        coordinator.ParentId.ShouldBe("loop");
        coordinator.Config.GetProperty("provider").GetString().ShouldBe("Anthropic");
        var schema = coordinator.Config.GetProperty("responseSchema");
        schema.GetProperty("required").EnumerateArray().Select(e => e.GetString()).ShouldBe(new[] { "decision" });
        coordinator.Inputs.GetProperty("userPrompt").GetString().ShouldContain("{{nodes.map.outputs.results}}",
            customMessage: "the coordinator must fold the round's map results into its prompt so it can judge");
    }

    [Fact]
    public void Coordinated_false_path_yields_the_one_shot_shape_no_loop()
    {
        // The default (one-shot) projection has NO flow.loop — coordinated mode is the only thing that adds one.
        var oneShot = Projector().Project(SamplePlan("analysis"));

        oneShot.Nodes.ShouldNotContain(n => n.TypeKey == "flow.loop");
        oneShot.Nodes.ShouldContain(n => n.Id == "map" && n.ParentId == null, "the one-shot map is top-level, not inside a loop");
    }

    [Fact]
    public void Coordinated_map_parallelism_cap_folds_into_the_map_config()
    {
        var definition = Projector().ProjectCoordinated(SamplePlan("analysis"), new CoordinationOptions { MaxParallelism = 3 });

        var map = definition.Nodes.Single(n => n.Id == "map");
        map.Config.GetProperty("maxParallelism").GetInt32().ShouldBe(3);
    }

    // ── Service folds the grounding into the request before planning ──────────

    [Fact]
    public async Task Flag_on_grounds_with_the_request_repository_and_team_then_passes_it_to_the_planner()
    {
        Environment.SetEnvironmentVariable(WorkflowPlanningService.EnabledEnvVar, "1");
        try
        {
            var repositoryId = Guid.NewGuid();
            var teamId = Guid.NewGuid();
            var planner = new RecordingPlanner(SamplePlan("analysis"));
            var grounding = new RecordingGrounding("Repository top-level layout for acme/api. Top-level entries:\n- src (directory)");
            var service = new WorkflowPlanningService(planner, Projector(), BuildValidator(), grounding);

            await service.PlanFromTaskAsync(new WorkflowPlanRequest { TaskText = "do it", TeamId = teamId, RepositoryId = repositoryId }, CancellationToken.None);

            // The service grounded against the SAME repo + team the request carried (team never the wire — sourced upstream).
            grounding.Invocations.ShouldBe(1);
            grounding.SeenRepositoryId.ShouldBe(repositoryId);
            grounding.SeenTeamId.ShouldBe(teamId);

            // ...and folded the result into request.GroundingContext so the planner SAW it (the planner stays a pure consumer).
            planner.LastRequest.ShouldNotBeNull();
            planner.LastRequest!.GroundingContext.ShouldContain("Repository top-level layout");
        }
        finally
        {
            Environment.SetEnvironmentVariable(WorkflowPlanningService.EnabledEnvVar, null);
        }
    }

    // ── RepoGroundingProvider: null repo + honest string assembly ─────────────

    [Fact]
    public async Task Grounding_for_a_null_repository_is_null_so_the_planner_runs_task_only()
    {
        // null repositoryId returns before any DB/provider use — db is never dereferenced, so null! is safe here.
        var provider = new RepoGroundingProvider(db: null!, registry: null!, scopeChecker: null!, logger: NullLogger<RepoGroundingProvider>.Instance);

        var grounding = await provider.BuildGroundingAsync(repositoryId: null, teamId: Guid.NewGuid(), CancellationToken.None);

        grounding.ShouldBeNull();
    }

    [Fact]
    public void Grounding_summary_is_an_honest_top_level_layout_and_never_over_claims()
    {
        var repo = new Repository
        {
            Id = Guid.NewGuid(), TeamId = Guid.NewGuid(), FullPath = "acme/api", DefaultBranch = "main",
            ProviderInstance = new ProviderInstance { Provider = ProviderKind.Git },
        };
        var entries = new RemoteTreeEntry[]
        {
            new() { Name = "src", Path = "src", Type = RemoteTreeEntryType.Directory },
            new() { Name = "README.md", Path = "README.md", Type = RemoteTreeEntryType.File },
        };

        var summary = RepoGroundingProvider.BuildSummary(repo, entries);

        // Honest framing: the repo + a real top-level entry are named, framed as a layout only.
        summary.ShouldContain("acme/api");
        summary.ShouldContain("top-level layout", Case.Insensitive);
        summary.ShouldContain("src");
        summary.ShouldContain("README.md");

        // Over-claim guard: a top-level listing must NEVER claim the code was analyzed or read.
        summary.ShouldNotContain("analyzed your codebase", Case.Insensitive);
        summary.ShouldNotContain("read your code", Case.Insensitive);
    }

    [Fact]
    public void Grounding_summary_for_an_empty_root_says_so_honestly()
    {
        var repo = new Repository
        {
            Id = Guid.NewGuid(), TeamId = Guid.NewGuid(), FullPath = "acme/api", DefaultBranch = "main",
            ProviderInstance = new ProviderInstance { Provider = ProviderKind.Git },
        };

        var summary = RepoGroundingProvider.BuildSummary(repo, Array.Empty<RemoteTreeEntry>());

        summary.ShouldContain("acme/api");
        summary.ShouldContain("empty", Case.Insensitive, "an empty root must be stated honestly, not faked into entries");
        summary.ShouldNotContain("Top-level entries:", customMessage: "no entries header when the root is empty");
    }

    [Fact]
    public void Grounding_summary_caps_a_wide_root_and_reports_the_remainder()
    {
        var repo = new Repository
        {
            Id = Guid.NewGuid(), TeamId = Guid.NewGuid(), FullPath = "acme/api", DefaultBranch = "main",
            ProviderInstance = new ProviderInstance { Provider = ProviderKind.Git },
        };
        // 42 entries — two past the 40-entry cap that keeps the prompt bounded for a wide repo root.
        var entries = Enumerable.Range(0, 42).Select(i => new RemoteTreeEntry { Name = $"e{i}", Path = $"e{i}", Type = RemoteTreeEntryType.File }).ToArray();

        var summary = RepoGroundingProvider.BuildSummary(repo, entries);

        summary.ShouldContain("e0");
        summary.ShouldContain("e39", customMessage: "the first 40 entries are listed");
        summary.ShouldNotContain("- e40 ", customMessage: "the 41st entry is past the cap and must not be listed");
        summary.ShouldContain("and 2 more", customMessage: "the truncated remainder is reported honestly");
    }

    // ── Planner prompt folds grounding in honestly (over-claim guard) ──────────

    [Fact]
    public void Planner_user_prompt_with_grounding_frames_it_as_top_level_and_never_over_claims()
    {
        var prompt = LlmWorkflowPlanner.BuildUserPromptForTest(new WorkflowPlanRequest
        {
            TaskText = "Improve onboarding",
            TeamId = Guid.NewGuid(),
            GroundingContext = "Repository top-level layout for acme/api. Top-level entries:\n- src (directory)",
        });

        prompt.ShouldContain("Improve onboarding");
        prompt.ShouldContain("top-level", Case.Insensitive);
        prompt.ShouldContain("src");

        prompt.ShouldNotContain("analyzed your codebase", Case.Insensitive);
        prompt.ShouldNotContain("read your code", Case.Insensitive);
    }

    [Fact]
    public void Planner_user_prompt_without_grounding_omits_the_layout_section()
    {
        var prompt = LlmWorkflowPlanner.BuildUserPromptForTest(new WorkflowPlanRequest { TaskText = "Just the task", TeamId = Guid.NewGuid() });

        prompt.ShouldContain("Just the task");
        prompt.ShouldNotContain("top-level layout", Case.Insensitive);
    }

    [Fact]
    public void Planner_user_prompt_carries_the_capability_catalog_when_provided()
    {
        var catalog = CapabilityCatalog.Render(
            new IAgentHarness[] { new CatalogHarnessStub("claude-code", "Anthropic", "Custom") },
            new[] { new PoolModelInfo("metis-coder-max", "Anthropic") });

        var prompt = LlmWorkflowPlanner.BuildUserPromptForTest(new WorkflowPlanRequest { TaskText = "Build it", TeamId = Guid.NewGuid() }, catalog);

        prompt.ShouldContain("Build it");
        prompt.ShouldContain("claude-code — drives: Anthropic, Custom", Case.Sensitive, "the planner sees the harness↔provider map");
        prompt.ShouldContain("metis-coder-max — Anthropic", Case.Sensitive, "the planner sees the team's pool");
    }

    private sealed class CatalogHarnessStub : IAgentHarness, IModelCredentialProjector
    {
        public CatalogHarnessStub(string kind, params string[] providers) { Kind = kind; SupportedProviders = providers; }
        public string Kind { get; }
        public string Version => "test";
        public IReadOnlyList<string> Models => Array.Empty<string>();
        public IReadOnlyList<string> SupportedProviders { get; }
        public SandboxSpec BuildInvocation(AgentTask task) => throw new NotSupportedException();
        public IReadOnlyList<AgentEvent> ParseEvents(string rawLine) => throw new NotSupportedException();
        public AgentRunResult BuildResult(IReadOnlyList<AgentEvent> events, int exitCode) => throw new NotSupportedException();
        public IReadOnlyDictionary<string, string> ProjectToEnv(ResolvedModelCredential credential) => throw new NotSupportedException();
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    /// <summary>A projector over a two-harness registry (codex-cli + claude-code) — the closed set the projector clamps the planner's authored per-subtask harness to.</summary>
    private static WorkflowPlanProjector Projector() => new(new AgentHarnessRegistry(new IAgentHarness[]
    {
        new CatalogHarnessStub("codex-cli", "OpenAI", "OpenRouter", "Ollama", "Custom"),
        new CatalogHarnessStub("claude-code", "Anthropic", "Custom"),
    }));

    private static WorkflowPlanRequest SampleRequest() =>
        new() { TaskText = "Improve the onboarding flow", TeamId = Guid.NewGuid() };

    private static PlannedWorkflow SamplePlan(string recommendedKind) => new()
    {
        Goal = "Improve onboarding",
        Subtasks = new[]
        {
            new PlannedSubtask { Id = "s1", Title = "Audit", Instruction = "Review the current funnel" },
            new PlannedSubtask { Id = "s2", Title = "Fix", Instruction = "Address the worst drop-off" },
        },
        SuccessCriteria = new[] { "Drop-off reduced" },
        Risks = new[] { "Unknown analytics gaps" },
        RecommendedWorkflowKind = recommendedKind,
    };

    /// <summary>The REAL validator over the REAL builtin node runtimes the projection wires — so the test proves the production graph, not a stub.</summary>
    private static DefinitionValidator BuildValidator()
    {
        var emptyRegistry = new LLMClientRegistry(Array.Empty<ILLMClient>());

        var nodes = new INodeRuntime[]
        {
            new TriggerManualNode(),
            new FlowWaitApprovalNode(),
            new LogicIfNode(),
            new FlowMapNode(),
            new FlowMapStartNode(),
            new FlowLoopNode(),
            new FlowLoopStartNode(),
            new AgentCodeNode(),
            new LlmCompleteNode(emptyRegistry, null!),   // manifest-only (validator reads Manifest, never RunAsync) → selector unused
            new TerminalNode(),
        };

        return new DefinitionValidator(new NodeRegistry(nodes));
    }

    private sealed class RecordingPlanner : IWorkflowPlanner
    {
        private readonly PlannedWorkflow? _plan;

        public RecordingPlanner(PlannedWorkflow? plan = null) { _plan = plan; }

        public int Invocations { get; private set; }

        /// <summary>The request the planner last SAW — proves the service folded the grounding into request.GroundingContext before calling it.</summary>
        public WorkflowPlanRequest? LastRequest { get; private set; }

        public Task<PlannedWorkflow> PlanAsync(WorkflowPlanRequest request, CancellationToken cancellationToken)
        {
            Invocations++;
            LastRequest = request;
            return Task.FromResult(_plan ?? throw new InvalidOperationException("no plan configured"));
        }
    }

    /// <summary>A grounding provider that echoes a canned string and records what it was asked to ground — lets the service test prove the repositoryId/teamId threading without a DB.</summary>
    private sealed class RecordingGrounding : IRepoGroundingProvider
    {
        private readonly string? _grounding;

        public RecordingGrounding(string? grounding = null) { _grounding = grounding; }

        public Guid? SeenRepositoryId { get; private set; }
        public Guid SeenTeamId { get; private set; }
        public int Invocations { get; private set; }

        public Task<string?> BuildGroundingAsync(Guid? repositoryId, Guid teamId, CancellationToken cancellationToken)
        {
            Invocations++;
            SeenRepositoryId = repositoryId;
            SeenTeamId = teamId;
            return Task.FromResult(_grounding);
        }
    }
}
