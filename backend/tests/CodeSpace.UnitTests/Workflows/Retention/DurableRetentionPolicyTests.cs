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

    [Theory]
    [InlineData(DurableRecordClass.LogStream, 30)]
    [InlineData(DurableRecordClass.CleanupReceipt, 30)]
    [InlineData(DurableRecordClass.CaptureGap, 30)]
    [InlineData(DurableRecordClass.QualificationEvidence, 180)]
    [InlineData(DurableRecordClass.BudgetReservation, 90)]
    [InlineData(DurableRecordClass.TransferIntent, 7)]
    public void The_committed_rule_table_is_pinned_to_its_literal_windows(DurableRecordClass value, int minimumAgeDays)
    {
        var rule = DurableRetentionPolicy.For(value).ShouldNotBeNull();

        rule.MinimumAge.ShouldBe(TimeSpan.FromDays(minimumAgeDays));
        rule.QuarantineWindow.ShouldBe(TimeSpan.FromHours(24), "every class waits a second, independent day after the first uncited observation");
    }

    [Fact]
    public void Every_declared_class_has_a_rule_and_the_table_declares_nothing_else()
    {
        DurableRetentionPolicy.Rules.Keys.Order().ShouldBe(Enum.GetValues<DurableRecordClass>().Order(),
            customMessage: "a class with no rule is never claimed, so adding one to the enum without a rule silently disables its plane");
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
