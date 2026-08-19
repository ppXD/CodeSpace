using CodeSpace.Core.Services.Agents.Capture;
using CodeSpace.Core.Services.Agents.Reduction;
using CodeSpace.Messages.Agents;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace CodeSpace.UnitTests.Agents;

/// <summary>
/// The sink that joins the reduction to the live capture pump, which is where the re-attach defect is either fixed or
/// not. <c>HarnessReductionFoldTests</c> already proves differentially that a fold resumed from a checkpoint lands on
/// the state a whole-stream fold produces; what is unproven until here is that the wiring actually RESUMES — that a
/// worker which never saw the pre-restart frames recovers their reduction from the stored row instead of folding its
/// tail into a state nobody can tell apart from the right one.
/// </summary>
[Trait("Category", "Unit")]
public sealed class HarnessReductionSinkTests
{
    /// <summary>
    /// The headline. A replaced worker sees only what came after it; resumed from the stored checkpoint, its reduction
    /// lands on exactly the state a fold of the whole stream produces — prefix digest included, which is the witness
    /// that it reduced this exact prefix rather than a shorter one that merely looks consistent.
    ///
    /// <para><b>What is and is not production here.</b> The counts, the channel set and the prefix digest are recovered
    /// on every real run, and so is <c>FirstSessionId</c> — <c>GroundedFrameProjector</c> fills it from the harness's
    /// own structured session record for a harness that implements <c>IAgentGroundedFrameReader</c>. This stream is
    /// still hand-built rather than pumped, so what it pins is the SINK's half: that a grounded fact reaching the fold
    /// survives a worker replacement. That the projector produces one is pinned over the real pump in
    /// <c>AgentNativeRecordPumpTests</c>, and end to end over a real re-attach in
    /// <c>HarnessReductionReattachFlowTests</c>.</para>
    /// </summary>
    [Fact]
    public async Task A_replaced_worker_resumes_the_prefix_and_keeps_a_fact_stated_once_before_it()
    {
        var frames = Captured();
        var plane = new RecordingReductionPlane(CheckpointOver(frames.Take(4)));

        var sink = await HarnessReductionSink.OpenAsync(plane, Handle(), NullLogger.Instance, CancellationToken.None);

        var resumed = sink.Reduce(Batch(frames.Skip(4)));

        resumed.ShouldNotBeNull("a batch of frames after the resume position must produce the checkpoint its write carries");
        resumed.State.PrefixDigest.ShouldBe(WholeStreamState(frames).PrefixDigest,
            customMessage: "the digest of the whole prefix is what a real run recovers here, and it is the witness that the resumed fold consumed the frames before the restart rather than only its own tail");
        resumed.State.FirstSessionId.ShouldBe(HarnessReductionStream.SessionId,
            customMessage: "a fact stated ONCE before the worker was replaced is the one a tail-only fold loses");
        resumed.State.ShouldBe(WholeStreamState(frames),
            customMessage: "a resumed reduction must be indistinguishable from a fold of the whole stream, or the recovered state is a different value no reader can tell apart from the right one");
        resumed.Position.RecordsConsumed.ShouldBe(frames.Count);
    }

    /// <summary>
    /// The same worker, resumed from NOTHING — which is what the re-attach path did before this wiring, and what it
    /// would do again if the stored checkpoint were ignored. It is a fold of the tail: internally consistent, honestly
    /// counted, and missing every fact stated before the restart.
    /// </summary>
    [Fact]
    public async Task A_worker_that_resumes_from_nothing_folds_only_its_tail()
    {
        var frames = Captured();
        var sink = await HarnessReductionSink.OpenAsync(new RecordingReductionPlane(stored: null), Handle(), NullLogger.Instance, CancellationToken.None);

        // The tail alone cannot even be folded onto a fresh frontier — its ordinals start past zero — so the reduction
        // stops rather than folding a prefix with a hole in it. That refusal IS the defect made visible: what the tail
        // could contribute is not the whole stream's state, and the fold will not pretend otherwise.
        sink.Reduce(Batch(frames.Skip(4))).ShouldBeNull();
        sink.IsReducing.ShouldBeFalse("a gap past the frontier stops the reduction; folding over it would store a state that reads like a consumed prefix");
    }

    /// <summary>A checkpoint may only ever claim the frames it has actually folded — the position and the state agree, and the position is exactly the batches handed in.</summary>
    [Fact]
    public async Task A_checkpoint_claims_exactly_the_batches_it_was_handed()
    {
        var frames = Captured();
        var sink = await HarnessReductionSink.OpenAsync(new RecordingReductionPlane(stored: null), Handle(), NullLogger.Instance, CancellationToken.None);

        var first = sink.Reduce(Batch(frames.Take(3)));
        var second = sink.Reduce(Batch(frames.Skip(3)));

        first!.Position.RecordsConsumed.ShouldBe(3);
        first.State.RecordsConsumed.ShouldBe(3, customMessage: "position and state state the same count, which is what makes 'it cannot claim what it did not consume' checkable rather than believed");
        second!.Position.RecordsConsumed.ShouldBe(frames.Count);
        second.Validate().ShouldBeEmpty();
    }

    /// <summary>
    /// A batch the source re-delivers after a crash is folded again by nobody: every frame is behind the frontier, so
    /// nothing advanced and no checkpoint is offered. Offering one would rewrite the row with an identical position for
    /// no reason; folding one would double every count and chain the digest twice.
    /// </summary>
    [Fact]
    public async Task A_batch_entirely_behind_the_frontier_offers_no_checkpoint()
    {
        var frames = Captured();
        var sink = await HarnessReductionSink.OpenAsync(new RecordingReductionPlane(stored: null), Handle(), NullLogger.Instance, CancellationToken.None);

        var advanced = sink.Reduce(Batch(frames));

        sink.Reduce(Batch(frames)).ShouldBeNull("a re-delivered batch advanced nothing, so there is no new position to claim");
        advanced!.State.RecordsConsumed.ShouldBe(frames.Count, customMessage: "and the state it already had must not have doubled");
    }

    /// <summary>A plane that cannot hand back a checkpoint leaves the pump capturing exactly as it would with no reduction wired at all — the run is untouched and so is the frame stream.</summary>
    [Fact]
    public async Task A_plane_that_will_not_read_a_checkpoint_disables_the_reduction_only()
    {
        var sink = await HarnessReductionSink.OpenAsync(new RefusingReductionPlane(), Handle(), NullLogger.Instance, CancellationToken.None);

        sink.IsReducing.ShouldBeFalse();
        sink.Reduce(Batch(Captured())).ShouldBeNull("no checkpoint may be written from a reduction that never resumed");
    }

    /// <summary>A plane with no reduction capability at all — the shape of a deployment or a hand-built double that knows only the batch writer. Frames are still captured; nothing else changes.</summary>
    [Fact]
    public async Task A_plane_without_the_reduction_capability_folds_nothing()
    {
        var sink = await HarnessReductionSink.OpenAsync(new BatchOnlyPlane(), Handle(), NullLogger.Instance, CancellationToken.None);

        sink.IsReducing.ShouldBeFalse();
        sink.Reduce(Batch(Captured())).ShouldBeNull();
    }

    /// <summary>
    /// A frame the contract cannot read must never reach the prefix digest: once a bad record is chained in, every
    /// later checkpoint witnesses a prefix that never existed. The reduction stops for the round and the frames are
    /// still written — losing the reduction of a frame is not losing the frame.
    /// </summary>
    [Fact]
    public async Task A_frame_the_fold_refuses_stops_the_reduction_and_writes_no_checkpoint()
    {
        var sink = await HarnessReductionSink.OpenAsync(new RecordingReductionPlane(stored: null), Handle(), NullLogger.Instance, CancellationToken.None);
        var unreadable = HarnessReductionStream.Frame(HarnessReductionStream.PrimaryStreamId, 0, NativeRecordChannel.Stdout, "assistant") with
        {
            Record = HarnessReductionStream.Record(HarnessReductionStream.PrimaryStreamId, 0, NativeRecordChannel.Stdout, "assistant") with { Digest = "not-a-digest" },
        };

        sink.Reduce(new NativeRecordBatch { Handle = Handle(), Records = new[] { Captured(unreadable) }, Events = Array.Empty<AgentSemanticEventV1>() }).ShouldBeNull();
        sink.IsReducing.ShouldBeFalse();

        sink.Reduce(Batch(Captured())).ShouldBeNull(
            customMessage: "a stopped reduction stays stopped for the round; resuming it mid-stream would fold a prefix with a hole in it");
    }

    /// <summary>A projection whose completing frame is not in the batch cannot be attributed to any position, so folding the batch would silently drop it. Refused instead.</summary>
    [Fact]
    public async Task A_projection_of_a_frame_outside_the_batch_stops_the_reduction()
    {
        var frames = Captured();
        var sink = await HarnessReductionSink.OpenAsync(new RecordingReductionPlane(stored: null), Handle(), NullLogger.Instance, CancellationToken.None);

        var orphaned = new NativeRecordBatch
        {
            Handle = Handle(),
            Records = new[] { Captured(frames[1]) },
            Events = frames[0].Projections,
        };

        sink.Reduce(orphaned).ShouldBeNull();
        sink.IsReducing.ShouldBeFalse();
    }

    /// <summary>
    /// The representative stream as a CAPTURE actually produces it: every projection cites the frame it was projected
    /// from, because the pump projects one frame at a time and the plane refuses an event grounded in nothing. The
    /// shared stream deliberately leaves an inferred projection uncited — legal for the contract type, and outside
    /// what a captured batch can express, since a projection with no frame has no position to be folded at.
    /// </summary>
    private static IReadOnlyList<HarnessReductionFrame> Captured() =>
        HarnessReductionStream.Representative().Select(frame => frame with
        {
            Projections = frame.Projections
                .Select(projection => projection.SourceNativeRecordIds.Count > 0 ? projection : projection with { SourceNativeRecordIds = new[] { frame.Record.RecordId } })
                .ToList(),
        }).ToList();

    private static NativeRecordCaptureHandle Handle() => new()
    {
        TeamId = Guid.NewGuid(),
        AgentRunId = Guid.NewGuid(),
        ExecutionId = HarnessReductionStream.ExecutionId,
        AttemptId = Guid.NewGuid(),
        StreamId = HarnessReductionStream.PrimaryStreamId,
        Channel = NativeRecordChannel.Stdout,
        WorkflowRunId = Guid.NewGuid(),
    };

    private static NativeRecordBatch Batch(IEnumerable<HarnessReductionFrame> frames)
    {
        var listed = frames.ToList();

        return new NativeRecordBatch
        {
            Handle = Handle(),
            Records = listed.Select(Captured).ToList(),
            Events = listed.SelectMany(frame => frame.Projections).ToList(),
        };
    }

    private static NativeRecordCapture Captured(HarnessReductionFrame frame) => new()
    {
        Frame = frame.Record,
        Normalization = frame.Projections.Count > 0 ? NativeRecordNormalization.Projected : NativeRecordNormalization.Unrecognized,
    };

    /// <summary>The checkpoint a worker would have stored before it was replaced: exactly the prefix it had folded.</summary>
    private static HarnessReductionCheckpointV1 CheckpointOver(IEnumerable<HarnessReductionFrame> prefix)
    {
        var fold = new HarnessReductionFold(HarnessReductionFold.SeedCheckpoint(HarnessReductionStream.ExecutionId));

        foreach (var frame in prefix) fold.Add(frame);

        return fold.Checkpoint;
    }

    private static HarnessReducedStateV1 WholeStreamState(IEnumerable<HarnessReductionFrame> frames) => CheckpointOver(frames).State;

    /// <summary>Hands back a stored checkpoint and remembers what it was asked for — the shape of a plane that has a durable reduction to resume.</summary>
    private sealed class RecordingReductionPlane : INativeRecordPlane, INativeRecordReductionPlane
    {
        private readonly HarnessReductionCheckpointV1? _stored;

        public RecordingReductionPlane(HarnessReductionCheckpointV1? stored) => _stored = stored;

        public Task<NativeRecordCaptureHandle?> OpenAsync(NativeRecordCaptureRequest request, CancellationToken cancellationToken) => Task.FromResult<NativeRecordCaptureHandle?>(null);

        public Task WriteAsync(NativeRecordBatch batch, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task CloseAsync(NativeRecordCaptureHandle handle, int? exitCode, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<HarnessReductionCheckpointV1?> ReadCheckpointAsync(Guid teamId, Guid executionId, string reducerKind, CancellationToken cancellationToken) => Task.FromResult(_stored);

        public Task WriteReducedAsync(NativeRecordBatch batch, HarnessReductionCheckpointV1 checkpoint, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class RefusingReductionPlane : INativeRecordPlane, INativeRecordReductionPlane
    {
        public Task<NativeRecordCaptureHandle?> OpenAsync(NativeRecordCaptureRequest request, CancellationToken cancellationToken) => Task.FromResult<NativeRecordCaptureHandle?>(null);

        public Task WriteAsync(NativeRecordBatch batch, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task CloseAsync(NativeRecordCaptureHandle handle, int? exitCode, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<HarnessReductionCheckpointV1?> ReadCheckpointAsync(Guid teamId, Guid executionId, string reducerKind, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("the stored reduction could not be read");

        public Task WriteReducedAsync(NativeRecordBatch batch, HarnessReductionCheckpointV1 checkpoint, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class BatchOnlyPlane : INativeRecordPlane
    {
        public Task<NativeRecordCaptureHandle?> OpenAsync(NativeRecordCaptureRequest request, CancellationToken cancellationToken) => Task.FromResult<NativeRecordCaptureHandle?>(null);

        public Task WriteAsync(NativeRecordBatch batch, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task CloseAsync(NativeRecordCaptureHandle handle, int? exitCode, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
