using CodeSpace.Core.Services.Supervisor;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Enums;
using Shouldly;

namespace CodeSpace.UnitTests.Agents;

/// <summary>🟢 Unit: the pure active-plan tape window shared by every current-head consumer.</summary>
[Trait("Category", "Unit")]
public class SupervisorPlanWindowTests
{
    [Fact]
    public void The_latest_valid_non_empty_plan_supersedes_every_earlier_generation()
    {
        var oldSpawn = Decision(SupervisorDecisionKinds.Spawn, 1, "{}");
        var firstPlan = Plan(2, ValidPlanPayload("old"));
        var firstGeneration = Decision(SupervisorDecisionKinds.Spawn, 3, "{}");
        var latestPlan = Plan(4, ValidPlanPayload("current"));
        var currentGeneration = Decision(SupervisorDecisionKinds.Spawn, 5, "{}");

        var window = SupervisorPlanWindow.Read(new[] { oldSpawn, firstPlan, firstGeneration, latestPlan, currentGeneration });

        window.IsPlanBounded.ShouldBeTrue();
        window.Decisions.ShouldBe(new[] { latestPlan, currentGeneration });
    }

    [Fact]
    public void Empty_malformed_structurally_invalid_and_failed_plans_do_not_open_a_generation()
    {
        var oldSpawn = Decision(SupervisorDecisionKinds.Spawn, 1, "{}");
        var empty = Plan(2, """{"goal":"g","subtasks":[]}""");
        var malformed = Plan(3, "{not-json");
        var dangling = Plan(4, """{"goal":"g","subtasks":[{"id":"s2","title":"S2","instruction":"do","dependsOn":["missing"]}]}""");
        var failed = Plan(5, ValidPlanPayload("failed"), SupervisorDecisionStatus.Failed);
        var tape = new[] { oldSpawn, empty, malformed, dangling, failed };

        var window = SupervisorPlanWindow.Read(tape);

        window.IsPlanBounded.ShouldBeFalse();
        window.Decisions.ShouldBeSameAs(tape, "an invalid plan cannot supersede work, and a plan-less/legacy tape stays allocation- and behavior-identical");
    }

    [Fact]
    public void Invalid_or_empty_plans_after_a_valid_plan_do_not_replace_its_boundary()
    {
        var oldSpawn = Decision(SupervisorDecisionKinds.Spawn, 1, "{}");
        var valid = Plan(2, ValidPlanPayload("current"));
        var currentSpawn = Decision(SupervisorDecisionKinds.Spawn, 3, "{}");
        var empty = Plan(4, """{"goal":"g","subtasks":[]}""");
        var malformed = Plan(5, "not-json");

        var window = SupervisorPlanWindow.Read(new[] { oldSpawn, valid, currentSpawn, empty, malformed });

        window.IsPlanBounded.ShouldBeTrue();
        window.Decisions.ShouldBe(new[] { valid, currentSpawn, empty, malformed });
    }

    [Fact]
    public void A_plan_less_legacy_tape_is_returned_verbatim()
    {
        var tape = new[] { Decision(SupervisorDecisionKinds.Spawn, 1, "{}"), Decision(SupervisorDecisionKinds.Merge, 2, "{}") };

        var window = SupervisorPlanWindow.Read(tape);

        window.IsPlanBounded.ShouldBeFalse();
        window.Decisions.ShouldBeSameAs(tape);
    }

    private static SupervisorPriorDecision Plan(long sequence, string payloadJson, SupervisorDecisionStatus status = SupervisorDecisionStatus.Succeeded) =>
        new() { Id = Guid.NewGuid(), Sequence = sequence, DecisionKind = SupervisorDecisionKinds.Plan, Status = status, PayloadJson = payloadJson, OutcomeJson = "{}" };

    private static SupervisorPriorDecision Decision(string kind, long sequence, string outcomeJson) =>
        new() { Id = Guid.NewGuid(), Sequence = sequence, DecisionKind = kind, Status = SupervisorDecisionStatus.Succeeded, PayloadJson = "{}", OutcomeJson = outcomeJson };

    private static string ValidPlanPayload(string id) => $$"""{"goal":"g","subtasks":[{"id":"{{id}}","title":"{{id}}","instruction":"do {{id}}"}]}""";
}
