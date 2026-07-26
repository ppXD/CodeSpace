using CodeSpace.Core.Services.Supervisor;
using CodeSpace.Messages.Agents;
using Shouldly;

namespace CodeSpace.UnitTests.Supervisor;

/// <summary>
/// The ONE shared stop classifier both the RESULT card and the journal step read (<see cref="SupervisorOutcome.ClassifyStop"/>).
/// A model stop whose outcome is in the shared success set is a genuine SUCCESS; a non-success outcome is a model
/// GIVE-UP; a payload-<c>reason</c> stop with no outcome is a server-FORCED bound; a stop with neither signal is a
/// bare success (defensive, never a false alarm). The forced reason is read off the PAYLOAD via
/// <see cref="SupervisorOutcome.ReadStopReason"/>.
/// </summary>
[Trait("Category", "Unit")]
public class SupervisorOutcomeStopClassificationTests
{
    [Fact]
    public void A_success_outcome_classifies_as_a_genuine_success()
    {
        var c = SupervisorOutcome.ClassifyStop(payloadJson: "{}", outcomeJson: """{"stopped":true,"outcome":"completed","summary":"Shipped it."}""");

        c.Kind.ShouldBe(SupervisorStopKind.Succeeded);
        c.Degraded.ShouldBeFalse();
        c.DisplayText.ShouldBe("Shipped it.");
        c.Reason.ShouldBeNull();
    }

    [Fact]
    public void A_non_success_outcome_classifies_as_a_model_give_up()
    {
        var c = SupervisorOutcome.ClassifyStop(payloadJson: "{}", outcomeJson: $$"""{"stopped":true,"outcome":"{{SupervisorStopPayload.NonConformantOutcome}}","summary":"malformed reply"}""");

        c.Kind.ShouldBe(SupervisorStopKind.GaveUp);
        c.Degraded.ShouldBeTrue();
        c.DisplayText.ShouldBe("malformed reply", "the model's summary is the display line when present");
        c.Reason.ShouldBe(SupervisorStopPayload.NonConformantOutcome, "the non-success outcome label is the machine reason");
    }

    [Fact]
    public void A_give_up_with_no_summary_falls_back_to_the_outcome_label_for_display()
    {
        var c = SupervisorOutcome.ClassifyStop(payloadJson: "{}", outcomeJson: """{"stopped":true,"outcome":"no-model"}""");

        c.Kind.ShouldBe(SupervisorStopKind.GaveUp);
        c.DisplayText.ShouldBe("no-model", "no model summary → the reason is the display line");
    }

    [Fact]
    public void A_payload_reason_with_no_outcome_classifies_as_a_server_forced_stop()
    {
        // The reported gap: a budget/governance/bound-forced stop stamps {reason} on the PAYLOAD; ExecuteStop then
        // writes an outcome with a null outcome label — so there is no success outcome, only a reason.
        var c = SupervisorOutcome.ClassifyStop(payloadJson: $$"""{"reason":"{{SupervisorStopReasons.NoProgress}}"}""", outcomeJson: """{"stopped":true,"outcome":null,"summary":null}""");

        c.Kind.ShouldBe(SupervisorStopKind.Forced);
        c.Degraded.ShouldBeTrue();
        c.Reason.ShouldBe("no progress");
        c.DisplayText.ShouldBe("no progress", "a forced stop never renders a blank display line");
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("{}", null)]
    [InlineData("{}", "{}")]
    [InlineData("{}", "not json")]
    public void A_stop_with_no_outcome_and_no_reason_is_a_bare_success_never_a_false_alarm(string? payload, string? outcome)
    {
        var c = SupervisorOutcome.ClassifyStop(payload, outcome);

        c.Kind.ShouldBe(SupervisorStopKind.Succeeded);
        c.Degraded.ShouldBeFalse("an unclassifiable stop must not read as degraded");
    }

    [Theory]
    [InlineData("""{"reason":"no progress"}""", "no progress")]
    [InlineData("{}", null)]
    [InlineData(null, null)]
    [InlineData("not json", null)]
    [InlineData("""{"reason":42}""", null)]
    public void ReadStopReason_reads_the_forced_bound_off_the_payload(string? payloadJson, string? expected)
    {
        SupervisorOutcome.ReadStopReason(payloadJson).ShouldBe(expected);
    }

    [Theory]
    [InlineData("needs_clarification")]
    [InlineData("needs-clarification")]
    [InlineData("NEEDS_CLARIFICATION")]
    public void A_clarification_outcome_classifies_as_needs_clarification_with_the_question(string outcome)
    {
        // P5-1: the honest ask — never a success, never a give-up; the summary carries the question verbatim.
        var classification = SupervisorOutcome.ClassifyStop("{}", $$"""{"outcome":"{{outcome}}","summary":"Which auth provider should the login use?"}""");

        classification.Kind.ShouldBe(SupervisorStopKind.NeedsClarification);
        classification.Summary.ShouldBe("Which auth provider should the login use?");
    }

    [Fact]
    public void An_unknown_label_still_fail_closes_to_give_up_never_to_abstention()
    {
        SupervisorOutcome.ClassifyStop("{}", """{"outcome":"clarify-ish","summary":"?"}""")
            .Kind.ShouldBe(SupervisorStopKind.GaveUp, "the recognizer is exact — a fuzzy label can never buy the un-punished state");
    }

    // ── A1: HonestOutcome — the ONE terminal word both the node output and the run row derive from ──

    [Theory]
    [InlineData("""{"stopped":true,"outcome":"completed","summary":"Shipped."}""", "Succeeded")]
    [InlineData("""{"stopped":true,"outcome":"gave-up","summary":"stuck"}""", "GaveUp")]
    [InlineData("""{"stopped":true,"outcome":"needs-clarification","summary":"which repo?"}""", "NeedsClarification")]
    public void The_honest_outcome_reports_the_stop_kind(string outcomeJson, string expected)
    {
        SupervisorOutcome.HonestOutcome(payloadJson: "{}", outcomeJson).ShouldBe(expected);
    }

    [Fact]
    public void A_server_forced_stop_reports_Forced()
    {
        // No authored outcome + a payload reason = the server ended it, not the model.
        SupervisorOutcome.HonestOutcome("""{"reason":"no-progress"}""", """{"stopped":true}""").ShouldBe("Forced");
    }

    [Fact]
    public void A_failed_objective_grade_outranks_an_orderly_success_stop()
    {
        // The crown-jewel honesty case: the model stopped gracefully claiming completion, but the run's own
        // definition-of-done FAILED. The work missed its contract however gracefully the loop ended.
        var graded = """{"stopped":true,"outcome":"completed","summary":"Shipped.","acceptanceGrade":{"passed":false,"detail":"tests-failed-exit-1"}}""";

        SupervisorOutcome.HonestOutcome("{}", graded).ShouldBe(SupervisorOutcome.AcceptanceFailedOutcome);
    }

    [Fact]
    public void A_passing_grade_leaves_the_stop_kind_intact()
    {
        var graded = """{"stopped":true,"outcome":"completed","summary":"Shipped.","acceptanceGrade":{"passed":true,"detail":"tests-passed"}}""";

        SupervisorOutcome.HonestOutcome("{}", graded).ShouldBe("Succeeded");
    }

    [Fact]
    public void An_unclassifiable_stop_never_false_alarms_as_degraded()
    {
        // Defensive floor, matching ClassifyStop's own: neither signal present reads as a bare success rather than
        // inventing a degradation the tape never recorded.
        SupervisorOutcome.HonestOutcome("{}", "{}").ShouldBe("Succeeded");
        SupervisorOutcome.HonestOutcome(null, null).ShouldBe("Succeeded");
    }

    [Fact]
    public void The_classified_overload_agrees_with_the_tape_overload()
    {
        // The two overloads are ONE authority with two entry points — the node holds a folded result, the engine
        // holds raw tape bytes, and they must never drift into two vocabularies.
        var outcomeJson = """{"stopped":true,"outcome":"gave-up","summary":"stuck"}""";

        SupervisorOutcome.HonestOutcomeOf(acceptancePassed: null, SupervisorOutcome.ClassifyStop("{}", outcomeJson))
            .ShouldBe(SupervisorOutcome.HonestOutcome("{}", outcomeJson));

        SupervisorOutcome.HonestOutcomeOf(acceptancePassed: false, SupervisorOutcome.ClassifyStop("{}", outcomeJson))
            .ShouldBe(SupervisorOutcome.AcceptanceFailedOutcome, "a failed grade outranks the kind through either entry point");
    }

    [Fact]
    public void The_acceptance_failed_word_is_pinned()
    {
        // A durable column value AND a node output word — a rename is a data migration, not a refactor.
        SupervisorOutcome.AcceptanceFailedOutcome.ShouldBe("AcceptanceFailed");
    }
}
