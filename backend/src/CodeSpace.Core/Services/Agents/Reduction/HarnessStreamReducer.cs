using CodeSpace.Core.DependencyInjection;
using CodeSpace.Messages.Agents;

namespace CodeSpace.Core.Services.Agents.Reduction;

/// <summary>
/// Drives a <see cref="HarnessReductionFold"/> forward over an injected <see cref="IHarnessRecordSource"/>, offering a
/// checkpoint at its cadence and once more at the end of the pass.
///
/// <para>Stateless: everything about a pass lives in its <see cref="HarnessReductionRequest"/> and in the fold it
/// creates, so two passes can run concurrently over different executions without sharing anything. Registered as a
/// transient dependency, and nothing in production resolves it yet: the executor drives the same
/// <see cref="HarnessReductionFold"/> from the other direction, PUSHED by the live capture pump
/// (<c>HarnessReductionSink</c>), because on that path the checkpoint has to commit inside the batch's own
/// transaction and therefore cannot be an independent write through
/// <see cref="HarnessReductionRequest.OnCheckpointAsync"/>. This driver is what a pass over an already-durable stream
/// needs — a backfill, a re-reduction under a new reducer kind — and that caller does not exist yet.</para>
///
/// <para>The cadence choice is CONSUME THEN CHECKPOINT, and <see cref="HarnessReductionRequest.OnCheckpointAsync"/>
/// documents why: an offer can only carry a position already folded, so a crash re-consumes rather than skips. What
/// makes that safe is the fold's position guard, not idempotent reductions — the counts and the prefix digest are
/// deliberately not idempotent, and a replayed frame is refused entry instead of folded twice.</para>
/// </summary>
public sealed class HarnessStreamReducer : ITransientDependency
{
    /// <summary>Fold every frame the source has after the resume position, offering checkpoints as they are earned.</summary>
    public async Task<HarnessReductionOutcome> ReduceForwardAsync(HarnessReductionRequest request, CancellationToken cancellationToken)
    {
        EnsureCadence(request);

        var fold = new HarnessReductionFold(request.ResumeFrom);
        long reduced = 0, replayed = 0, sinceOffer = 0;
        var offered = 0;

        await foreach (var frame in request.Source.ReadForwardAsync(request.ResumeFrom.Position, cancellationToken).ConfigureAwait(false))
        {
            EnsureFoldable(frame);

            if (fold.Add(frame) == HarnessFrameDisposition.AlreadyReduced)
            {
                replayed++;
                continue;
            }

            reduced++;
            sinceOffer++;

            if (sinceOffer < request.CheckpointEveryRecords) continue;

            await request.OnCheckpointAsync(fold.Checkpoint, cancellationToken).ConfigureAwait(false);
            sinceOffer = 0;
            offered++;
        }

        if (sinceOffer > 0)
        {
            await request.OnCheckpointAsync(fold.Checkpoint, cancellationToken).ConfigureAwait(false);
            offered++;
        }

        return new HarnessReductionOutcome { Checkpoint = fold.Checkpoint, FramesReduced = reduced, FramesReplayed = replayed, CheckpointsOffered = offered };
    }

    /// <summary>Rejected at the seam, before a single frame is read: <c>sinceOffer</c> is at least one wherever the cadence is compared, so a non-positive one offers after EVERY record and turns the pass into one durable write per captured line — the opposite of what a checkpoint is for. One is the smallest honest value and means exactly that.</summary>
    private static void EnsureCadence(HarnessReductionRequest request)
    {
        if (request.CheckpointEveryRecords <= 0)
            throw new ArgumentOutOfRangeException(nameof(request.CheckpointEveryRecords), request.CheckpointEveryRecords, "a checkpoint cadence must be at least one record");
    }

    /// <summary>Rejected at the seam, before the fold: an unreadable record chained into the prefix digest makes every later checkpoint a witness to a prefix that never existed.</summary>
    private static void EnsureFoldable(HarnessReductionFrame frame)
    {
        var errors = frame.Validate();

        if (errors.Count > 0)
            throw new HarnessReductionSourceException($"the record source yielded a frame that cannot be folded: {string.Join("; ", errors)}");
    }
}
