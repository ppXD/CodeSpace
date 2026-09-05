using System.Text.Json;
using CodeSpace.Core.Services.Sessions.Room;
using CodeSpace.Core.Services.Supervisor;
using CodeSpace.Messages.Agents;
using Shouldly;

namespace CodeSpace.UnitTests.Sessions.Room;

/// <summary>
/// A1 (result honesty) — the RESULT card's verdict. The Session Room used to read ONLY the stop's own classification,
/// so a run whose objective acceptance check FAILED still painted the green "Result" behind the model's own
/// success-sounding closing line (the engine's honest word lives on the run row, never on the card). These pin the
/// composition against the SAME authority the run row's <c>Outcome</c> is folded through, so the two cannot drift.
///
/// <para>Tier: Unit — <see cref="RoomProjector.ResultVerdict"/> is pure over the two durable stop-decision facts.</para>
/// </summary>
[Trait("Category", "Unit")]
public class RoomResultVerdictTests
{
    [Fact]
    public void A_stop_whose_acceptance_grade_FAILED_degrades_the_card_and_names_the_reason()
    {
        // The model stopped orderly and wrote a confident closing line; the objective check disagreed.
        var verdict = RoomProjector.ResultVerdict(acceptancePassed: false, Stop(SupervisorStopKind.Succeeded, summary: "Fixed the flaky tests."));

        verdict.Degraded.ShouldBeTrue("the work missed its own definition of done — a green Result would be a lie");
        verdict.Reason.ShouldBe("Checks failed", "the card states the ledger's verdict itself, because the answer TEXT is the model's success claim");
    }

    [Fact]
    public void A_stop_whose_acceptance_grade_PASSED_keeps_the_green_result()
    {
        var verdict = RoomProjector.ResultVerdict(acceptancePassed: true, Stop(SupervisorStopKind.Succeeded, summary: "Fixed the flaky tests."));

        verdict.Degraded.ShouldBeFalse();
        verdict.Reason.ShouldBeNull();
    }

    [Fact]
    public void An_UNGRADED_succeeded_stop_is_untouched_by_the_acceptance_axis()
    {
        // A run that staked no oracle at all: absence is not a verdict. Byte-identical to the pre-A1 projection.
        var verdict = RoomProjector.ResultVerdict(acceptancePassed: null, Stop(SupervisorStopKind.Succeeded, summary: "Answered inline."));

        verdict.Degraded.ShouldBeFalse("a null grade must never manufacture a degrade");
        verdict.Reason.ShouldBeNull();
    }

    [Theory]
    [InlineData(SupervisorStopKind.GaveUp)]
    [InlineData(SupervisorStopKind.Forced)]
    [InlineData(SupervisorStopKind.NeedsClarification)]
    public void An_UNGRADED_non_success_stop_stays_degraded_with_NO_reason_line(SupervisorStopKind kind)
    {
        // Regression guard: the classifier's own shapes are unchanged, and they need no extra reason line — the card's
        // TEXT already IS the classifier's account of why the run stopped short.
        var verdict = RoomProjector.ResultVerdict(acceptancePassed: null, Stop(kind, reason: "no progress"));

        verdict.Degraded.ShouldBeTrue();
        verdict.Reason.ShouldBeNull("a second, redundant account would only repeat the card's own text");
    }

    [Fact]
    public void A_FAILED_grade_outranks_the_stop_classification()
    {
        // Both axes fire. The grade is the objective one, so it owns the reason line.
        var verdict = RoomProjector.ResultVerdict(acceptancePassed: false, Stop(SupervisorStopKind.Forced, reason: "cost cap reached"));

        verdict.Degraded.ShouldBeTrue();
        verdict.Reason.ShouldBe("Checks failed");
    }

    /// <summary>
    /// The COUPLING pin: drive the whole stop-kind × grade cross-product from the RAW payload / outcome bytes a stop
    /// decision actually persists, and hold the card's verdict against <see cref="SupervisorOutcome.HonestOutcome"/> —
    /// the very function the engine folds into <c>WorkflowRun.Outcome</c>. If the two ever disagree, the room is lying
    /// about a run the ledger already judged.
    ///
    /// <para>Honest about its reach: today a naive <c>acceptancePassed is false</c> body ALSO satisfies this, because
    /// <c>HonestOutcomeOf</c> is extensionally equal to it right now — so this theory cannot, at present, tell the two
    /// apart (verified by mutation). What it does buy is the RELATIONSHIP: it goes red the moment the card's rule and
    /// the engine's rule diverge — a new degraded word, a widened success set, a waived-grade change on either side,
    /// or a card that starts reading absence as a verdict (that last one turns 4 of these cases red today).</para>
    /// </summary>
    [Theory]
    [InlineData("completed", null, null)]
    [InlineData("completed", null, true)]
    [InlineData("completed", null, false)]
    [InlineData("no-decision", null, null)]
    [InlineData("no-decision", null, true)]
    [InlineData("no-decision", null, false)]
    [InlineData("needs_clarification", null, null)]
    [InlineData("needs_clarification", null, true)]
    [InlineData("needs_clarification", null, false)]
    [InlineData(null, "no progress", null)]
    [InlineData(null, "no progress", true)]
    [InlineData(null, "no progress", false)]
    public void The_cards_verdict_never_disagrees_with_the_engines_own_honest_outcome(string? outcome, string? forcedReason, bool? grade)
    {
        var payloadJson = forcedReason is null ? "{}" : JsonSerializer.Serialize(new { reason = forcedReason });
        var outcomeJson = JsonSerializer.Serialize(new { stopped = true, outcome, summary = "The supervisor's closing line." });

        if (grade is { } passed)
            outcomeJson = SupervisorOutcome.AppendAcceptanceGrade(outcomeJson, passed, detail: "2 of 7 tests failed");

        // Read the two facts back off the bytes exactly as the projector does.
        var acceptancePassed = SupervisorOutcome.ReadAcceptanceGradePassed(outcomeJson);
        var stopClass = SupervisorOutcome.ClassifyStop(payloadJson, outcomeJson);

        acceptancePassed.ShouldBe(grade, "the fixture must round-trip through the real fold/read pair, or this theory proves nothing");

        var honestOutcome = SupervisorOutcome.HonestOutcome(payloadJson, outcomeJson);
        var acceptanceFailed = honestOutcome == SupervisorOutcome.AcceptanceFailedOutcome;

        var verdict = RoomProjector.ResultVerdict(acceptancePassed, stopClass);

        verdict.Degraded.ShouldBe(acceptanceFailed || stopClass.Degraded,
            customMessage: $"the card and the run row must agree: outcome={outcome ?? "<null>"} reason={forcedReason ?? "<null>"} grade={grade?.ToString() ?? "<null>"} → honest word '{honestOutcome}'");

        (verdict.Reason is not null).ShouldBe(acceptanceFailed,
            customMessage: $"a reason line is owed EXACTLY when the honest word is {SupervisorOutcome.AcceptanceFailedOutcome} (got '{honestOutcome}', reason '{verdict.Reason ?? "<null>"}')");
    }

    private static SupervisorStopClassification Stop(SupervisorStopKind kind, string? summary = null, string? reason = null) =>
        new() { Kind = kind, Summary = summary, Reason = reason };

    // ── C1: the UNVERIFIED marker ──

    [Fact]
    public void A_success_that_nothing_checked_is_marked_unverified()
    {
        // The most expensive silence in the room: a run with no operator floor, no model-authored oracle and no output
        // critic terminalizes a green "Result" that reads exactly like a fully-verified one.
        var verification = RoomProjector.Verification(graded: false, criticReviewed: false);

        verification.Verified.ShouldBe(false);
        verification.Note.ShouldBe("Unverified — no check ran on this result", "the copy is BACKEND-authored — the FE never maps a flag to words");
    }

    [Theory]
    [InlineData(true, false)]    // an acceptance grade (the stop's, or any unit's)
    [InlineData(false, true)]    // an output-critic verdict, including a silent approval
    [InlineData(true, true)]
    public void A_success_something_checked_carries_no_marker(bool graded, bool criticReviewed)
    {
        var verification = RoomProjector.Verification(graded, criticReviewed);

        verification.Verified.ShouldBe(true);
        verification.Note.ShouldBeNull("a verified card is byte-identical to before — the chip exists only for the unexamined one");
    }
}
