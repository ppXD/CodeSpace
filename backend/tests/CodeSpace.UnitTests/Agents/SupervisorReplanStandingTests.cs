using System.Text.Json;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Supervisor;
using CodeSpace.Messages.Agents;
using Shouldly;

namespace CodeSpace.UnitTests.Agents;

/// <summary>
/// 🟢 Unit: the re-plan fixed point — whether a re-plan has ALREADY been spent on a unit without moving its
/// acceptance verdict, and where the unit must be sent instead. Pins the walk: a first verdict claims nothing; a
/// verdict still standing after a re-plan (nothing re-graded it, or a re-graded attempt returned the identical
/// detail) withdraws the plan verb; a CHANGED verdict gives it back; an amendable check names
/// <c>amend_acceptance</c> and a work-classed one names only the human; and a plan that opened no generation
/// re-planned nothing.
/// </summary>
[Trait("Category", "Unit")]
public class SupervisorReplanStandingTests
{
    private const string InfraDetail = "grade-error: npm not found";
    private const string WorkDetail = "tests-failed-exit-1";

    private static SupervisorPriorDecision Plan(long seq, SupervisorDecisionStatus status = SupervisorDecisionStatus.Succeeded, bool empty = false)
    {
        var payload = new SupervisorPlanPayload
        {
            Goal = "ship",
            Subtasks = empty ? Array.Empty<SupervisorPlannedSubtask>() : new[] { new SupervisorPlannedSubtask { Id = "s1", Title = "Audit", Instruction = "audit it" } },
        };

        return new SupervisorPriorDecision { Id = Guid.NewGuid(), Sequence = seq, Status = status, DecisionKind = SupervisorDecisionKinds.Plan, PayloadJson = JsonSerializer.Serialize(payload, AgentJson.Options), OutcomeJson = """{"planned":["s1"],"count":1}""" };
    }

    /// <summary>A staged attempt for 's1' whose grade is folded exactly the way the rehydrate fold folds it — so the walk reads the production shape, never a hand-written result array.</summary>
    private static SupervisorPriorDecision Staged(long seq, string decisionKind, bool? passed, string? detail, bool waived = false)
    {
        var agentId = Guid.NewGuid();
        var result = new SupervisorAgentResult
        {
            AgentRunId = agentId, Status = "Succeeded", Summary = "did it", ProducedBranch = "codespace/agent/s1",
            AcceptancePassed = passed, AcceptanceDetail = detail,
            AcceptanceVerdict = waived ? CodeSpace.Messages.Contracts.VerificationDisposition.Waived : null,
        };

        return new SupervisorPriorDecision
        {
            Id = Guid.NewGuid(), Sequence = seq, Status = SupervisorDecisionStatus.Succeeded, DecisionKind = decisionKind,
            PayloadJson = decisionKind == SupervisorDecisionKinds.Spawn ? """{"subtaskIds":["s1"]}""" : """{"subtaskId":"s1"}""",
            OutcomeJson = SupervisorOutcome.FoldAgentResults($$"""{"agentRunIds":["{{agentId}}"],"agentCount":1}""", new[] { result }),
        };
    }

    [Fact]
    public void A_first_verdict_has_survived_no_re_plan()
    {
        // The load-bearing negative: every tape whose verdict was graded UNDER the current plan must keep the
        // steer it always had, or the ramp is a global copy change dressed up as a fix.
        var tape = new[] { Plan(1), Staged(2, SupervisorDecisionKinds.Spawn, passed: false, InfraDetail) };

        SupervisorReplanStanding.VerdictSurvivedAReplan(tape, "s1").ShouldBeFalse("the plan predates the grade — no re-plan has been spent on this unit");
        SupervisorReplanStanding.ExitFor(tape, "s1").ShouldBe(SupervisorReplanExit.None);
    }

    [Fact]
    public void A_verdict_nothing_re_graded_after_a_re_plan_has_survived_it()
    {
        // THE observed attractor: plan → spawn → plan, and the rendered verdict row is literally the same row, so
        // its detail is identical by construction. Another plan is a move this run has already made here.
        var tape = new[] { Plan(1), Staged(2, SupervisorDecisionKinds.Spawn, passed: false, InfraDetail), Plan(3) };

        SupervisorReplanStanding.VerdictSurvivedAReplan(tape, "s1").ShouldBeTrue();
        SupervisorReplanStanding.ExitFor(tape, "s1").ShouldBe(SupervisorReplanExit.ToAmendment, "the check could not RUN, so the server's own amend gate admits a proposal for it");
    }

    [Theory]
    [InlineData(InfraDetail, true)]    // re-graded and came back saying exactly the same thing → the re-plan changed nothing
    [InlineData("grade-error: dotnet not found", false)]   // a DIFFERENT verdict → the re-plan did move something; the steer is honest again
    public void A_re_attempt_under_the_new_plan_decides_on_the_verdict_it_returned(string retriedDetail, bool survived)
    {
        var tape = new[]
        {
            Plan(1), Staged(2, SupervisorDecisionKinds.Spawn, passed: false, InfraDetail), Plan(3),
            Staged(4, SupervisorDecisionKinds.Retry, passed: false, retriedDetail),
        };

        SupervisorReplanStanding.VerdictSurvivedAReplan(tape, "s1").ShouldBe(survived,
            "the second leg of the rule is the DETAIL: a re-plan that produced a genuinely different verdict is not the fixed point, however many plans preceded it");
    }

    [Fact]
    public void A_work_classed_verdict_that_survived_a_re_plan_has_only_the_human_left()
    {
        // The measured-red-baseline arm's tape. The check RAN and rejected the work, so SupervisorAmendPrecondition
        // refuses a proposal — and the turn's roster withholds the verb on the same reading. A steer naming
        // 'amend_acceptance' here would be the two-rosters defect, one screen apart.
        var tape = new[] { Plan(1), Staged(2, SupervisorDecisionKinds.Spawn, passed: false, WorkDetail), Plan(3) };

        SupervisorReplanStanding.VerdictSurvivedAReplan(tape, "s1").ShouldBeTrue();
        SupervisorReplanStanding.ExitFor(tape, "s1").ShouldBe(SupervisorReplanExit.ToHuman);
        SupervisorAmendPrecondition.IsAmendable(tape, "s1").ShouldBeFalse("…and that is the server's own ruling, not this walk's opinion of it");
    }

    [Fact]
    public void Only_a_plan_that_opened_a_generation_counts_as_a_re_plan()
    {
        // A failed / empty / structurally-invalid plan re-planned NOTHING (SupervisorPlanWindow.IsValidBoundary is
        // the shared bar), so it must not be able to withdraw the plan verb from a unit no second plan ever covered.
        var graded = Staged(2, SupervisorDecisionKinds.Spawn, passed: false, InfraDetail);

        SupervisorReplanStanding.VerdictSurvivedAReplan(new[] { Plan(1), graded, Plan(3, SupervisorDecisionStatus.Failed) }, "s1")
            .ShouldBeFalse("a plan the server never recorded as succeeded opened no generation");
        SupervisorReplanStanding.VerdictSurvivedAReplan(new[] { Plan(1), graded, Plan(3, empty: true) }, "s1")
            .ShouldBeFalse("a plan declaring no subtasks opened no generation either");
        SupervisorReplanStanding.VerdictSurvivedAReplan(new[] { Plan(1), graded, Plan(3) }, "s1")
            .ShouldBeTrue("…and the valid one still does, or the two assertions above prove nothing");
    }

    [Fact]
    public void Nothing_but_a_standing_graded_failure_can_survive_a_re_plan()
    {
        SupervisorReplanStanding.ExitFor(new[] { Plan(1), Plan(3) }, "s1")
            .ShouldBe(SupervisorReplanExit.None, "a unit that was never attempted has no verdict a plan could fail to move");
        SupervisorReplanStanding.ExitFor(new[] { Plan(1), Staged(2, SupervisorDecisionKinds.Spawn, passed: true, "tests-passed"), Plan(3) }, "s1")
            .ShouldBe(SupervisorReplanExit.None, "a PASSED unit is not stranded — nothing is asking it to re-plan");
        SupervisorReplanStanding.ExitFor(new[] { Plan(1), Staged(2, SupervisorDecisionKinds.Spawn, passed: null, null), Plan(3) }, "s1")
            .ShouldBe(SupervisorReplanExit.None, "an ungraded pass-through carries no verdict at all");
        SupervisorReplanStanding.ExitFor(new[] { Plan(1), Staged(2, SupervisorDecisionKinds.Spawn, passed: false, InfraDetail, waived: true), Plan(3) }, "s1")
            .ShouldBe(SupervisorReplanExit.None, "a human WAIVED this unit's verification — WAIVED is not FAILED at any door (B2)");
        SupervisorReplanStanding.ExitFor(new[] { Plan(1), Staged(2, SupervisorDecisionKinds.Spawn, passed: false, InfraDetail), Plan(3) }, "s2")
            .ShouldBe(SupervisorReplanExit.None, "a subtask this tape never staged");
        SupervisorReplanStanding.ExitFor(new[] { Plan(1), Staged(2, SupervisorDecisionKinds.Spawn, passed: false, InfraDetail), Plan(3) }, null)
            .ShouldBe(SupervisorReplanExit.None);
    }

    [Fact]
    public void A_pending_co_sign_keeps_its_own_standing_rather_than_the_re_plan_exit()
    {
        // The two readings overlap on exactly one shape — a co-sign approved AFTER the newest plan, for a unit
        // graded BEFORE it — and the amend arms own their steers there. This pins the seam so a later edit cannot
        // let the ramp name 'amend_acceptance' on a unit whose gate refuses a second card.
        var tape = new[]
        {
            Plan(1), Staged(2, SupervisorDecisionKinds.Spawn, passed: false, InfraDetail), Plan(3),
            ApprovedCard(4, "s1"),
        };

        SupervisorAmendObligation.StandingFor(tape, "s1").ShouldBe(SupervisorAmendStanding.AwaitingRetry, "the card outlives the newest plan, and no staging has consumed it");
        SupervisorReplanStanding.VerdictSurvivedAReplan(tape, "s1").ShouldBeTrue("the verdict really did survive the re-plan — the tape fact is unchanged by the co-sign");
        SupervisorReplanStanding.ExitFor(tape, "s1").ShouldBe(SupervisorReplanExit.ToHuman,
            "…but the gate refuses a SECOND amendment while one is awaiting its retry, so the ramp must not name the verb the roster withholds");
    }

    /// <summary>An amend card the human APPROVED, built from the PRODUCTION card builder so the marker sentence and the structured proposal are exactly what the obligation walk and the co-sign overlay read back.</summary>
    private static SupervisorPriorDecision ApprovedCard(long sequence, string subtaskId)
    {
        var card = SupervisorAmendAcceptance.IntoAskHuman(new SupervisorAmendAcceptancePayload
        {
            SubtaskId = subtaskId, Reason = "the check shells out to tooling this repository never had",
            Acceptance = new SupervisorAcceptanceSpec { Command = new[] { "dotnet", "test" } },
        });

        return new SupervisorPriorDecision { Id = Guid.NewGuid(), Sequence = sequence, Status = SupervisorDecisionStatus.Succeeded, DecisionKind = SupervisorDecisionKinds.AskHuman, PayloadJson = card.PayloadJson, OutcomeJson = """{"question":"q","answer":"approve"}""" };
    }
}
