using System.Text.Json;
using CodeSpace.Core.Services.Workflows.Engine;
using CodeSpace.Core.Services.Workflows.Llm;
using CodeSpace.Core.Services.Workflows.Nodes;
using CodeSpace.Core.Services.Workflows.Nodes.Builtin;
using CodeSpace.Core.Services.Workflows.Planning;
using CodeSpace.Messages.Commands.Workflows;
using CodeSpace.Messages.Dtos.Workflows;
using CodeSpace.Messages.Dtos.Workflows.Planning;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

/// <summary>
/// Unit coverage for the task-first planner (PR-D Slice 1): the response-schema commit-contract, the
/// flag env-const (Rule 8 pin), the flag-OFF disabled path (no planner call), and the projector — its
/// emitted graph passes the REAL DefinitionValidator, its flow.map items binding resolves to the subtasks,
/// and the recommended-kind switch picks the right body node type.
/// </summary>
[Trait("Category", "Unit")]
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
        itemRequired.ShouldBe(new[] { "id", "title", "instruction" });
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
            var service = new WorkflowPlanningService(planner, new WorkflowPlanProjector(), BuildValidator());

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
            var service = new WorkflowPlanningService(planner, new WorkflowPlanProjector(), BuildValidator());

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
        var definition = new WorkflowPlanProjector().Project(SamplePlan(recommendedKind));

        var result = BuildValidator().Validate(definition);

        result.IsValid.ShouldBeTrue($"projected {recommendedKind} definition must validate; errors: {string.Join("; ", result.Errors)}");
    }

    [Fact]
    public void Projection_binds_flow_map_items_to_the_baked_subtasks_input()
    {
        var plan = SamplePlan("analysis");

        var definition = new WorkflowPlanProjector().Project(plan);

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

    // ── Recommended-kind switch: coding → agent.code body, else → llm.complete ─

    [Theory]
    [InlineData("coding", "agent.code")]
    [InlineData("analysis", "llm.complete")]
    [InlineData("anything-else", "llm.complete")]
    public void Body_node_type_follows_recommended_workflow_kind(string kind, string expectedBodyTypeKey)
    {
        var definition = new WorkflowPlanProjector().Project(SamplePlan(kind));

        var body = definition.Nodes.Single(n => n.Id == "body");
        body.TypeKey.ShouldBe(expectedBodyTypeKey);
        body.ParentId.ShouldBe("map");
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

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
            new AgentCodeNode(),
            new LlmCompleteNode(emptyRegistry),
            new TerminalNode(),
        };

        return new DefinitionValidator(new NodeRegistry(nodes));
    }

    private sealed class RecordingPlanner : IWorkflowPlanner
    {
        private readonly PlannedWorkflow? _plan;

        public RecordingPlanner(PlannedWorkflow? plan = null) { _plan = plan; }

        public int Invocations { get; private set; }

        public Task<PlannedWorkflow> PlanAsync(WorkflowPlanRequest request, CancellationToken cancellationToken)
        {
            Invocations++;
            return Task.FromResult(_plan ?? throw new InvalidOperationException("no plan configured"));
        }
    }
}
