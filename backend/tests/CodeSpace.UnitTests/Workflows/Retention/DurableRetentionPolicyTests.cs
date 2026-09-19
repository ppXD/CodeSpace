using CodeSpace.Core.Services.Workflows.Retention;
using CodeSpace.Core.Handlers.QueryHandlers.Agents;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents.AgentRunLogging;
using CodeSpace.Messages.Artifacts;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Dtos.Agents;
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
    public void The_committed_rule_table_is_pinned_to_its_literal_windows(DurableRecordClass value, int minimumAgeDays)
    {
        var rule = DurableRetentionPolicy.For(value).ShouldNotBeNull();

        rule.MinimumAge.ShouldBe(TimeSpan.FromDays(minimumAgeDays), "the age floor is measured from the record's own terminal instant");
        rule.QuarantineWindow.ShouldBe(TimeSpan.FromHours(24), "a second, independent day passes after the first uncited observation");
        rule.RecheckInterval.ShouldBe(TimeSpan.FromHours(24), "a record that was looked at and kept is left alone this long, so one unreclaimable row cannot own a batch slot");
    }

    [Fact]
    public void A_purged_read_puts_its_availability_and_code_on_the_wire_verbatim()
    {
        var read = AgentRunLogWire.Unavailable(Metadata(), 0, new AgentRunLogProblem(AgentRunLogProblemCode.Purged));

        read.Availability.ShouldBe(AgentRunLogReadAvailability.Purged);
        read.Availability.ToString().ShouldBe("Purged");
        read.ProblemCode.ShouldBe("Purged", "the controller sends ProblemCode as the response's `code`, so this literal IS the web client's contract");
        read.IsRetryable.ShouldBeFalse("bytes reclaimed on purpose never come back, so no caller should retry the read");
    }

    private static AgentRunLogMetadata Metadata() => new(Guid.NewGuid(), Guid.NewGuid(), "stdout/v1", "text/plain", "utf-8", "spool/v1",
        ArtifactRetention.Run, AgentRunLogStreamState.Completed, 2, 1, 3, 3, null, Now, Now, Now, null);

    /// <summary>
    /// Who a reclamation is attributed to. The seeder is the established identity for background work — Hangfire
    /// workers, scheduled jobs and DbUp all write under it (<c>SystemUsers.cs</c>), and it holds a real seeded row, so
    /// a purge receipt attributes to something that exists. A dedicated retention actor would be more specific, but it
    /// would need its own seeded user and migration to be more HONEST; until then the choice is pinned here so
    /// changing it is a decision rather than a diff.
    /// </summary>
    [Fact]
    public void A_reclamation_is_attributed_to_the_system_background_actor()
    {
        SystemUsers.SeederId.ShouldBe(Guid.Parse("00000000-0000-0000-0000-000000000001"));
        SystemUsers.SeederName.ShouldBe("System");
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
