using System.Text.Json;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Supervisor;
using CodeSpace.Messages.Agents;
using Shouldly;

namespace CodeSpace.UnitTests.Agents;

/// <summary>
/// 🟢 Unit: what a plan authored over a unit's verdict has actually accomplished, and where the unit must be sent
/// instead of at another one. Pins the walk and — load-bearing — the SPLIT between its two arms: a re-plan nothing
/// has re-graded is UNRUN (stage it), only a re-plan that came back with the identical verdict is SPENT (amend it
/// or ask), a changed verdict gives the plan verb back, and a unit the newest plan dropped has only the human left.
/// </summary>
[Trait("Category", "Unit")]
public class SupervisorReplanStandingTests
{
    private const string InfraDetail = "grade-error: npm not found";
    private const string WorkDetail = "tests-failed-exit-1";

    private static SupervisorPriorDecision Plan(long seq, SupervisorDecisionStatus status = SupervisorDecisionStatus.Succeeded, bool empty = false, string[]? subtaskIds = null)
    {
        var declared = subtaskIds ?? new[] { "s1" };
        var payload = new SupervisorPlanPayload
        {
            Goal = "ship",
            Subtasks = empty ? Array.Empty<SupervisorPlannedSubtask>() : declared.Select(id => new SupervisorPlannedSubtask { Id = id, Title = "Audit", Instruction = "audit it" }).ToArray(),
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
        SupervisorReplanStanding.AwaitsItsReplannedStaging(tape, "s1").ShouldBeFalse("…and none is authored-but-unrun over it either");
        SupervisorReplanStanding.ExitFor(tape, "s1").ShouldBe(SupervisorReplanExit.None);
    }

    /// <summary>
    /// THE arm split, and the reason this file was rewritten: <c>plan → spawn → plan</c> is the tape one turn AFTER
    /// the model obeyed "re-plan this item with a check its agent can satisfy". Nothing has re-graded the unit, so
    /// nothing witnesses that the new plan changed nothing — the re-plan is UNRUN, and the honest move is to stage
    /// it. The first cut read this as "the verdict survived a re-plan" (the standing row matched ITSELF in the
    /// detail comparison, collapsing the rule to "∃ a valid plan boundary after this verdict"), told the model not
    /// to plan and not to retry, and named neither <c>spawn</c> nor <c>retry</c> — a second attractor in the shape
    /// of a fix for the first.
    /// </summary>
    [Fact]
    public void A_re_plan_nothing_has_re_graded_is_unrun_rather_than_spent()
    {
        var tape = new[] { Plan(1), Staged(2, SupervisorDecisionKinds.Spawn, passed: false, InfraDetail), Plan(3) };

        SupervisorReplanStanding.AwaitsItsReplannedStaging(tape, "s1").ShouldBeTrue("a plan for it is authored and has never been run");
        SupervisorReplanStanding.VerdictSurvivedAReplan(tape, "s1").ShouldBeFalse(
            "nothing re-graded the unit, so nothing on this tape says the new plan left the verdict where it found it");
        SupervisorReplanStanding.ExitFor(tape, "s1").ShouldBe(SupervisorReplanExit.ToStaging, "the exit is the staging the plan is waiting for, not an amendment");
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
        SupervisorReplanStanding.AwaitsItsReplannedStaging(tape, "s1").ShouldBeFalse("either way the plan HAS been run — this arm is about an unrun one");
        SupervisorReplanStanding.ExitFor(tape, "s1").ShouldBe(survived ? SupervisorReplanExit.ToAmendment : SupervisorReplanExit.None,
            "the check could not RUN, so where the verdict really did survive, the server's own amend gate admits a proposal for it");
    }

    [Fact]
    public void A_work_classed_verdict_that_survived_a_re_plan_has_only_the_human_left()
    {
        // The measured-red-baseline arm's tape. The check RAN and rejected the work, so SupervisorAmendPrecondition
        // refuses a proposal — and the turn's roster withholds the verb on the same reading. A steer naming
        // 'amend_acceptance' here would be the two-rosters defect, one screen apart.
        var tape = new[]
        {
            Plan(1), Staged(2, SupervisorDecisionKinds.Spawn, passed: false, WorkDetail), Plan(3),
            Staged(4, SupervisorDecisionKinds.Retry, passed: false, WorkDetail),
        };

        SupervisorReplanStanding.VerdictSurvivedAReplan(tape, "s1").ShouldBeTrue();
        SupervisorReplanStanding.ExitFor(tape, "s1").ShouldBe(SupervisorReplanExit.ToHuman);
        SupervisorAmendPrecondition.IsAmendable(tape, "s1").ShouldBeFalse("…and that is the server's own ruling, not this walk's opinion of it");
    }

    /// <summary>
    /// The plan-MEMBERSHIP conjunct: a unit the newest generation no longer declares has neither live exit. A spawn
    /// cannot stage a unit the plan does not declare, and an amendment co-signed for it mints a human ruling no
    /// retry can ever consume — the residual <c>SupervisorAmendPrecondition.GradedAttempts</c> names and declines to
    /// guard, because the gate reads the whole tape on purpose. Swept over BOTH arms, since either could otherwise
    /// spend a co-sign or name a spawn on a dropped unit.
    /// </summary>
    [Fact]
    public void A_unit_the_newest_plan_no_longer_declares_is_sent_only_to_a_human()
    {
        var dropped = Plan(3, subtaskIds: new[] { "s2" });

        SupervisorReplanStanding.ExitFor(new[] { Plan(1), Staged(2, SupervisorDecisionKinds.Spawn, passed: false, InfraDetail), dropped }, "s1")
            .ShouldBe(SupervisorReplanExit.ToHuman, "there is no spawn that can stage a unit the plan dropped");
        SupervisorReplanStanding.ExitFor(new[]
        {
            Plan(1), Staged(2, SupervisorDecisionKinds.Spawn, passed: false, InfraDetail), dropped,
            Staged(4, SupervisorDecisionKinds.Retry, passed: false, InfraDetail),
        }, "s1").ShouldBe(SupervisorReplanExit.ToHuman, "…and no amendment for it either, however amendable its verdict reads");

        SupervisorReplanStanding.ExitFor(new[] { Plan(1), Staged(2, SupervisorDecisionKinds.Spawn, passed: false, InfraDetail), Plan(3, subtaskIds: new[] { "s1", "s2" }) }, "s1")
            .ShouldBe(SupervisorReplanExit.ToStaging, "…and a plan that still declares it keeps the staging exit, or the assertions above prove nothing");
    }

    [Fact]
    public void Only_a_plan_that_opened_a_generation_counts_as_a_re_plan()
    {
        // A failed / empty / structurally-invalid plan re-planned NOTHING (SupervisorPlanWindow.IsValidBoundary is
        // the shared bar), so it must not be able to withdraw the plan verb from a unit no second plan ever covered.
        var graded = Staged(2, SupervisorDecisionKinds.Spawn, passed: false, InfraDetail);

        SupervisorReplanStanding.ExitFor(new[] { Plan(1), graded, Plan(3, SupervisorDecisionStatus.Failed) }, "s1")
            .ShouldBe(SupervisorReplanExit.None, "a plan the server never recorded as succeeded opened no generation");
        SupervisorReplanStanding.ExitFor(new[] { Plan(1), graded, Plan(3, empty: true) }, "s1")
            .ShouldBe(SupervisorReplanExit.None, "a plan declaring no subtasks opened no generation either");
        SupervisorReplanStanding.ExitFor(new[] { Plan(1), graded, Plan(3) }, "s1")
            .ShouldBe(SupervisorReplanExit.ToStaging, "…and the valid one still does, or the two assertions above prove nothing");
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

    /// <summary>
    /// The batch entry point the prompt build reads — the SAME answers off one walk of the tape, or the hoist that
    /// stopped the per-row re-walk quietly changed the prompt while making it cheaper. Includes the None units, so a
    /// caller can tell "no exit" from "not on this tape" without asking again.
    /// </summary>
    [Fact]
    public void The_batch_walk_answers_exactly_what_the_per_unit_walk_does()
    {
        var tape = new[]
        {
            Plan(1, subtaskIds: new[] { "s1", "s2" }),
            Staged(2, SupervisorDecisionKinds.Spawn, passed: false, InfraDetail),
            Plan(3, subtaskIds: new[] { "s1", "s2" }),
            Staged(4, SupervisorDecisionKinds.Retry, passed: false, InfraDetail),
        };

        var exits = SupervisorReplanStanding.ExitsFor(tape);

        exits.Keys.ShouldBe(new[] { "s1" }, ignoreOrder: true, customMessage: "every unit the tape folded a result for, and nothing it did not");
        exits["s1"].ShouldBe(SupervisorReplanStanding.ExitFor(tape, "s1"));
        SupervisorReplanStanding.ExitsFor(Array.Empty<SupervisorPriorDecision>()).ShouldBeEmpty("an empty tape stages nothing");
    }

    [Fact]
    public void A_pending_co_sign_keeps_its_own_standing_rather_than_the_re_plan_exit()
    {
        // The two readings overlap on exactly one shape — a co-sign approved AFTER the newest plan, for a unit
        // graded BEFORE it — and the amend arms own their steers there: every render site reads the amend standing
        // FIRST, and AwaitingRetry names the retry that consumes the co-sign. This pins the seam, because the exit
        // below generalises that ("stage it") rather than contradicting it, which is what makes the precedence safe
        // rather than lucky.
        var tape = new[]
        {
            Plan(1), Staged(2, SupervisorDecisionKinds.Spawn, passed: false, InfraDetail), Plan(3),
            ApprovedCard(4, "s1"),
        };

        SupervisorAmendObligation.StandingFor(tape, "s1").ShouldBe(SupervisorAmendStanding.AwaitingRetry, "the card outlives the newest plan, and no staging has consumed it");
        SupervisorReplanStanding.ExitFor(tape, "s1").ShouldBe(SupervisorReplanExit.ToStaging,
            "the tape fact is unchanged by the co-sign — a plan for this unit is authored and unrun, and staging it is exactly what the AwaitingRetry steer asks for by name");
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
