using CodeSpace.Core.Services.Workflows.Retention;
using CodeSpace.Messages.Retention;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows.Retention;

/// <summary>
/// The committed rule table and the one function that decides whether a durable record may be reclaimed. The windows
/// are asserted as literals on purpose: they are the only numbers in this plane whose reduction destroys data, so
/// shortening one has to be a deliberate edit here as well as in the policy.
///
/// <para>Every test below names the mutation it catches, because a retention decision that is wrong in the permissive
/// direction has no second chance — the bytes are gone.</para>
/// </summary>
[Trait("Category", "Unit")]
public sealed class DurableRetentionPolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 18, 12, 0, 0, TimeSpan.Zero);
    private static readonly DurableRetentionRule Rule = DurableRetentionPolicy.LogStream;

    [Fact]
    public void The_committed_rule_table_is_pinned_to_its_literal_windows()
    {
        var rule = DurableRetentionPolicy.For(DurableRecordClass.LogStream).ShouldNotBeNull();

        rule.MinimumAge.ShouldBe(TimeSpan.FromDays(30), "a log stream is not a candidate until a month after its capture settled");
        rule.QuarantineWindow.ShouldBe(TimeSpan.FromHours(24), "a second, independent day passes after the first uncited observation");
        rule.RecheckInterval.ShouldBe(TimeSpan.FromHours(24), "a stream that was looked at and kept is left alone this long, so one unreclaimable row cannot own a batch slot");
    }

    [Fact]
    public void Every_declared_class_has_a_rule_and_the_table_declares_nothing_else()
    {
        // The table advertises what is actually reclaimed. A class listed here without a cursor would read as a
        // promise the system does not keep; a cursor whose class is missing claims nothing at all. Either way the
        // right time to notice is here.
        DurableRetentionPolicy.Rules.Keys.Order().ShouldBe(Enum.GetValues<DurableRecordClass>().Order(),
            customMessage: "a class with no rule is never claimed, so adding one to the enum without a rule silently disables its plane");
        Enum.GetValues<DurableRecordClass>().ShouldBe([DurableRecordClass.LogStream],
            customMessage: "a class belongs here only together with the cursor that sweeps it — add both in one change, never the rule first");
    }

    [Fact]
    public void A_class_the_policy_does_not_register_has_no_rule_and_therefore_keeps()
    {
        // The reaper reads a null rule as "claim nothing"; the decision reads it as Indeterminate. Both mean keep,
        // which is what makes REMOVING a class from the table a safe operation rather than a purge.
        DurableRetentionPolicy.For((DurableRecordClass)9999).ShouldBeNull();
        Decide(null, Now.AddDays(-400), null, DurableReferenceVerdict.Unreferenced).Action.ShouldBe(DurableRetentionAction.Indeterminate);
    }

    [Fact]
    public void A_citation_clears_the_quarantine_it_contradicts()
    {
        // Mutation: carry the old retain_until through a Referenced settlement. A stream cited for a year would then
        // be collectable the instant its pin went away, with a quarantine "window" that elapsed while something still
        // pointed at it — a second wait that never actually waited for anything.
        var decision = Decide(Rule, Now.AddDays(-400), Now.AddDays(-100), DurableReferenceVerdict.Referenced);

        decision.RetainUntil.ShouldBeNull();
    }

    [Fact]
    public void An_unanswered_question_keeps_the_quarantine_it_never_contradicted()
    {
        // The opposite of the test above, and the reason the two are separate: an unreadable citation site says
        // nothing about the earlier uncited observation, so clearing the marker would restart a wait that was already
        // most of the way through.
        var quarantinedAt = Now.AddHours(-1);
        var decision = Decide(Rule, Now.AddDays(-400), quarantinedAt, DurableReferenceVerdict.Indeterminate);

        decision.RetainUntil.ShouldBe(quarantinedAt);
    }

    [Fact]
    public void Pinned_row_is_never_collected()
    {
        // Mutation: drop the pin check in the cursor (so a pinned record classifies Unreferenced) and this arm is the
        // only thing left between a sealed qualification result and the evidence it was computed from.
        var decision = Decide(Rule, Now.AddDays(-400), Now.AddDays(-100), DurableReferenceVerdict.Referenced);

        decision.Action.ShouldBe(DurableRetentionAction.Referenced, "a citation outranks every elapsed window, including a quarantine that is long past");
        decision.RetainUntil.ShouldBeNull();
    }

    [Fact]
    public void First_unreferenced_observation_only_quarantines()
    {
        // Mutation: collect on the first uncited observation. The record would then be removed by the very sweep that
        // first looked at it, with no durable window in which an operator or a late writer could intervene.
        var decision = Decide(Rule, Now.AddDays(-400), null, DurableReferenceVerdict.Unreferenced);

        decision.Action.ShouldBe(DurableRetentionAction.Quarantine);
        decision.RetainUntil.ShouldBe(Now.Add(Rule.QuarantineWindow));
    }

    [Fact]
    public void An_open_quarantine_window_waits_rather_than_collecting()
    {
        var decision = Decide(Rule, Now.AddDays(-400), Now.AddHours(1), DurableReferenceVerdict.Unreferenced);

        decision.Action.ShouldBe(DurableRetentionAction.Wait);
        decision.Code.ShouldBe("quarantine-window-open");
    }

    [Fact]
    public void Both_waits_elapsed_with_no_citation_is_the_only_path_to_collect()
    {
        var decision = Decide(Rule, Now.AddDays(-400), Now.AddSeconds(-1), DurableReferenceVerdict.Unreferenced);

        decision.Action.ShouldBe(DurableRetentionAction.Collect);
    }

    [Theory]
    [InlineData(DurableReferenceVerdict.Unreferenced)]
    [InlineData(DurableReferenceVerdict.Referenced)]
    [InlineData(DurableReferenceVerdict.Indeterminate)]
    public void Young_row_keeps_whatever_its_verdict(DurableReferenceVerdict verdict)
    {
        // Mutation: remove the age floor. A record whose citing write is still in flight — a seal mid-transaction, a
        // receipt about to be upserted — would be observed uncited and quarantined before its writer ever committed.
        var decision = Decide(Rule, Now.AddDays(-1), Now.AddDays(-100), verdict);

        decision.Action.ShouldBeOneOf(DurableRetentionAction.Wait, DurableRetentionAction.Referenced);
        decision.Action.ShouldNotBe(DurableRetentionAction.Collect);
    }

    [Fact]
    public void An_unanswered_citation_question_keeps_the_record()
    {
        // Mutation: treat Indeterminate as Unreferenced. "I could not tell" must never resolve to "delete".
        var decision = Decide(Rule, Now.AddDays(-400), Now.AddDays(-100), DurableReferenceVerdict.Indeterminate);

        decision.Action.ShouldBe(DurableRetentionAction.Indeterminate);
        decision.Code.ShouldBe("reference-status-indeterminate");
    }

    private static DurableRetentionDecision Decide(DurableRetentionRule? rule, DateTimeOffset terminalAt, DateTimeOffset? retainUntil, DurableReferenceVerdict verdict) =>
        DurableRetentionDecision.Decide(rule, new DurableRetentionObservation(terminalAt, retainUntil, verdict, Now));
}
