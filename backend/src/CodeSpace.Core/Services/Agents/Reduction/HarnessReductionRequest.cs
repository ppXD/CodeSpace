using CodeSpace.Messages.Agents;

namespace CodeSpace.Core.Services.Agents.Reduction;

/// <summary>
/// One forward pass of a reduction: where to resume from, where to read, how often to offer a checkpoint, and who
/// writes it. Consolidated into a record because the four travel together and a five-parameter method would be at the
/// signature cap with nothing left for the cancellation token.
/// </summary>
public sealed record HarnessReductionRequest
{
    /// <summary>Records between offers. A committed constant, tuned once here: a smaller value replays fewer frames after a crash and writes more rows, a larger one the reverse, and neither is a per-deployment decision.</summary>
    public const int DefaultCheckpointEveryRecords = 256;

    /// <summary>The stored checkpoint to continue, or <see cref="HarnessReductionFold.SeedCheckpoint"/> to begin.</summary>
    public required HarnessReductionCheckpointV1 ResumeFrom { get; init; }

    /// <summary>Where the frames come from.</summary>
    public required IHarnessRecordSource Source { get; init; }

    /// <summary>
    /// Writes a checkpoint the pass has ALREADY folded. It is a callback rather than a return value because that is
    /// what makes the crash direction structural: the reducer can only ever hand over a position it has consumed, so a
    /// stored checkpoint can lag the fold and never lead it. A crash between folding and this call therefore re-consumes
    /// on the next pass — frames come back as <see cref="HarnessFrameDisposition.AlreadyReduced"/> and are skipped — and
    /// never skips. The opposite direction, checkpointing before folding, would lose those records permanently, which is
    /// the same silent-prefix-loss this whole reduction exists to end.
    /// </summary>
    public required Func<HarnessReductionCheckpointV1, CancellationToken, Task> OnCheckpointAsync { get; init; }

    /// <summary>How many newly folded records to accumulate before offering a checkpoint; at least one, and a non-positive value is refused rather than silently becoming a durable write per record. The end of the pass always offers one when anything was folded since the last offer.</summary>
    public int CheckpointEveryRecords { get; init; } = DefaultCheckpointEveryRecords;
}
