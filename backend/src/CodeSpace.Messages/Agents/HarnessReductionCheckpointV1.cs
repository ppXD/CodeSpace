using CodeSpace.Messages.Contracts;

namespace CodeSpace.Messages.Agents;

/// <summary>
/// The durable answer to "how far did the fold get, and what had it reduced?" — the pair a re-attach needs and today
/// has no place to keep. Persisted as <c>workflow_run_harness_reduction_checkpoint</c>.
///
/// <para>Position and state travel TOGETHER, in one value and one row, because a position without its state is a
/// promise to skip records nothing folded, and a state without its position is a fold nothing can continue. The
/// database refuses any write where the two disagree; <see cref="Validate"/> is the same statement in-process, so a
/// caller finds out before the round trip rather than after it.</para>
///
/// <para><see cref="ReducerKind"/> carries its OWN <c>/vN</c> and is immutable: a reduction whose state shape changes
/// is a new kind stored beside the old one, never a rewrite that hands an old reader a state it cannot parse.</para>
/// </summary>
public sealed record HarnessReductionCheckpointV1
{
    /// <summary>Data-contract version these fields are stamped with.</summary>
    public required int ContractVersion { get; init; }

    /// <summary>The harness execution whose captured records this reduction folds.</summary>
    public required Guid ExecutionId { get; init; }

    /// <summary>Stable <c>&lt;kind&gt;/v&lt;major&gt;</c> of the reduction that produced <see cref="State"/>.</summary>
    public required string ReducerKind { get; init; }

    /// <summary>Exactly the prefix <see cref="State"/> was folded from.</summary>
    public required HarnessReductionPosition Position { get; init; }

    /// <summary>The bounded reduction of that prefix.</summary>
    public required HarnessReducedStateV1 State { get; init; }

    /// <summary>Every reason this checkpoint cannot be resumed from. Empty ⇒ readable.</summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();

        if (!WorkflowRunDataContract.IsSupported(ContractVersion))
            errors.Add($"contractVersion '{ContractVersion}' is unsupported");
        if (ExecutionId == Guid.Empty)
            errors.Add("executionId must be non-empty");
        if (string.IsNullOrWhiteSpace(ReducerKind))
            errors.Add("reducerKind must be non-empty");
        if (State.ContractVersion != ContractVersion)
            errors.Add("state must carry the checkpoint's own contract version");

        errors.AddRange(Position.Validate().Select(error => $"position: {error}"));
        errors.AddRange(State.Validate().Select(error => $"state: {error}"));

        // The one invariant the whole lane exists for: a checkpoint may never claim a position it has not consumed.
        if (Position.RecordsConsumed != State.RecordsConsumed)
            errors.Add($"position accounts for {Position.RecordsConsumed} records but the state reduced {State.RecordsConsumed}");

        return errors;
    }
}

/// <summary>
/// What one forward pass of a reduction did. <see cref="FramesReplayed"/> is the honest half: after a crash between
/// consuming and checkpointing, a source legitimately re-delivers frames the stored position already covers, and they
/// are skipped rather than folded twice. A pass that replays a great many frames every time is a cadence that is too
/// coarse for the source, not a correctness problem — which is only distinguishable because the count is reported.
/// </summary>
public sealed record HarnessReductionOutcome
{
    /// <summary>The checkpoint at the end of the pass — the exact position folded and the state folded from it.</summary>
    public required HarnessReductionCheckpointV1 Checkpoint { get; init; }

    /// <summary>Frames folded in during this pass.</summary>
    public required long FramesReduced { get; init; }

    /// <summary>Frames the source re-delivered from behind the resume position and that were therefore skipped.</summary>
    public required long FramesReplayed { get; init; }

    /// <summary>How many times the pass offered a checkpoint to its writer. Every offer is made AFTER the frames it covers were folded, never before.</summary>
    public required int CheckpointsOffered { get; init; }
}
