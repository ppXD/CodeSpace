using System.Text.Json;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Agents.Reduction;
using CodeSpace.Messages.Agents;
using Shouldly;

namespace CodeSpace.UnitTests.Agents;

/// <summary>
/// The lane's core claim, proved differentially: resuming a reduction from a durable checkpoint lands on a state
/// INDISTINGUISHABLE from folding the whole stream. Every segmented fold here goes through JSON on the way, because
/// that is what durability actually does — an in-memory hand-off would prove the fold and skip the part that stores it.
///
/// <para>The headline is <see cref="Resuming_recovers_a_fact_stated_only_once_before_the_boundary"/>: the session id is
/// named by frame 0 and by nothing else, which is exactly the fact today's tail-only re-attach loses. Every other test
/// here would still pass with a reducer that dropped it.</para>
/// </summary>
[Trait("Category", "Unit")]
public sealed class HarnessReductionFoldTests
{
    [Fact]
    public void Folding_the_whole_stream_reduces_every_bounded_fact_in_its_declared_direction()
    {
        var whole = FoldAll(HarnessReductionStream.Representative());

        whole.State.RecordsConsumed.ShouldBe(10);
        whole.State.ProjectionsConsumed.ShouldBe(8);
        whole.State.ExactlyGroundedProjections.ShouldBe(7, customMessage: "seven projections are Exact or RedactedExact; the Stderr one is Heuristic and may never back a strict read");
        whole.State.RequiredProjections.ShouldBe(2);
        whole.State.FirstSessionId.ShouldBe(HarnessReductionStream.SessionId);
        whole.State.FirstModelCallId.ShouldBe(HarnessReductionStream.FirstModelCallId);
        whole.State.LastModelCallId.ShouldBe(HarnessReductionStream.LastModelCallId);
        whole.State.LastRequiredEventType.ShouldBe(HarnessReductionStream.WinningTerminal, customMessage: "the superseded Required projection must be superseded, not kept beside its successor");
        whole.State.RedactedByteCount.ShouldBe(24, customMessage: "the masked tool frame dropped 64 - 40 bytes; nothing else was redacted");
        whole.State.ChannelsSeen.ShouldBe(new[]
        {
            NativeRecordChannel.SessionState, NativeRecordChannel.Stdout, NativeRecordChannel.Protocol,
            NativeRecordChannel.ModelWire, NativeRecordChannel.Stderr, NativeRecordChannel.ToolWire, NativeRecordChannel.Control,
        }, customMessage: "channels are the DISTINCT set in first-occurrence order — ModelWire arrives twice and Stdout three times");

        whole.Position.Streams.Select(stream => (stream.StreamId, stream.NextOrdinal)).ShouldBe(new[]
        {
            (HarnessReductionStream.PrimaryStreamId, 7L), (HarnessReductionStream.ProtocolStreamId, 3L),
        });
        whole.Position.RecordsConsumed.ShouldBe(whole.State.RecordsConsumed);
        whole.Validate().ShouldBeEmpty();
    }

    /// <summary>
    /// THE headline. The session id is stated by frame 0 and by no other frame, so a fold that starts at the boundary
    /// answers null — which is what a re-attach does today, and what nothing downstream can distinguish from a harness
    /// that never named a session at all.
    /// </summary>
    [Fact]
    public void Resuming_recovers_a_fact_stated_only_once_before_the_boundary()
    {
        var frames = HarnessReductionStream.Representative();
        var whole = FoldAll(frames);

        var throughBoundary = FoldAll(frames.Take(4).ToList());
        var resumed = FoldFrom(RoundTrip(throughBoundary), frames.Skip(4).ToList());

        // The premise: the boundary really does cut the only frame that names the session out of the tail.
        frames.Skip(4).ShouldAllBe(frame => frame.Projections.All(projection => projection.SessionId == null));

        // And the defect, reproduced: a reduction that knows WHERE the stream is but has reduced nothing — exactly what
        // a re-attach believes today — folds the same tail and answers "no session", indistinguishably from a harness
        // that never named one.
        var amnesiac = new HarnessReductionFold(throughBoundary with { State = Forgotten(throughBoundary.State.RecordsConsumed) });
        foreach (var frame in frames.Skip(4)) amnesiac.Add(frame);
        amnesiac.Checkpoint.State.FirstSessionId.ShouldBeNull(customMessage: "if the tail alone knew the session id, this test would prove nothing");

        resumed.State.FirstSessionId.ShouldBe(HarnessReductionStream.SessionId);
        resumed.State.ShouldBe(whole.State);
        resumed.Position.ShouldBe(whole.Position);
    }

    /// <summary>
    /// A guess may never become an established fact. The quality vocabulary exists because a session id pattern-matched
    /// out of prose and one read off a structured frame are indistinguishable once both are a normalized event — and
    /// <see cref="HarnessReducedStateV1.FirstSessionId"/> is precisely what a warm resume rests on. The counters still
    /// see the guesses, so the prefix stays honest about having contained them.
    /// </summary>
    [Fact]
    public void A_guessed_fact_is_counted_but_never_reaches_the_identity_a_warm_resume_reads()
    {
        var guessed = FoldAll(HarnessReductionStream.GuessedFactsOnly());

        guessed.State.ProjectionsConsumed.ShouldBe(2, customMessage: "the guesses are still folded and still counted — they are dropped from the named facts, not from the prefix");
        guessed.State.ExactlyGroundedProjections.ShouldBe(0);
        guessed.State.RequiredProjections.ShouldBe(1, customMessage: "the tally counts what the PRODUCER marked Required, whatever its provenance");

        guessed.State.FirstSessionId.ShouldBeNull(customMessage: "a session id scraped out of stderr must not arrive at a warm resume wearing the shape of one the harness stated");
        guessed.State.FirstModelCallId.ShouldBeNull();
        guessed.State.LastModelCallId.ShouldBeNull();
        guessed.State.LastRequiredEventType.ShouldBeNull();
        guessed.Validate().ShouldBeEmpty(customMessage: "keeping a guess out of the named facts must still leave the state internally consistent");
    }

    [Theory]
    [InlineData(4)]
    [InlineData(1)]
    [InlineData(9)]
    public void Resuming_from_one_boundary_is_indistinguishable_from_folding_the_whole_stream(int boundary)
    {
        var frames = HarnessReductionStream.Representative();
        var whole = FoldAll(frames);

        var resumed = FoldFrom(RoundTrip(FoldAll(frames.Take(boundary).ToList())), frames.Skip(boundary).ToList());

        AssertIndistinguishable(resumed, whole, $"resumed at {boundary}");
    }

    [Fact]
    public void Resuming_from_two_boundaries_is_indistinguishable_from_folding_the_whole_stream()
    {
        var frames = HarnessReductionStream.Representative();
        var whole = FoldAll(frames);

        var first = RoundTrip(FoldAll(frames.Take(3).ToList()));
        var second = RoundTrip(FoldFrom(first, frames.Skip(3).Take(4).ToList()));
        var third = FoldFrom(second, frames.Skip(7).ToList());

        AssertIndistinguishable(third, whole, "resumed at 3 then at 7");
        first.State.RecordsConsumed.ShouldBe(3);
        second.State.RecordsConsumed.ShouldBe(7);
    }

    /// <summary>
    /// The crash direction, in the direction this reducer chose: CONSUME then CHECKPOINT. A crash between the two
    /// re-delivers frames the stored checkpoint already covers, and they must be skipped rather than folded twice —
    /// because the counts and the prefix digest are deliberately NOT idempotent, so a second fold of the same record
    /// would silently produce a state no whole-stream fold could ever produce.
    /// </summary>
    [Fact]
    public void Re_consuming_a_checkpointed_frame_is_idempotent_and_folding_it_twice_would_not_be()
    {
        var frames = HarnessReductionStream.Representative();
        var whole = FoldAll(frames);

        var checkpoint = RoundTrip(FoldAll(frames.Take(6).ToList()));
        var fold = new HarnessReductionFold(checkpoint);
        var dispositions = frames.Select(fold.Add).ToList();

        dispositions.Take(6).ShouldAllBe(disposition => disposition == HarnessFrameDisposition.AlreadyReduced);
        dispositions.Skip(6).ShouldAllBe(disposition => disposition == HarnessFrameDisposition.Reduced);
        AssertIndistinguishable(fold.Checkpoint, whole, "every frame re-delivered after a checkpoint at 6");

        // Why the guard is load-bearing rather than belt-and-braces: 6 frames were already folded and all 10 were
        // delivered again, so a reduction that merely hoped its reductions were idempotent would report 16 records and
        // chain six of them into the prefix digest twice.
        dispositions.Count(disposition => disposition == HarnessFrameDisposition.AlreadyReduced).ShouldBe(6);
        fold.Checkpoint.State.RecordsConsumed.ShouldBe(10);
    }

    [Fact]
    public void A_frame_past_the_frontier_is_refused_rather_than_folded_into_a_prefix_with_a_hole()
    {
        var frames = HarnessReductionStream.Representative();
        var fold = new HarnessReductionFold(HarnessReductionFold.SeedCheckpoint(HarnessReductionStream.ExecutionId));

        fold.Add(frames[0]).ShouldBe(HarnessFrameDisposition.Reduced);

        var jumped = HarnessReductionStream.Frame(HarnessReductionStream.PrimaryStreamId, 4, NativeRecordChannel.Stdout, "assistant");
        var gap = Should.Throw<HarnessReductionGapException>(() => { fold.Add(jumped); });

        gap.ExpectedOrdinal.ShouldBe(1);
        gap.ObservedOrdinal.ShouldBe(4);
        gap.StreamId.ShouldBe(HarnessReductionStream.PrimaryStreamId);
        fold.Checkpoint.State.RecordsConsumed.ShouldBe(1, customMessage: "the refused frame must leave the frontier and the state exactly where they were");
    }

    /// <summary>
    /// The prefix digest is the witness that makes "this state reduced this prefix" checkable by somebody other than
    /// its author. Two folds over the same records in a different order, or over one record twice, must not collide
    /// with the honest value.
    /// </summary>
    [Fact]
    public void The_prefix_digest_witnesses_the_exact_prefix_and_not_merely_its_size()
    {
        var frames = HarnessReductionStream.Representative();
        var whole = FoldAll(frames);

        var reordered = new HarnessReductionFold(HarnessReductionFold.SeedCheckpoint(HarnessReductionStream.ExecutionId));
        foreach (var frame in Reorder(frames)) reordered.Add(frame);

        reordered.Checkpoint.State.RecordsConsumed.ShouldBe(whole.State.RecordsConsumed);
        reordered.Checkpoint.State.PrefixDigest.ShouldNotBe(whole.State.PrefixDigest, customMessage: "a digest that ignored position would let a differently-interleaved fold pass as the same prefix");
        FoldAll(frames.Take(9).ToList()).State.PrefixDigest.ShouldNotBe(whole.State.PrefixDigest);
        HarnessReductionFold.SeedCheckpoint(HarnessReductionStream.ExecutionId).State.PrefixDigest.Length.ShouldBe(64);
    }

    /// <summary>
    /// Retention is O(1) in the record count: 20 000 frames must leave a state no larger than 100 frames' worth, apart
    /// from the digits its counters gained. A reduction that started retaining anything per record — a list of event
    /// types, the ids it saw — fails here long before it exhausts a heap in production.
    /// </summary>
    [Fact]
    public void Retention_is_bounded_by_what_it_reduces_and_never_by_stream_length()
    {
        var small = Serialize(FoldAll(HarnessReductionStream.Long(100)));
        var large = Serialize(FoldAll(HarnessReductionStream.Long(20_000)));

        (large.Length - small.Length).ShouldBeLessThanOrEqualTo(12,
            customMessage: $"the reduced state grew by {large.Length - small.Length} characters over 19 900 extra records, so something in it is retained PER RECORD. Only counter digits may grow.\nsmall: {small}\nlarge: {large}");
        large.Length.ShouldBeLessThan(700, customMessage: $"a 20 000-record checkpoint must stay a small row:\n{large}");

        var folded = FoldAll(HarnessReductionStream.Long(20_000));
        folded.State.ChannelsSeen.Count.ShouldBe(1);
        folded.Position.Streams.Count.ShouldBe(1);
        folded.State.RecordsConsumed.ShouldBe(20_000);
    }

    /// <summary>
    /// A fold resumed from ANOTHER reduction's checkpoint would be internally consistent and quietly wrong — same
    /// field names, different meanings — so the kind is refused rather than trusted.
    /// </summary>
    [Fact]
    public void A_checkpoint_from_another_reduction_or_an_inconsistent_one_is_not_resumable()
    {
        var honest = FoldAll(HarnessReductionStream.Representative().Take(3).ToList());

        Should.Throw<ArgumentException>(() => new HarnessReductionFold(honest with { ReducerKind = "harness-prefix/v2" }))
            .Message.ShouldContain("cannot resume");

        var overclaiming = honest with { State = honest.State with { RecordsConsumed = honest.State.RecordsConsumed + 1 } };
        overclaiming.Validate().ShouldContain(error => error.Contains("position accounts for", StringComparison.Ordinal));
        Should.Throw<ArgumentException>(() => new HarnessReductionFold(overclaiming)).Message.ShouldContain("not resumable");
    }

    /// <summary>
    /// The reduced state's JSON KEYS are the schema 0140's guard reads: it cross-checks
    /// <c>recordsConsumed</c>, <c>contractVersion</c> and <c>prefixDigest</c> inside the stored object. A C# rename
    /// would leave the guard reading a key that no longer exists, so it is pinned here rather than discovered in
    /// production by a checkpoint that stopped being checked.
    /// </summary>
    [Fact]
    public void The_reduced_state_serializes_the_exact_keys_the_checkpoint_schema_cross_checks()
    {
        var state = FoldAll(HarnessReductionStream.Representative()).State;

        var json = JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(state, AgentJson.Options));

        json.GetProperty("recordsConsumed").GetInt64().ShouldBe(10);
        json.GetProperty("contractVersion").GetInt32().ShouldBe(1);
        json.GetProperty("prefixDigest").GetString().ShouldBe(state.PrefixDigest);

        var position = JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(FoldAll(HarnessReductionStream.Representative()).Position, AgentJson.Options));
        position.GetProperty("streams")[0].GetProperty("streamId").GetGuid().ShouldBe(HarnessReductionStream.PrimaryStreamId);
        position.GetProperty("streams")[0].GetProperty("nextOrdinal").GetInt64().ShouldBe(7);
        position.TryGetProperty("recordsConsumed", out _).ShouldBeFalse(customMessage: "the frontier must not carry a fourth writable copy of the count");
    }

    private static void AssertIndistinguishable(HarnessReductionCheckpointV1 actual, HarnessReductionCheckpointV1 whole, string how)
    {
        actual.State.RecordsConsumed.ShouldBe(whole.State.RecordsConsumed, customMessage: how);
        actual.State.ProjectionsConsumed.ShouldBe(whole.State.ProjectionsConsumed, customMessage: how);
        actual.State.ExactlyGroundedProjections.ShouldBe(whole.State.ExactlyGroundedProjections, customMessage: how);
        actual.State.RequiredProjections.ShouldBe(whole.State.RequiredProjections, customMessage: how);
        actual.State.ChannelsSeen.ShouldBe(whole.State.ChannelsSeen, customMessage: how);
        actual.State.FirstSessionId.ShouldBe(whole.State.FirstSessionId, customMessage: how);
        actual.State.FirstModelCallId.ShouldBe(whole.State.FirstModelCallId, customMessage: how);
        actual.State.LastModelCallId.ShouldBe(whole.State.LastModelCallId, customMessage: how);
        actual.State.LastRequiredEventType.ShouldBe(whole.State.LastRequiredEventType, customMessage: how);
        actual.State.RedactedByteCount.ShouldBe(whole.State.RedactedByteCount, customMessage: how);
        actual.State.PrefixDigest.ShouldBe(whole.State.PrefixDigest, customMessage: how);
        actual.Position.ShouldBe(whole.Position, customMessage: how);
        actual.State.ShouldBe(whole.State, customMessage: how);
    }

    private static HarnessReductionCheckpointV1 FoldAll(IReadOnlyList<HarnessReductionFrame> frames) =>
        FoldFrom(HarnessReductionFold.SeedCheckpoint(HarnessReductionStream.ExecutionId), frames);

    private static HarnessReductionCheckpointV1 FoldFrom(HarnessReductionCheckpointV1 resumeFrom, IReadOnlyList<HarnessReductionFrame> frames)
    {
        var fold = new HarnessReductionFold(resumeFrom);

        foreach (var frame in frames) fold.Add(frame);

        return fold.Checkpoint;
    }

    /// <summary>A position with nothing reduced behind it — the state a re-attach starts from today.</summary>
    private static HarnessReducedStateV1 Forgotten(long recordsConsumed) =>
        HarnessReductionFold.SeedCheckpoint(HarnessReductionStream.ExecutionId).State with { RecordsConsumed = recordsConsumed };

    /// <summary>The segment boundary as durability makes it: out through JSON and back, so a field that cannot survive storage cannot pass this suite.</summary>
    private static HarnessReductionCheckpointV1 RoundTrip(HarnessReductionCheckpointV1 checkpoint) =>
        JsonSerializer.Deserialize<HarnessReductionCheckpointV1>(JsonSerializer.Serialize(checkpoint, AgentJson.Options), AgentJson.Options)!;

    private static string Serialize(HarnessReductionCheckpointV1 checkpoint) => JsonSerializer.Serialize(checkpoint.State, AgentJson.Options);

    /// <summary>The same records with the two capture streams interleaved differently — a legal total order for a source, and a different prefix for a position-aware digest.</summary>
    private static IReadOnlyList<HarnessReductionFrame> Reorder(IReadOnlyList<HarnessReductionFrame> frames) =>
        frames.Where(frame => frame.Record.StreamId == HarnessReductionStream.PrimaryStreamId)
            .Concat(frames.Where(frame => frame.Record.StreamId == HarnessReductionStream.ProtocolStreamId))
            .ToArray();
}
