using CodeSpace.Core.Services.Sessions.Room;
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

    private static SupervisorStopClassification Stop(SupervisorStopKind kind, string? summary = null, string? reason = null) =>
        new() { Kind = kind, Summary = summary, Reason = reason };
}
