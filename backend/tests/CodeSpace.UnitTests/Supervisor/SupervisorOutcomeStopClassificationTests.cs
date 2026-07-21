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
}
