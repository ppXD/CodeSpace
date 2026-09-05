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
/// untouched whenever the active generation has a mergeable result, and ONLY a generation with none falls back to the
/// run's earlier Succeeded, not-withheld, not-yet-CONSOLIDATED agent runs. Also pins the recitation line that tells the
/// brain the same fact, so the prompt can never disagree with what <c>merge</c> would actually do.
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

    /// <summary>A merge that INTEGRATED its contributors onto one reviewable head — consolidated.</summary>
    private static SupervisorPriorDecision IntegratedMerge(params Guid[] agentRunIds) => Merge(agentRunIds, new { status = "Clean", integratedBranch = "codespace/integration/turn1" });

    /// <summary>A merge whose integration CONFLICTED: it recorded the very same <c>merged[]</c> array (the fold is written before the integration runs) while landing nothing at all.</summary>
    private static SupervisorPriorDecision ConflictedMerge(params Guid[] agentRunIds) => Merge(agentRunIds, new { status = "Conflicted", reason = "overlapping edits" });

    /// <summary>A merge from a run whose integrate gate is OFF — <c>{ merged, count }</c> with NO integration block, the pre-SOTA-#3 shape <c>RealSupervisorActionExecutor.Merge.cs</c> still writes. Nothing was left un-landed, so the fold IS the consolidation.</summary>
    private static SupervisorPriorDecision GateOffMerge(params Guid[] agentRunIds) => Merge(agentRunIds, integration: null);

    private static SupervisorPriorDecision Merge(Guid[] agentRunIds, object? integration) => new()
    {
        Id = Guid.NewGuid(), Sequence = 3, DecisionKind = SupervisorDecisionKinds.Merge, Status = SupervisorDecisionStatus.Succeeded, PayloadJson = "{}",
        OutcomeJson = JsonSerializer.Serialize(new { merged = agentRunIds.Select(id => new { agentRunId = id, status = "Succeeded" }), count = agentRunIds.Length, integration }, AgentJson.Options),
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
    public void An_already_integrated_earlier_result_is_not_carried_over_again()
    {
        var a = Unit();
        var b = Unit();

        var tape = new[] { Plan("s1"), Staging(SupervisorDecisionKinds.Spawn, a, b), IntegratedMerge(a.AgentRunId), Plan("s2") };

        var selection = SupervisorMergeContributors.Resolve(tape);

        selection.AgentRunIds.ShouldBe(new[] { b.AgentRunId }, "a result a prior merge already integrated onto the head is not unmerged work");
        selection.CarriedOverFromEarlierGenerations.ShouldBe(1);
    }

    [Fact]
    public void Everything_earlier_already_integrated_carries_nothing_over()
    {
        var a = Unit();

        SupervisorMergeContributors.Resolve(new[] { Plan("s1"), Staging(SupervisorDecisionKinds.Spawn, a), IntegratedMerge(a.AgentRunId), Plan("s2") })
            .CarriedOverFromEarlierGenerations.ShouldBe(0, "nothing is stranded, so the fallback must stay silent");
    }

    [Fact]
    public void A_merge_from_a_run_with_the_integrate_gate_off_still_consolidates_what_it_folded()
    {
        // The narrowing must not reach the gate-off shape: with the integrate gate off a merge records NO integration
        // block at all, and the fold IS the whole product — nothing was left un-landed to re-offer. Keying the
        // exclusion on "landed a branch" alone would re-carry every gate-off run's contributors after every re-plan.
        var a = Unit();
        var b = Unit();

        var selection = SupervisorMergeContributors.Resolve(new[] { Plan("s1"), Staging(SupervisorDecisionKinds.Spawn, a, b), GateOffMerge(a.AgentRunId), Plan("s2") });

        selection.AgentRunIds.ShouldBe(new[] { b.AgentRunId }, "the gate-off fold consolidated what it named, exactly as it did before the narrowing");
        selection.CarriedOverFromEarlierGenerations.ShouldBe(1);
    }

    [Fact]
    public void A_conflicted_merge_does_not_permanently_strand_its_own_contributors()
    {
        // The merge executor writes merged[] BEFORE it attempts the integration, so a CONFLICTED merge records its
        // contributors while landing none of them. Reading that array as "already consolidated" made those two
        // results unreachable forever: the conflict is exactly the state a later merge (or resolver) exists to clear.
        var a = Unit();
        var b = Unit();

        var selection = SupervisorMergeContributors.Resolve(new[] { Plan("s1"), Staging(SupervisorDecisionKinds.Spawn, a, b), ConflictedMerge(a.AgentRunId, b.AgentRunId), Plan("s2") });

        selection.AgentRunIds.ShouldBe(new[] { a.AgentRunId, b.AgentRunId }, "a merge that integrated NOTHING consolidated nothing — its contributors are still unmerged work");
        selection.CarriedOverFromEarlierGenerations.ShouldBe(2);
    }

    [Fact]
    public void A_repeated_merge_over_the_same_stranded_tape_still_trips_the_no_progress_backstop()
    {
        // The safety net the narrowed exclusion leans on: re-folding the same ids is NOT progress. FoldNoProgressDecisions
        // accumulates every id any merge ever folded, so the second conflicted merge over the same contributors adds
        // nothing new and the streak keeps climbing toward the stall bound — the autonomous runaway backstop holds
        // even though the carry-over will happily offer those contributors again.
        var a = Unit();
        var tape = new[] { Plan("s1"), Staging(SupervisorDecisionKinds.Spawn, a), ConflictedMerge(a.AgentRunId), Plan("s2"), ConflictedMerge(a.AgentRunId) };

        SupervisorTurnService.FoldNoProgressDecisions(tape)
            .ShouldBe(2, "the trailing plan → merge produced no fresh progress: the second merge re-folds an id the first already counted");
    }

    [Fact]
    public void A_generation_whose_only_staged_unit_was_rejected_carries_earlier_work_over()
    {
        // The unified trigger: "the active generation has no MERGEABLE result" — staged nothing, OR everything it
        // staged is withheld. Before, the merge rung fired on this and the publish rung did not, so the same tape was
        // mergeable-by-carry-over and publishable-as-nothing.
        var done = Unit();
        var rejected = Unit(acceptancePassed: false);

        var selection = SupervisorMergeContributors.Resolve(new[]
        {
            Plan("s1"), Staging(SupervisorDecisionKinds.Spawn, done), Plan("s2"), Staging(SupervisorDecisionKinds.Spawn, rejected),
        });

        selection.AgentRunIds.ShouldBe(new[] { done.AgentRunId }, "a rejected unit is no result at all — the generation staged nothing a door to the head may take");
        selection.CarriedOverFromEarlierGenerations.ShouldBe(1);
    }

    [Fact]
    public void A_still_running_wave_is_the_generations_own_work_and_carries_nothing_over()
    {
        var done = Unit();
        var running = Unit("Running");

        var selection = SupervisorMergeContributors.Resolve(new[] { Plan("s1"), Staging(SupervisorDecisionKinds.Spawn, done), Plan("s2"), Staging(SupervisorDecisionKinds.Spawn, running) });

        selection.AgentRunIds.ShouldBe(new[] { running.AgentRunId }, "an unsettled unit is not withheld — the generation owns its own in-flight wave");
        selection.CarriedOverFromEarlierGenerations.ShouldBe(0);
    }

    [Fact]
    public void A_resolvers_own_succeeded_branch_is_carried_over_with_the_contributors_it_reconciled()
    {
        // The staging-verb filter has to match the one the publish rung replaces (spawn|retry|RESOLVE). Filtering to
        // spawn/retry carried over precisely the stale halves a resolver had already reconciled, and dropped the
        // reconciliation itself.
        var a = Unit();
        var resolver = Unit();

        SupervisorMergeContributors.SettledAcrossGenerations(new[]
        {
            Plan("s1"), Staging(SupervisorDecisionKinds.Spawn, a), ConflictedMerge(a.AgentRunId), Staging(SupervisorDecisionKinds.Resolve, resolver), Plan("s2"),
        }).ShouldBe(new[] { a.AgentRunId, resolver.AgentRunId }, "every agent-STAGING verb settles work — a resolver's own branch is finished work too");
    }

    [Fact]
    public void A_replan_that_abandoned_the_earlier_direction_still_merges_it()
    {
        // ACCEPTED LIMITATION, pinned so a future change is deliberate (see SupervisorPlanPayload's class doc): a plan
        // carries no supersedes/discard signal, so the server cannot tell "re-planned because gen1 was the wrong
        // direction" from "re-planned after gen1 landed". Conservation wins — losing finished work is the strictly
        // worse failure — and the brain is told which way it goes by the recitation line, so it can spawn first.
        var abandoned = Unit();

        SupervisorMergeContributors.Resolve(new[] { Plan("wrong-direction"), Staging(SupervisorDecisionKinds.Spawn, abandoned), Plan("start-over") })
            .AgentRunIds.ShouldBe(new[] { abandoned.AgentRunId }, "a re-plan cannot DISCARD work — until a plan can say so explicitly, a merge that follows one folds what came before");
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
