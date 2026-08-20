using CodeSpace.Core.Services.Workflows.Artifacts.Retention;
using CodeSpace.Messages.Artifacts;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows.Artifacts;

/// <summary>
/// Counter-example tests for the decision that is allowed to delete data. Each one drives
/// <see cref="ArtifactRetentionDecision.Decide"/> with inputs that are ONE step away from a collection and asserts the
/// answer is a keep — so removing any single guard in that function turns one of these red.
/// </summary>
[Trait("Category", "Unit")]
public sealed class ArtifactRetentionDecisionTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 19, 12, 0, 0, TimeSpan.Zero);
    private static readonly ArtifactRetentionRule Rule = new(ArtifactRetentionClass.ArtifactManifestContent, TimeSpan.FromDays(7), TimeSpan.FromHours(24));

    [Fact]
    public void A_quarantined_unreferenced_inline_artifact_past_both_waits_is_the_only_input_that_collects()
    {
        // The positive control. Every other test below is this exact input with ONE field made unsafe.
        ArtifactRetentionDecision.Decide(Rule, Collectable()).Action.ShouldBe(ArtifactRetentionAction.Collect);
    }

    [Fact]
    public void A_referenced_artifact_is_never_collected_however_old_it_is()
    {
        var decision = ArtifactRetentionDecision.Decide(Rule, Collectable() with { Verdict = ArtifactReferenceVerdict.Referenced, ArtifactCreatedAt = Now.AddYears(-5) });

        decision.Action.ShouldBe(ArtifactRetentionAction.Referenced, "a live reference outranks every age and every window");
    }

    [Fact]
    public void An_indeterminate_reference_status_keeps_the_artifact_rather_than_collecting_it()
    {
        // Fail-closed: "I could not tell" must never resolve to "delete". A Retry keeps the row live and the artifact
        // intact; the reaper's own attempt budget later turns a permanent Retry into a terminal keep.
        var decision = ArtifactRetentionDecision.Decide(Rule, Collectable() with { Verdict = ArtifactReferenceVerdict.Indeterminate });

        decision.Action.ShouldBe(ArtifactRetentionAction.Retry);
        decision.ErrorCode.ShouldBe("reference-status-indeterminate");
    }

    [Fact]
    public void An_unregistered_retention_class_keeps_the_artifact()
    {
        // A rule removed from ArtifactRetentionPolicy must not turn its existing declarations into deletions.
        var decision = ArtifactRetentionDecision.Decide(null, Collectable());

        decision.Action.ShouldBe(ArtifactRetentionAction.Indeterminate);
        decision.ErrorCode.ShouldBe("retention-class-unregistered");
    }

    [Theory]
    [InlineData(ArtifactPurgePath.LocalBlobShared, "artifact-blob-shared")]              // another row names the same physical file
    [InlineData(ArtifactPurgePath.Routed, "artifact-routed-storage")]                    // a committed transfer intent can hand the object to a later writer
    [InlineData(ArtifactPurgePath.BackendCannotPurge, "artifact-blob-backend-cannot-purge")]   // the transport offers no removal at all
    public void An_artifact_whose_bytes_have_no_purge_path_is_kept_and_says_which_one_is_missing(ArtifactPurgePath purge, string expectedCode)
    {
        var decision = ArtifactRetentionDecision.Decide(Rule, Collectable() with { Purge = purge });

        decision.Action.ShouldBe(ArtifactRetentionAction.Indeterminate, "no purge path is a terminal keep, not a retry that eventually deletes");
        decision.ErrorCode.ShouldBe(expectedCode);
    }

    [Fact]
    public void An_artifact_whose_placement_could_not_be_read_is_kept_without_becoming_terminal()
    {
        // Distinct from the three above: not knowing WHERE the bytes are is transient, so it must stay retryable rather
        // than settle as a permanent keep on one unreadable read.
        var decision = ArtifactRetentionDecision.Decide(Rule, Collectable() with { Purge = ArtifactPurgePath.Unknown });

        decision.Action.ShouldBe(ArtifactRetentionAction.Retry);
        decision.ErrorCode.ShouldBe("artifact-placement-indeterminate");
    }

    [Fact]
    public void An_offloaded_artifact_whose_blob_no_other_row_names_is_collectable_exactly_like_an_inline_one()
    {
        // The lane's whole point: bytes outside the row are not automatically unreapable — only unreapable bytes are.
        ArtifactRetentionDecision.Decide(Rule, Collectable() with { Purge = ArtifactPurgePath.LocalBlobExclusive })
            .Action.ShouldBe(ArtifactRetentionAction.Collect);
    }

    [Theory]
    [InlineData(0)]      // written this instant
    [InlineData(1)]      // a day old
    [InlineData(6)]      // one day short of the floor
    public void An_artifact_younger_than_its_class_age_floor_is_never_collected_even_when_nothing_references_it(int daysOld)
    {
        // Retention is a POLICY, not "delete when unreferenced": a just-written object whose reference has not yet
        // committed is unreferenced AND uncollectable.
        var observation = Collectable() with { ArtifactCreatedAt = Now.AddDays(-daysOld) };

        var decision = ArtifactRetentionDecision.Decide(Rule, observation);

        decision.Action.ShouldBe(ArtifactRetentionAction.Wait);
        decision.ErrorCode.ShouldBe("age-floor-open");
        decision.NextSweepAt.ShouldBe(observation.ArtifactCreatedAt.Add(Rule.MinimumAge), "the row must not be re-read before the floor it is waiting on");
    }

    [Fact]
    public void A_first_unreferenced_observation_only_quarantines_and_never_collects()
    {
        // The second, independent wait. A declaration that has never been observed unreferenced starts the clock here;
        // it cannot be deleted in the same sweep that first noticed it.
        var decision = ArtifactRetentionDecision.Decide(Rule, Collectable() with { State = ArtifactRetentionState.Declared, QuarantinedAt = null });

        decision.Action.ShouldBe(ArtifactRetentionAction.Quarantine);
        decision.NextSweepAt.ShouldBe(Now.Add(Rule.QuarantineWindow));
    }

    [Fact]
    public void A_quarantine_window_that_has_not_elapsed_keeps_the_artifact()
    {
        var quarantinedAt = Now.Add(-Rule.QuarantineWindow).AddSeconds(1);

        var decision = ArtifactRetentionDecision.Decide(Rule, Collectable() with { QuarantinedAt = quarantinedAt });

        decision.Action.ShouldBe(ArtifactRetentionAction.Wait);
        decision.ErrorCode.ShouldBe("quarantine-window-open");
        decision.NextSweepAt.ShouldBe(quarantinedAt.Add(Rule.QuarantineWindow));
    }

    /// <summary>The one collectable input: old enough, bytes removable, proven unreferenced, and quarantined longer than the window.</summary>
    private static ArtifactRetentionObservation Collectable() => new(
        ArtifactRetentionState.Quarantined, Now.AddDays(-30), ArtifactPurgePath.Inline, Now.Add(-Rule.QuarantineWindow), ArtifactReferenceVerdict.Unreferenced, Now);
}
