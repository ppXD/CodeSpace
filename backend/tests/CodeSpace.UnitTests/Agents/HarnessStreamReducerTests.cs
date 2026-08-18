using CodeSpace.Core.Services.Agents.Reduction;
using CodeSpace.Messages.Agents;
using Shouldly;

namespace CodeSpace.UnitTests.Agents;

/// <summary>
/// The cadence and the crash direction. Every checkpoint the reducer offers must already have been folded — that is the
/// whole of CONSUME THEN CHECKPOINT, and it is what makes a crash between the two re-consume rather than skip. The
/// opposite direction loses those records permanently, which is the silent prefix loss this reduction exists to end.
/// </summary>
[Trait("Category", "Unit")]
public sealed class HarnessStreamReducerTests
{
    [Fact]
    public async Task Every_offered_checkpoint_covers_only_frames_already_folded()
    {
        var frames = HarnessReductionStream.Representative();
        var offers = new List<HarnessReductionCheckpointV1>();

        var outcome = await Reduce(frames, offers, checkpointEvery: 3);

        offers.Count.ShouldBe(4, customMessage: "10 frames at a cadence of 3 is three full offers plus the end-of-pass one");
        offers.Select(offer => offer.State.RecordsConsumed).ShouldBe(new long[] { 3, 6, 9, 10 });
        outcome.CheckpointsOffered.ShouldBe(4);
        outcome.FramesReduced.ShouldBe(10);
        outcome.FramesReplayed.ShouldBe(0);

        // The offers are snapshots, not views: the last one having 10 must not have retroactively moved the first.
        offers[0].Position.RecordsConsumed.ShouldBe(3);
        offers.ShouldAllBe(offer => offer.Validate().Count == 0);
        outcome.Checkpoint.State.FirstSessionId.ShouldBe(HarnessReductionStream.SessionId);
    }

    [Fact]
    public async Task A_pass_that_folds_nothing_offers_no_checkpoint()
    {
        var frames = HarnessReductionStream.Representative();
        var offers = new List<HarnessReductionCheckpointV1>();
        var finished = await Reduce(frames, new List<HarnessReductionCheckpointV1>(), checkpointEvery: 1000);

        var second = await Reduce(frames, offers, checkpointEvery: 1000, resumeFrom: finished.Checkpoint);

        offers.ShouldBeEmpty(customMessage: "re-reading a fully consumed stream must not rewrite the row it would not change");
        second.FramesReduced.ShouldBe(0);
        second.CheckpointsOffered.ShouldBe(0);
        second.Checkpoint.State.ShouldBe(finished.Checkpoint.State);
    }

    /// <summary>
    /// The crash: a checkpoint was folded but never stored, so the next pass resumes from an older position and its
    /// source re-delivers everything. The pass must land on exactly the state the uninterrupted pass reached, and must
    /// report the replay honestly rather than counting it as work.
    /// </summary>
    [Fact]
    public async Task A_crash_between_folding_and_storing_re_consumes_and_never_skips()
    {
        var frames = HarnessReductionStream.Representative();
        var stored = new List<HarnessReductionCheckpointV1>();

        // The pass folds all ten and offers at 4, 8 and 10, but the process dies before the last offer is stored.
        await Reduce(frames, stored, checkpointEvery: 4);
        stored.Select(offer => offer.State.RecordsConsumed).ShouldBe(new long[] { 4, 8, 10 });
        var lastDurable = stored[^2];

        var replaying = new FixedHarnessRecordSource(frames) { RedeliverEverything = true };
        var offers = new List<HarnessReductionCheckpointV1>();
        var recovered = await new HarnessStreamReducer().ReduceForwardAsync(Request(replaying, offers, 4, lastDurable), CancellationToken.None);

        recovered.FramesReplayed.ShouldBe(8, customMessage: "the eight frames the stored checkpoint already covers must be skipped, not folded again");
        recovered.FramesReduced.ShouldBe(2);
        recovered.Checkpoint.State.ShouldBe((await Reduce(frames, new List<HarnessReductionCheckpointV1>(), checkpointEvery: 1000)).Checkpoint.State);
        recovered.Checkpoint.State.FirstSessionId.ShouldBe(HarnessReductionStream.SessionId, customMessage: "the session id was named only by frame 0, which this pass never saw as new work");
        replaying.Reads.Single().RecordsConsumed.ShouldBe(8);
    }

    [Fact]
    public async Task An_unreadable_frame_is_refused_at_the_seam_before_it_can_reach_the_prefix_digest()
    {
        var poisoned = HarnessReductionStream.Representative().ToList();
        poisoned[1] = poisoned[1] with { Record = poisoned[1].Record with { Digest = "not-a-digest" } };

        var failure = await Should.ThrowAsync<HarnessReductionSourceException>(() => Reduce(poisoned, new List<HarnessReductionCheckpointV1>(), checkpointEvery: 1000));

        failure.Message.ShouldContain("cannot be folded");
        failure.Message.ShouldContain("digest");
    }

    [Fact]
    public void The_default_cadence_is_a_committed_constant()
    {
        HarnessReductionRequest.DefaultCheckpointEveryRecords.ShouldBe(256,
            customMessage: "the cadence is tuned once, in code, and changed by a reviewed commit — never per deployment");
    }

    /// <summary>
    /// <c>sinceOffer</c> is at least one wherever the cadence is compared, so a non-positive cadence does not disable
    /// checkpointing — it offers after EVERY record, and since the offer is the durable writer, the pass silently
    /// becomes one UPDATE per captured line. It is refused rather than accepted as a configuration.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task A_non_positive_cadence_is_refused_rather_than_becoming_a_durable_write_per_record(int cadence)
    {
        var refused = await Should.ThrowAsync<ArgumentOutOfRangeException>(() => Reduce(HarnessReductionStream.Representative(), new List<HarnessReductionCheckpointV1>(), checkpointEvery: cadence));

        refused.ParamName.ShouldBe(nameof(HarnessReductionRequest.CheckpointEveryRecords));
        refused.Message.ShouldContain("at least one record");
    }

    [Fact]
    public async Task A_cadence_of_one_is_legal_and_offers_after_every_record()
    {
        var offers = new List<HarnessReductionCheckpointV1>();

        await Reduce(HarnessReductionStream.Representative(), offers, checkpointEvery: 1);

        offers.Count.ShouldBe(10, customMessage: "one is the smallest honest cadence — expensive, but exactly what it says; the guard's boundary is at zero, not at two");
    }

    private static async Task<HarnessReductionOutcome> Reduce(IReadOnlyList<HarnessReductionFrame> frames, List<HarnessReductionCheckpointV1> offers, int checkpointEvery, HarnessReductionCheckpointV1? resumeFrom = null)
    {
        var source = new FixedHarnessRecordSource(frames);

        return await new HarnessStreamReducer().ReduceForwardAsync(Request(source, offers, checkpointEvery, resumeFrom), CancellationToken.None);
    }

    private static HarnessReductionRequest Request(IHarnessRecordSource source, List<HarnessReductionCheckpointV1> offers, int checkpointEvery, HarnessReductionCheckpointV1? resumeFrom) => new()
    {
        ResumeFrom = resumeFrom ?? HarnessReductionFold.SeedCheckpoint(HarnessReductionStream.ExecutionId),
        Source = source,
        CheckpointEveryRecords = checkpointEvery,
        OnCheckpointAsync = (checkpoint, _) =>
        {
            offers.Add(checkpoint);
            return Task.CompletedTask;
        },
    };
}
