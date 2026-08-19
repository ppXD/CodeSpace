using CodeSpace.Core.Services.Agents.Capture;
using CodeSpace.Messages.Agents;
using Microsoft.Extensions.Logging;

namespace CodeSpace.Core.Services.Agents.Reduction;

/// <summary>
/// Drives <see cref="HarnessReductionFold"/> from the frames a capture opening actually produced, and hands back the
/// checkpoint that must be written WITH them.
///
/// <para><b>What this closes.</b> A re-attached run folds only its tail: the executor's fold is O(1) per event but it
/// starts from nothing after a worker replacement, so what it recovers is silently a different value from the one a
/// whole-stream fold would produce. This sink resumes the fold from the stored checkpoint of the SAME execution —
/// which is also what makes a revise round the continuation of one reduction rather than a second one that forgot the
/// first.</para>
///
/// <para><b>What it recovers TODAY, stated because the shape of <see cref="HarnessReducedStateV1"/> invites the wider
/// reading.</b> The counts, the channel set and the prefix digest of the whole recorded prefix — everything
/// <see cref="HarnessReductionFold"/> takes from a RECORD — plus the named facts an exactly grounded projection
/// actually carries. <see cref="HarnessReducedStateV1.FirstSessionId"/> comes from
/// <see cref="GroundedFrameProjector"/>, which reads the harness's own structured session record for a harness that
/// implements <see cref="IAgentGroundedFrameReader"/> (Claude Code and Codex do).
/// <see cref="HarnessReducedStateV1.FirstModelCallId"/> and <see cref="HarnessReducedStateV1.LastModelCallId"/> come
/// from <see cref="GroundedModelCallProjector"/>, which reads the harness's own record of ONE model call — so they are
/// filled only for a harness that implements <see cref="IAgentModelCallFrameReader"/> (Claude Code does; Codex prints
/// no per-call record) and only for a run bound to a workflow run, because those ids name rows in a run-keyed plane and
/// nothing mints one where no row can exist. They arrived by an exactly grounded READING of the harness's own frames
/// and NOT by relaxing the grounding rule, which is the thing keeping a guess out of a field a warm resume reads as an
/// established fact.</para>
///
/// <para><see cref="HarnessReducedStateV1.LastRequiredEventType"/> is still null on every run, for the one stated
/// reason: every projection this plane writes is <see cref="SemanticEventNecessity.Ignorable"/>. And the MODEL a run
/// chose has no field here at all — it is a column of the model-call rows those ids point at, not of this state, and
/// adding one would change the state SHAPE, which this reducer kind's own contract makes a new kind rather than a
/// rewrite.</para>
///
/// <para><b>Ordering, stated because it is the whole correctness argument.</b> The batch is folded BEFORE it is
/// written, and the checkpoint rides the write's own transaction
/// (<see cref="INativeRecordReductionPlane.WriteReducedAsync"/>). So "a frame recorded but not yet folded" cannot
/// exist: the write is the only thing that makes a frame durable, and by the time it runs the frame is already in the
/// fold. A crash lands on one side or the other of a single commit — before it, and neither the frames nor the
/// position exist; after it, and both do. The stored position therefore never LEADS the durable records (which 0140
/// refuses) and never LAGS them (which would silently shorten a resumed prefix).</para>
///
/// <para><b>Why not <see cref="HarnessStreamReducer"/>.</b> That driver PULLS from an
/// <see cref="IHarnessRecordSource"/> and writes each checkpoint through its own callback, which is the right shape
/// for a pass over a durable stream and the wrong one here: this path is pushed by the live pump, and its checkpoint
/// cannot be an independent write without reopening exactly the window above. Both drive the same
/// <see cref="HarnessReductionFold"/>, so the equality between a resumed fold and a whole-stream fold that
/// <c>HarnessReductionFoldTests</c> proves differentially is the same property either way. The pull driver has no
/// production caller yet, and its own summary says so.</para>
///
/// <para>Retention is O(1) in the event count: one fold, whose every field is a count, a first, a last, a bounded set
/// or a rolling digest, plus the batch the caller was already holding.</para>
///
/// <para>Bookkeeping, never authority. Every failure — a plane that will not read a checkpoint, a stored state that no
/// longer parses, a frame the fold refuses — disables the reduction for the rest of the round and leaves the run
/// resolving exactly as it does where this plane is not deployed.</para>
/// </summary>
internal sealed class HarnessReductionSink
{
    private readonly ILogger _logger;
    private readonly Guid _agentRunId;
    private HarnessReductionFold? _fold;

    private HarnessReductionSink(HarnessReductionFold? fold, Guid agentRunId, ILogger logger)
    {
        _fold = fold;
        _agentRunId = agentRunId;
        _logger = logger;
    }

    /// <summary>Whether frames are actually being folded. False ⇒ the capture path is unchanged and no checkpoint is ever offered.</summary>
    internal bool IsReducing => _fold is not null;

    /// <summary>A sink that folds nothing — for an opening with no reduction plane behind it, so the absence is a named decision rather than a null nobody explains.</summary>
    internal static HarnessReductionSink Disabled(ILogger logger) => new(null, Guid.Empty, logger);

    /// <summary>
    /// Resume this execution's reduction, or begin one when nothing is stored. The resume is the point: a re-attach
    /// and a revise round both re-enter an execution whose earlier frames this worker never saw, and a fold that
    /// started from <see cref="HarnessReductionFold.SeedCheckpoint"/> there would answer with whatever the tail
    /// happened to carry.
    /// </summary>
    internal static async Task<HarnessReductionSink> OpenAsync(INativeRecordPlane? plane, NativeRecordCaptureHandle handle, ILogger logger, CancellationToken cancellationToken)
    {
        if (plane is not INativeRecordReductionPlane reductions) return Disabled(logger);

        try
        {
            var stored = await reductions.ReadCheckpointAsync(handle.TeamId, handle.ExecutionId, HarnessReductionFold.ReducerKind, cancellationToken).ConfigureAwait(false);

            return new HarnessReductionSink(new HarnessReductionFold(stored ?? HarnessReductionFold.SeedCheckpoint(handle.ExecutionId)), handle.AgentRunId, logger);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(exception, "Harness stream reduction could not resume for agent run {RunId}; frames are captured unchanged and no checkpoint is written", handle.AgentRunId);

            return Disabled(logger);
        }
    }

    /// <summary>
    /// Fold this batch and return the checkpoint its write must carry, or null when there is nothing new to claim —
    /// which is also what a disabled reduction always answers, so the caller's write path reads the same either way.
    /// </summary>
    internal HarnessReductionCheckpointV1? Reduce(NativeRecordBatch batch)
    {
        if (_fold is not { } fold) return null;

        try
        {
            var reduced = FoldBatch(fold, batch);

            return reduced ? fold.Checkpoint : null;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Harness stream reduction could not fold a captured batch for agent run {RunId}; the reduction stops for this round and the frames are written unchanged", _agentRunId);

            _fold = null;

            return null;
        }
    }

    /// <summary>Folds every frame of the batch in capture order and answers whether any of them advanced the fold — a batch entirely behind the frontier claims no new position and must offer no checkpoint.</summary>
    private static bool FoldBatch(HarnessReductionFold fold, NativeRecordBatch batch)
    {
        var projections = batch.Events.ToLookup(CompletedBy);
        var reduced = false;

        EnsureEveryProjectionIsGrounded(batch, projections);

        foreach (var capture in batch.Records)
        {
            var frame = new HarnessReductionFrame { Record = capture.Frame, Projections = projections[capture.Frame.RecordId].ToList() };

            EnsureFoldable(frame);

            reduced |= fold.Add(frame) == HarnessFrameDisposition.Reduced;
        }

        return reduced;
    }

    /// <summary>The frame a projection rides on: the LAST record it cites, which is the one that completed it, so a projection folded from several records is counted once and still cites the earlier ones. Empty for a projection citing nothing, which is refused below rather than folded onto an arbitrary frame.</summary>
    private static Guid CompletedBy(AgentSemanticEventV1 projection) =>
        projection.SourceNativeRecordIds.Count == 0 ? Guid.Empty : projection.SourceNativeRecordIds[^1];

    /// <summary>
    /// Refuses a batch carrying a projection whose completing frame is not in it. The pump buffers a record and the
    /// events projected from it together and flushes them together, so this cannot happen today — and if it ever does,
    /// the alternative is folding a prefix that quietly omits a projection, which is the shape of the double count and
    /// the silent loss this whole reduction exists to end.
    /// </summary>
    private static void EnsureEveryProjectionIsGrounded(NativeRecordBatch batch, ILookup<Guid, AgentSemanticEventV1> projections)
    {
        var records = batch.Records.Select(capture => capture.Frame.RecordId).ToHashSet();

        foreach (var grouping in projections)
        {
            if (!records.Contains(grouping.Key))
                throw new HarnessReductionSourceException($"a captured batch carries {grouping.Count()} projection(s) completed by record '{grouping.Key}', which is not one of the frames it is writing");
        }
    }

    /// <summary>Rejected before the fold, exactly as the pull driver rejects it: an unreadable record chained into the prefix digest makes every later checkpoint a witness to a prefix that never existed.</summary>
    private static void EnsureFoldable(HarnessReductionFrame frame)
    {
        var errors = frame.Validate();

        if (errors.Count > 0)
            throw new HarnessReductionSourceException($"a captured frame cannot be folded: {string.Join("; ", errors)}");
    }
}
