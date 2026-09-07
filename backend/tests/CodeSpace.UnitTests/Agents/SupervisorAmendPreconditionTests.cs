using System.Text.Json;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Supervisor;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Contracts;
using Shouldly;

namespace CodeSpace.UnitTests.Agents;

/// <summary>
/// 🟢 Unit: B4's hard infra precondition (MAJOR-3) — an amend proposal reaches its co-sign card ONLY when the
/// target's latest SERVER verdict is an infra-classed failure (the check itself could not run). Everything else
/// rejects with a named reason before any card is posted: a never-attempted unit, an already-waived one, a passed
/// check, an ungraded attempt, and — the mark-its-own-homework channel — a check that RAN and rejected the work.
/// Also pins the raw-verdict card suffix (the co-signer rules on the server's evidence) and its tail cap.
/// </summary>
[Trait("Category", "Unit")]
public class SupervisorAmendPreconditionTests
{
    private static SupervisorAmendAcceptancePayload Amend(string id = "s1") => new() { SubtaskId = id, Waive = true, Reason = "r" };

    private static SupervisorTurnContext Context(params SupervisorAgentResult[] units)
    {
        if (units.Length == 0) return new SupervisorTurnContext { Goal = "g", PriorDecisions = Array.Empty<SupervisorPriorDecision>() };

        var payload = JsonSerializer.Serialize(new { subtaskIds = units.Select((_, i) => $"s{i + 1}").ToArray() }, AgentJson.Options);
        var outcome = SupervisorOutcome.FoldAgentResults(
            JsonSerializer.Serialize(new { agentRunIds = units.Select(u => u.AgentRunId).ToArray(), agentCount = units.Length }, AgentJson.Options), units);

        var spawn = new SupervisorPriorDecision { Id = Guid.NewGuid(), Sequence = 1, Status = SupervisorDecisionStatus.Succeeded, DecisionKind = SupervisorDecisionKinds.Spawn, PayloadJson = payload, OutcomeJson = outcome };

        return new SupervisorTurnContext { Goal = "g", PriorDecisions = new[] { spawn } };
    }

    private static SupervisorAgentResult Unit(bool? passed = null, string? detail = null, VerificationDisposition? verdict = null, string? branch = "codespace/agent/s1") =>
        new() { AgentRunId = Guid.NewGuid(), Status = "Succeeded", ProducedBranch = branch, AcceptancePassed = passed, AcceptanceDetail = detail, AcceptanceVerdict = verdict };

    [Fact]
    public void A_never_attempted_subtask_rejects()
    {
        SupervisorAmendPrecondition.Reject(Context(), Amend()).ShouldNotBeNull().ShouldContain("never been attempted");
    }

    [Fact]
    public void An_already_waived_subtask_rejects()
    {
        SupervisorAmendPrecondition.Reject(Context(Unit(verdict: VerificationDisposition.Waived)), Amend())
            .ShouldNotBeNull().ShouldContain("already WAIVED");
    }

    [Fact]
    public void A_passed_check_rejects()
    {
        SupervisorAmendPrecondition.Reject(Context(Unit(passed: true, detail: "tests-passed")), Amend())
            .ShouldNotBeNull().ShouldContain("PASSED");
    }

    [Fact]
    public void An_ungraded_attempt_rejects()
    {
        SupervisorAmendPrecondition.Reject(Context(Unit()), Amend())
            .ShouldNotBeNull().ShouldContain("never graded");
    }

    [Fact]
    public void A_work_classed_failure_rejects_naming_the_channel()
    {
        // The mark-its-own-homework channel: the check RAN and rejected the work — the model must fix the work,
        // never amend the judge away. The SERVER's verdict decides, not the model's framing of it.
        var rejection = SupervisorAmendPrecondition.Reject(Context(Unit(passed: false, detail: "tests-failed-exit-1")), Amend());

        rejection.ShouldNotBeNull();
        rejection.ShouldContain("evidence against the WORK");
        rejection.ShouldContain("tests-failed-exit-1", customMessage: "the rejection quotes the verdict so the next turn's decider sees exactly why");
    }

    [Theory]
    [InlineData("grade-error: npm: command not found")]   // the grader's process start failed — the oracle names missing tooling
    [InlineData("no-rubric")]                             // spec-incomplete — the oracle itself is half-authored
    [InlineData("clone-failed: auth")]                    // environment
    public void An_infra_classed_failure_is_amendable(string detail)
    {
        SupervisorAmendPrecondition.Reject(Context(Unit(passed: false, detail: detail)), Amend())
            .ShouldBeNull("the check itself could not run — exactly the broken-oracle evidence the amend verb exists for");
    }

    [Fact]
    public void A_second_amend_while_one_awaits_its_retry_rejects()
    {
        // B6 (the re-enactment arm's live finding): the target's latest verdict is still the dead oracle's failure
        // — which passes the infra arm and let a live brain re-amend the same subtask five times without ever
        // retrying. One signed repair at a time.
        var context = Context(Unit(passed: false, detail: "grade-error: npm: command not found"));
        var approvedCard = ApprovedAmendCard(sequence: 2);
        context = context with { PriorDecisions = context.PriorDecisions.Append(approvedCard).ToList() };

        SupervisorAmendPrecondition.Reject(context, Amend())
            .ShouldNotBeNull().ShouldContain("already carries an approved amendment");
    }

    [Fact]
    public void A_retry_that_consumed_the_amendment_reopens_the_ordinary_arms()
    {
        var context = Context(Unit(passed: false, detail: "grade-error: npm: command not found"));
        var retried = Unit(passed: false, detail: "grade-error: still broken");
        var retry = new SupervisorPriorDecision
        {
            Id = Guid.NewGuid(), Sequence = 3, Status = SupervisorDecisionStatus.Succeeded, DecisionKind = SupervisorDecisionKinds.Retry,
            PayloadJson = """{"subtaskId":"s1"}""", OutcomeJson = Outcome(retried),
        };
        context = context with { PriorDecisions = context.PriorDecisions.Append(ApprovedAmendCard(sequence: 2)).Append(retry).ToList() };

        SupervisorAmendPrecondition.Reject(context, Amend())
            .ShouldBeNull("the retry consumed the prior amendment and re-graded infra-classed — a fresh proposal is legitimate again");
    }

    private static SupervisorPriorDecision ApprovedAmendCard(long sequence)
    {
        var card = SupervisorAmendAcceptance.IntoAskHuman(new SupervisorAmendAcceptancePayload
        {
            SubtaskId = "s1", Reason = "r", Acceptance = new SupervisorAcceptanceSpec { Command = new[] { "sh", "check.sh" } },
        });

        return new SupervisorPriorDecision { Id = Guid.NewGuid(), Sequence = sequence, Status = SupervisorDecisionStatus.Succeeded, DecisionKind = SupervisorDecisionKinds.AskHuman, PayloadJson = card.PayloadJson, OutcomeJson = """{"question":"q","answer":"approve"}""" };
    }

    [Fact]
    public void The_latest_attempt_rules_a_retried_unit()
    {
        // The original failed work-classed; a retry re-graded infra-classed — the LATEST verdict decides (the same
        // last-wins walk the dependency gate shares, so the two can never disagree on which attempt speaks).
        var original = Unit(passed: false, detail: "tests-failed-exit-1");
        var retried = Unit(passed: false, detail: "grade-error: setup crashed");

        var spawnPayload = JsonSerializer.Serialize(new { subtaskIds = new[] { "s1" } }, AgentJson.Options);
        var retryPayload = JsonSerializer.Serialize(new { subtaskId = "s1" }, AgentJson.Options);

        var context = new SupervisorTurnContext
        {
            Goal = "g",
            PriorDecisions = new[]
            {
                new SupervisorPriorDecision { Id = Guid.NewGuid(), Sequence = 1, Status = SupervisorDecisionStatus.Succeeded, DecisionKind = SupervisorDecisionKinds.Spawn, PayloadJson = spawnPayload, OutcomeJson = Outcome(original) },
                new SupervisorPriorDecision { Id = Guid.NewGuid(), Sequence = 2, Status = SupervisorDecisionStatus.Succeeded, DecisionKind = SupervisorDecisionKinds.Retry, PayloadJson = retryPayload, OutcomeJson = Outcome(retried) },
            },
        };

        SupervisorAmendPrecondition.Reject(context, Amend()).ShouldBeNull();
    }

    // ── the evidence SCOPE: a re-plan may supersede an instruction, never the grade an amendment answers ──

    [Fact]
    public void A_replan_does_not_hide_the_infra_graded_attempt_the_amendment_answers()
    {
        // The window-vs-tape split this gate shipped with: SupervisorPlanWindow closes over the spawn that produced
        // the grade, so the arms below answered "never been attempted" while SupervisorAmendObligation.StandingFor,
        // the decider's Discarded steer and the turn roster all read the whole tape. Golden
        // 'amended-oracle-discarded-by-replan' is that contradiction rendered into one prompt: steered at the verb,
        // one screen under a menu reporting it unavailable (decision eval 34085079257, 24/25 — the model answered
        // with a third verb). Re-anchoring a repaired check to the NEW plan is exactly what the verb is for, so the
        // pre-re-plan attempt is the evidence, not stale.
        var context = RePlannedOver(Context(Unit(passed: false, detail: "grade-error: npm: command not found")));

        SupervisorPlanWindow.Read(context.PriorDecisions).Decisions.Count
            .ShouldBe(1, "the re-plan must really be the window boundary, or this scenario measures nothing it claims to");

        SupervisorAmendPrecondition.Reject(context, Amend())
            .ShouldBeNull("the check still cannot run and no co-sign is in force — a re-plan discarded the repair, it did not un-grade the attempt");
        SupervisorAmendPrecondition.AnyAmendableUnit(context)
            .ShouldBeTrue("…so the roster must OFFER the verb its own steer sends the model at");
    }

    [Fact]
    public void A_unit_with_no_graded_attempt_anywhere_on_the_tape_is_still_refused()
    {
        // Widening the SCOPE of the evidence read must not widen the SET of admissible targets to "anything the
        // model names": an id with no graded attempt on the whole tape has no warrant, re-plan or no re-plan.
        var attemptedS1 = RePlannedOver(Context(Unit(passed: false, detail: "grade-error: npm: command not found")));

        SupervisorAmendPrecondition.Reject(attemptedS1, Amend("s2"))
            .ShouldNotBeNull("'s2' was never attempted — the tape carries a grade for 's1' and nothing else")
            .ShouldContain("never been attempted");

        var nothingAttempted = new SupervisorTurnContext { Goal = "g", PriorDecisions = new[] { RePlan(1) } };

        SupervisorAmendPrecondition.Reject(nothingAttempted, Amend()).ShouldNotBeNull().ShouldContain("never been attempted");
        SupervisorAmendPrecondition.AnyAmendableUnit(nothingAttempted).ShouldBeFalse("a planned-but-unattempted run has nothing to amend, so the roster must withhold the verb");
    }

    [Fact]
    public void A_work_classed_failure_a_replan_closed_over_is_refused_on_the_work_channel()
    {
        // The pair that proves the widening is a SCOPE change and not an amnesty: the same re-plan that stops hiding
        // an infra grade must not launder a work-classed one into "never been attempted" either — the model is sent
        // back to the work, with the verdict quoted, exactly as it is on an unwindowed tape.
        var context = RePlannedOver(Context(Unit(passed: false, detail: "tests-failed-exit-1")));

        SupervisorAmendPrecondition.Reject(context, Amend())
            .ShouldNotBeNull().ShouldContain("evidence against the WORK", customMessage: "a re-plan is not a route around the mark-its-own-homework gate");
        SupervisorAmendPrecondition.AnyAmendableUnit(context).ShouldBeFalse();
    }

    /// <summary>The same tape with a valid re-plan appended LAST — the plan-generation boundary that closes <see cref="SupervisorPlanWindow"/> over every attempt before it.</summary>
    private static SupervisorTurnContext RePlannedOver(SupervisorTurnContext context) =>
        context with { PriorDecisions = context.PriorDecisions.Append(RePlan(context.PriorDecisions.Count + 1)).ToList() };

    /// <summary>A structurally VALID plan — <see cref="SupervisorPlanWindow.IsValidBoundary"/> ignores an empty, malformed or failed one, so a fixture that skimped here would open no generation and pin nothing.</summary>
    private static SupervisorPriorDecision RePlan(long sequence) => new()
    {
        Id = Guid.NewGuid(), Sequence = sequence, Status = SupervisorDecisionStatus.Succeeded, DecisionKind = SupervisorDecisionKinds.Plan,
        PayloadJson = """{"goal":"g","subtasks":[{"id":"s1","title":"S1","instruction":"re-author the s1 check"}]}""",
        OutcomeJson = """{"planned":["s1"],"count":1}""",
    };

    // ── the raw-verdict card suffix ────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_card_suffix_quotes_the_verdict_and_caps_the_tail()
    {
        var longTail = new string('x', 600);
        var unit = Unit(passed: false, detail: "grade-error: boom") with { AcceptanceEvidenceTail = longTail };

        var suffix = SupervisorAmendPrecondition.RawVerdictSuffix(Context(unit), "s1")!;

        suffix.ShouldContain("grade-error: boom");
        suffix.Length.ShouldBeLessThan(600, "the card shows the diagnosis headline, not the whole log");
        suffix.ShouldContain("…");
    }

    [Fact]
    public void A_subtask_with_no_verdict_detail_adds_no_suffix()
    {
        SupervisorAmendPrecondition.RawVerdictSuffix(Context(Unit()), "s1").ShouldBeNull();
        SupervisorAmendPrecondition.RawVerdictSuffix(Context(), "s1").ShouldBeNull();
    }

    private static string Outcome(SupervisorAgentResult unit) =>
        SupervisorOutcome.FoldAgentResults(
            JsonSerializer.Serialize(new { agentRunIds = new[] { unit.AgentRunId }, agentCount = 1 }, AgentJson.Options), new[] { unit });
}
