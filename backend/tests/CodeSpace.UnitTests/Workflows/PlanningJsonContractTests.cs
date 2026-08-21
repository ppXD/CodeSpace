using System.Text.Json;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Workflows.Planning;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Agents.Benchmark;
using CodeSpace.Messages.Dtos.Workflows.Planning;
using CodeSpace.Messages.Plans;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

/// <summary>Exact JSON-name contracts between the planning DTOs and the schemas that constrain model output.</summary>
[Trait("Category", "Unit")]
public sealed class PlanningJsonContractTests
{
    [Fact]
    public void PlannedWorkflow_production_wire_names_match_the_planner_schema_at_every_bound_object()
    {
        var serialized = JsonSerializer.SerializeToElement(FullPlan(), AgentJson.Options);
        var schema = PlannerSchema.ResponseSchema;

        AssertProperties(serialized, schema, "authoredByModel", "injectedLessonIds", "lessonArm");
        AssertRequired<PlannedWorkflow>(schema, AgentJson.Options);

        var serializedSubtask = serialized.GetProperty("subtasks")[0];
        var subtaskSchema = schema.GetProperty("properties").GetProperty("subtasks").GetProperty("items");
        AssertProperties(serializedSubtask, subtaskSchema);
        AssertRequired<PlannedSubtask>(subtaskSchema, AgentJson.Options);

        var serializedAcceptance = serializedSubtask.GetProperty("acceptance");
        var acceptanceSchema = subtaskSchema.GetProperty("properties").GetProperty("acceptance");
        AssertProperties(serializedAcceptance, acceptanceSchema, "protectedPaths", "setupCommand", "timeoutSeconds");
        AssertRequired<SupervisorAcceptanceSpec>(acceptanceSchema, AgentJson.Options);

        var serializedRubric = serializedAcceptance.GetProperty("rubric");
        var rubricSchema = acceptanceSchema.GetProperty("properties").GetProperty("rubric");
        AssertProperties(serializedRubric, rubricSchema, "judgeModelId");
        AssertRequired<AcceptanceRubric>(rubricSchema, AgentJson.Options);

        var serializedCriterion = serializedRubric.GetProperty("criteria")[0];
        var criterionSchema = rubricSchema.GetProperty("properties").GetProperty("criteria").GetProperty("items");
        AssertProperties(serializedCriterion, criterionSchema);
        AssertRequired<AcceptanceRubricCriterion>(criterionSchema, AgentJson.Options);

        var serializedQuestion = serialized.GetProperty("questions")[0];
        var questionSchema = schema.GetProperty("properties").GetProperty("questions").GetProperty("items");
        AssertProperties(serializedQuestion, questionSchema);
        AssertRequired<WorkPlanQuestion>(questionSchema, AgentJson.Options, "options");

        var serializedOption = serializedQuestion.GetProperty("options")[0];
        var optionSchema = questionSchema.GetProperty("properties").GetProperty("options").GetProperty("items");
        AssertProperties(serializedOption, optionSchema);
        AssertRequired<WorkPlanQuestionOption>(optionSchema, AgentJson.Options);
    }

    [Fact]
    public void CoordinatorDecision_declared_wire_names_match_its_narrow_coordinator_schema()
    {
        var serialized = JsonSerializer.SerializeToElement(new CoordinatorDecision
        {
            Decision = "rework",
            Summary = "one more round",
            ReworkSubtasks = new[] { FullSubtask() },
            Question = "continue?",
            RiskLevel = "medium",
        }, CoordinatorSchema.Options);
        var schema = CoordinatorSchema.ResponseSchema;

        AssertProperties(serialized, schema);
        AssertRequired<CoordinatorDecision>(schema, CoordinatorSchema.Options);

        var serializedSubtask = serialized.GetProperty("reworkSubtasks")[0];
        var subtaskSchema = schema.GetProperty("properties").GetProperty("reworkSubtasks").GetProperty("items");
        AssertProperties(serializedSubtask, subtaskSchema, "acceptance", "acceptanceCriteria", "dependsOn", "harness", "kind", "model");
        AssertRequired<PlannedSubtask>(subtaskSchema, CoordinatorSchema.Options);
    }

    private static PlannedWorkflow FullPlan() => new()
    {
        Goal = "ship",
        Subtasks = new[] { FullSubtask() },
        SuccessCriteria = new[] { "done" },
        Risks = new[] { "risk" },
        AuthoredByModel = "model-row",
        LessonArm = "injected",
        InjectedLessonIds = new[] { Guid.NewGuid() },
        RecommendedWorkflowKind = "coding",
        HasEnoughContext = false,
        Assumptions = new[] { "default" },
        Questions = new[]
        {
            new WorkPlanQuestion
            {
                Id = "q1",
                Question = "which?",
                Options = new[] { new WorkPlanQuestionOption { Id = "a", Label = "A" }, new WorkPlanQuestionOption { Id = "b", Label = "B" } },
                RecommendedOptionId = "a",
                AllowFreeText = true,
            },
        },
    };

    private static PlannedSubtask FullSubtask() => new()
    {
        Id = "s1",
        Title = "build",
        Instruction = "implement",
        Rationale = "needed",
        Harness = "codex-cli",
        Model = "model-row",
        Kind = "code",
        DependsOn = new[] { "s0" },
        AcceptanceCriteria = new[] { "clear" },
        Acceptance = new SupervisorAcceptanceSpec
        {
            Command = new[] { "dotnet", "test" },
            ProtectedPaths = new[] { "tests/check.sh" },
            Kind = BenchmarkGradingKind.LlmJudge,
            Description = "objective",
            Rubric = new AcceptanceRubric
            {
                Criteria = new[] { new AcceptanceRubricCriterion { Id = "c1", Requirement = "passes", Weight = 2 } },
                Threshold = 0.8,
                JudgeModelId = Guid.NewGuid(),
            },
            Schema = JsonSerializer.SerializeToElement(new { type = "object" }),
            TimeoutSeconds = 60,
            SetupCommand = new[] { "dotnet", "restore" },
        },
    };

    private static void AssertProperties(JsonElement serialized, JsonElement schema, params string[] dtoOnly)
    {
        var actual = serialized.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal).ToArray();
        var schemaProperties = schema.GetProperty("properties").EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal).ToArray();
        var expectedDtoOnly = dtoOnly.Order(StringComparer.Ordinal).ToArray();

        actual.Except(expectedDtoOnly, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray().ShouldBe(schemaProperties, ignoreOrder: false,
            customMessage: "every schema property must be an actual serializer-produced name, with only explicitly classified server/runtime fields outside the model schema");
        actual.Intersect(expectedDtoOnly, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray().ShouldBe(expectedDtoOnly, ignoreOrder: false,
            customMessage: "the explicit DTO-only list must name real serialized properties rather than becoming a stale allow-list");
        schemaProperties.Intersect(expectedDtoOnly, StringComparer.Ordinal).ShouldBeEmpty("a DTO-only property must not silently enter the model-authored schema");
    }

    private static void AssertRequired<T>(JsonElement schema, JsonSerializerOptions options, params string[] schemaOnlyRequired)
    {
        var serializerRequired = options.GetTypeInfo(typeof(T)).Properties
            .Where(property => property.IsRequired)
            .Select(property => property.Name)
            .Concat(schemaOnlyRequired)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var schemaRequired = schema.GetProperty("required").EnumerateArray()
            .Select(item => item.GetString().ShouldNotBeNull())
            .Order(StringComparer.Ordinal)
            .ToArray();
        var schemaProperties = schema.GetProperty("properties").EnumerateObject().Select(property => property.Name).ToHashSet(StringComparer.Ordinal);

        schemaRequired.ShouldBe(serializerRequired,
            "schema-required keys must be the serializer's exact required wire names, plus an explicitly stricter schema requirement when present");
        schemaRequired.Where(name => !schemaProperties.Contains(name)).ShouldBeEmpty("required may not name a property the schema does not declare");
    }
}
