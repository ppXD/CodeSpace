namespace CodeSpace.Messages.Agents;

/// <summary>
/// The rolling auto-compact digest of a supervisor run's OLDEST decisions (P1.2 — a data noun, Rule 18.1): the
/// prompt renders <see cref="Text"/> in place of every prior decision with <c>Sequence ≤ UpToSequence</c>, so an
/// hours-long run's decision tape stops outgrowing the brain model's context window. Written by the decider on the
/// FIRST ContextLengthExceeded (one bounded summarizer call), persisted per run (one rolling row, re-compacted
/// forward as the run keeps growing), and loaded at rehydrate. NEVER consulted by bounds / recitation / replay —
/// those read the complete tape.
/// </summary>
public sealed record SupervisorTapeSummary
{
    /// <summary>The highest ledger <c>Sequence</c> folded into this digest — the prompt renders only decisions AFTER it.</summary>
    public required long UpToSequence { get; init; }

    /// <summary>The model-written progress digest (what was planned, which subtasks settled how, branches produced, key learnings).</summary>
    public required string Text { get; init; }
}
