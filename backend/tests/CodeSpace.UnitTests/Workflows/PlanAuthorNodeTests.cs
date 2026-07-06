using System.Text.Json;
using CodeSpace.Core.Services.Workflows.Nodes.Builtin;
using CodeSpace.Messages.Enums;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

/// <summary>
/// The plan.author node's static contract (triad S1): the manifest pins (type key, side-effect flag, the
/// config/input/output schema keys the frontend + validator drive off) and the pure config→request mapping
/// (edit-loop feedback fold, defensive reviewMode read, model pins).
/// </summary>
[Trait("Category", "Unit")]
public class PlanAuthorNodeTests
{
    private static PlanAuthorNode Node() => new(scopeFactory: null!);   // manifest + statics only — RunAsync is integration-covered

    [Fact]
    public void Manifest_pins_the_node_contract()
    {
        var node = Node();

        node.TypeKey.ShouldBe("plan.author");
        node.Manifest.IsSideEffecting.ShouldBeTrue("one structured LLM call per execution — billing, like llm.complete");

        ConfigKeys(node).ShouldBe(new[] { "plannerModelId", "reviewMode", "reviewerModelId", "flatPlan", "reviewerAgent", "repositoryId" }, ignoreOrder: true);
        InputKeys(node).ShouldBe(new[] { "goal", "grounding", "feedback", "criteria" }, ignoreOrder: true);
        OutputKeys(node).ShouldBe(new[] { "planId", "version", "goal", "items", "executionNeeded", "json" }, ignoreOrder: true);

        node.Manifest.InputSchema.GetProperty("required").EnumerateArray().Select(e => e.GetString())
            .ShouldBe(new[] { "goal" }, "only the goal is required — grounding/feedback are optional binds");
    }

    [Fact]
    public void A_flat_plan_config_appends_the_parallel_constraint_to_the_task_text()
    {
        var config = new Dictionary<string, JsonElement> { ["flatPlan"] = JsonSerializer.SerializeToElement(true) };

        var request = PlanAuthorNode.BuildPlanRequest(config, Guid.NewGuid(), "ship it", grounding: "", feedback: "");

        request.TaskText.ShouldContain(PlanAuthorNode.FlatPlanConstraint, customMessage: "the parallel map cannot honor ordering — the planner must be told");
    }

    [Fact]
    public void Criteria_fold_into_the_goal_and_empty_criteria_are_byte_identical()
    {
        // S5b: the operator's DoD reaches the STANDARD tier's planner — the plan and its per-item contracts
        // target it, and the plan critic judges against the same yardstick (its Goal is this task text).
        var folded = PlanAuthorNode.ComposeGoalWithCriteria("ship it", new[] { "tests pass", " ", "PR opened" });

        folded.ShouldContain("ship it");
        folded.ShouldContain("Acceptance criteria");
        folded.ShouldContain("- tests pass");
        folded.ShouldContain("- PR opened");

        PlanAuthorNode.ComposeGoalWithCriteria("ship it", Array.Empty<string>()).ShouldBe("ship it");
        PlanAuthorNode.ComposeGoalWithCriteria("ship it", new[] { " ", "" }).ShouldBe("ship it", "all-blank criteria collapse to the verbatim goal");
    }

    [Fact]
    public void The_flat_plan_constraint_is_pinned()
    {
        // The map projections bake flatPlan into every standard-tier launch — rewording the constraint is a
        // visible prompt-behaviour decision, not an invisible refactor.
        PlanAuthorNode.FlatPlanConstraint.ShouldBe("Constraint: author INDEPENDENT subtasks only — they run in PARALLEL, so do NOT use dependsOn.");
    }

    [Fact]
    public void Without_the_flat_flag_the_task_text_is_unchanged()
    {
        var request = PlanAuthorNode.BuildPlanRequest(new Dictionary<string, JsonElement>(), Guid.NewGuid(), "ship it", grounding: "", feedback: "");

        request.TaskText.ShouldNotContain(PlanAuthorNode.FlatPlanConstraint);
    }

    [Fact]
    public void Model_pins_and_review_mode_map_into_the_planner_request()
    {
        var plannerRow = Guid.NewGuid();
        var reviewerRow = Guid.NewGuid();
        var config = Config($$"""{"plannerModelId":"{{plannerRow}}","reviewMode":2,"reviewerModelId":"{{reviewerRow}}"}""");

        var request = PlanAuthorNode.BuildPlanRequest(config, teamId: Guid.NewGuid(), goal: "ship it", grounding: "repo layout", feedback: "");

        request.TaskText.ShouldBe("ship it");
        request.GroundingContext.ShouldBe("repo layout");
        request.BrainModelId.ShouldBe(plannerRow);
        request.Review.ShouldBe(ReviewMode.Improve);
        request.ReviewerModelId.ShouldBe(reviewerRow);
    }

    [Theory]
    [InlineData("""{"reviewMode":99}""")]     // out-of-range int → off, never a throw
    [InlineData("""{"reviewMode":"Gate"}""")] // wrong JSON type → off (nodes read defensively)
    [InlineData("""{}""")]                    // absent → off
    public void An_unusable_review_mode_degrades_to_off(string configJson)
    {
        var request = PlanAuthorNode.BuildPlanRequest(Config(configJson), Guid.NewGuid(), "goal", "", "");

        request.Review.ShouldBe(ReviewMode.None);
        request.BrainModelId.ShouldBeNull();
        request.GroundingContext.ShouldBeNull("a blank grounding is omitted, not an empty string");
    }

    [Fact]
    public void Feedback_folds_into_the_task_text_as_an_explicit_revision_instruction()
    {
        var composed = PlanAuthorNode.ComposeTaskText("ship it", "drop step 3");

        composed.ShouldStartWith("ship it");
        composed.ShouldContain("Revise the plan to address this feedback:");
        composed.ShouldContain("drop step 3");

        PlanAuthorNode.ComposeTaskText("ship it", "").ShouldBe("ship it", "no feedback → the goal verbatim (byte-identical first pass)");
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────

    private static IReadOnlyDictionary<string, JsonElement> Config(string json) =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;

    private static string[] ConfigKeys(PlanAuthorNode node) => Keys(node.Manifest.ConfigSchema);

    private static string[] InputKeys(PlanAuthorNode node) => Keys(node.Manifest.InputSchema);

    private static string[] OutputKeys(PlanAuthorNode node) => Keys(node.Manifest.OutputSchema);

    private static string[] Keys(JsonElement schema) => schema.GetProperty("properties").EnumerateObject().Select(p => p.Name).ToArray();
}
