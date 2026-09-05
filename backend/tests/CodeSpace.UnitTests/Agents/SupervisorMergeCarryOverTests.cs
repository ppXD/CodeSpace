using System.Text.Json;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Supervisor;
using CodeSpace.Core.Services.Supervisor.Deciders;
using CodeSpace.Core.Services.Supervisor.Executors;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Enums;
using Shouldly;

namespace CodeSpace.UnitTests.Agents;

/// <summary>
/// 🟢 Unit: CONSERVATION across a plan-generation boundary. A live run planned 3 subtasks, spawned them (all three
/// Succeeded and pushed), then re-planned three times before merging — so the plan window sliced the tape past every
/// spawn and <c>merge</c> logged "merged 0 prior agent result(s)", stranding three finished, pushed results and
/// leaving <c>publish</c> with 0 targets. Pins <see cref="SupervisorMergeContributors.Resolve"/>: the window path is
/// untouched whenever it yields anything, and ONLY a window that yields nothing falls back to the run's earlier
/// Succeeded, not-withheld, not-yet-merged agent runs. Also pins the recitation line that tells the brain the same
/// fact, so the prompt can never disagree with what <c>merge</c> would actually do.
/// </summary>
[Trait("Category", "Unit")]
public class SupervisorMergeCarryOverTests
{
    private static SupervisorAgentResult Unit(string status = "Succeeded", bool? acceptancePassed = null) =>
        new() { AgentRunId = Guid.NewGuid(), Status = status, ProducedBranch = "codespace/agent/x", AcceptancePassed = acceptancePassed };

    private static SupervisorPriorDecision Staging(string kind, params SupervisorAgentResult[] units)
    {
        var ids = units.Select(u => u.AgentRunId).ToArray();
        var outcome = SupervisorOutcome.FoldAgentResults(
            JsonSerializer.Serialize(new { agentRunIds = ids, agentCount = ids.Length }, AgentJson.Options), units);

        return new SupervisorPriorDecision { Id = Guid.NewGuid(), Sequence = 1, DecisionKind = kind, Status = SupervisorDecisionStatus.Succeeded, PayloadJson = "{}", OutcomeJson = outcome };
    }

    private static SupervisorPriorDecision Plan(string subtaskId = "s1") => new()
    {
        Id = Guid.NewGuid(), Sequence = 2, DecisionKind = SupervisorDecisionKinds.Plan, Status = SupervisorDecisionStatus.Succeeded,
        PayloadJson = $$"""{"goal":"replacement","subtasks":[{"id":"{{subtaskId}}","title":"{{subtaskId}}","instruction":"do it"}]}""", OutcomeJson = "{}",
    };

    private static SupervisorPriorDecision Merged(params Guid[] agentRunIds) => new()
    {
        Id = Guid.NewGuid(), Sequence = 3, DecisionKind = SupervisorDecisionKinds.Merge, Status = SupervisorDecisionStatus.Succeeded, PayloadJson = "{}",
        OutcomeJson = JsonSerializer.Serialize(new { merged = agentRunIds.Select(id => new { agentRunId = id, status = "Succeeded" }), count = agentRunIds.Length }, AgentJson.Options),
    };

    [Fact]
    public void A_window_that_yields_results_is_untouched_and_carries_nothing_over()
    {
        var old = Unit();
        var current = Unit();

        var selection = SupervisorMergeContributors.Resolve(new[] { Staging(SupervisorDecisionKinds.Spawn, old), Plan(), Staging(SupervisorDecisionKinds.Spawn, current) });

        selection.AgentRunIds.ShouldBe(new[] { current.AgentRunId }, "the active generation staged its own work — an earlier generation's unit stays audit evidence, exactly as before");
        selection.CarriedOverFromEarlierGenerations.ShouldBe(0);
    }

    [Fact]
    public void A_replan_after_the_work_finished_carries_the_earlier_succeeded_results_over()
    {
        // The live trajectory: plan(3) → spawn×3 (all Succeeded, all pushed) → plan → plan → plan → merge. The
        // window starts at the newest valid plan, which has no spawn after it, so the merge saw nothing at all.
        var a = Unit();
        var b = Unit();
        var c = Unit();

        var selection = SupervisorMergeContributors.Resolve(new[]
        {
            Plan("s1"), Staging(SupervisorDecisionKinds.Spawn, a, b, c), Plan("s2"), Plan("s3"),
        });

        selection.AgentRunIds.ShouldBe(new[] { a.AgentRunId, b.AgentRunId, c.AgentRunId },
            "three finished, pushed results must not become unmergeable because the model re-planned after they landed");
        selection.CarriedOverFromEarlierGenerations.ShouldBe(3);
    }

    [Fact]
    public void An_already_merged_earlier_result_is_not_carried_over_again()
    {
        var a = Unit();
        var b = Unit();

        var tape = new[] { Plan("s1"), Staging(SupervisorDecisionKinds.Spawn, a, b), Merged(a.AgentRunId), Plan("s2") };

        var selection = SupervisorMergeContributors.Resolve(tape);

        selection.AgentRunIds.ShouldBe(new[] { b.AgentRunId }, "a result a prior merge already consolidated is not unmerged work");
        selection.CarriedOverFromEarlierGenerations.ShouldBe(1);
    }

    [Fact]
    public void Everything_earlier_already_merged_carries_nothing_over()
    {
        var a = Unit();

        SupervisorMergeContributors.Resolve(new[] { Plan("s1"), Staging(SupervisorDecisionKinds.Spawn, a), Merged(a.AgentRunId), Plan("s2") })
            .CarriedOverFromEarlierGenerations.ShouldBe(0, "nothing is stranded, so the fallback must stay silent");
    }

    [Theory]
    [InlineData("Failed", null)]
    [InlineData("Cancelled", null)]
    [InlineData("Succeeded", false)]
    public void Only_a_succeeded_unwithheld_earlier_result_is_carried_over(string status, bool? acceptancePassed)
    {
        var unmergeable = Unit(status, acceptancePassed);

        var selection = SupervisorMergeContributors.Resolve(new[] { Plan("s1"), Staging(SupervisorDecisionKinds.Spawn, unmergeable), Plan("s2") });

        selection.AgentRunIds.ShouldBeEmpty("the carry-over is conservation of FINISHED, accepted work — never a door around a failure or an acceptance rejection");
        selection.CarriedOverFromEarlierGenerations.ShouldBe(0);
    }

    [Fact]
    public void A_waived_earlier_result_is_not_carried_over()
    {
        var waived = Unit() with { AcceptanceVerdict = CodeSpace.Messages.Contracts.VerificationDisposition.Waived };

        SupervisorMergeContributors.Resolve(new[] { Plan("s1"), Staging(SupervisorDecisionKinds.Spawn, waived), Plan("s2") })
            .AgentRunIds.ShouldBeEmpty("WAIVED ≠ PASSED — the carry-over uses the SAME withhold door as the window path");
    }

    [Fact]
    public void The_merge_door_resolves_the_same_ids_the_selection_names()
    {
        var a = Unit();
        var context = new SupervisorTurnContext { Goal = "g", PriorDecisions = new[] { Plan("s1"), Staging(SupervisorDecisionKinds.Spawn, a), Plan("s2") } };

        RealSupervisorActionExecutor.ResolveAgentRunIdsToMerge(context)
            .ShouldBe(new[] { a.AgentRunId }, "the executor's door and the shared selection are one function — they cannot drift");
    }

    [Fact]
    public void The_recitation_tells_the_brain_when_earlier_results_are_waiting_to_be_merged()
    {
        var a = Unit();
        var b = Unit();

        var recited = SupervisorRecitation.Render(new[] { Plan("s1"), Staging(SupervisorDecisionKinds.Spawn, a, b), Plan("s2") });

        recited.ShouldNotBeNull();
        recited.ShouldContain("2 succeeded result(s) from earlier plan generations are not merged yet", Case.Sensitive,
            "the brain must be told the work still exists, or it re-plans and re-spawns what is already done");
    }

    [Fact]
    public void The_recitation_says_nothing_when_the_active_generation_owns_its_own_results()
    {
        var current = Unit();

        SupervisorRecitation.Render(new[] { Plan("s1"), Staging(SupervisorDecisionKinds.Spawn, current) })
            .ShouldNotContain("earlier plan generations", Case.Sensitive, "an ordinary run's prompt stays byte-identical");
    }
}
