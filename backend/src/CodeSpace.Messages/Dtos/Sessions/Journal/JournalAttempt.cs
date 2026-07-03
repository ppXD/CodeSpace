using CodeSpace.Messages.Enums;

namespace CodeSpace.Messages.Dtos.Sessions.Journal;

/// <summary>
/// One attempt of a turn — a rerun/replay of the same user message. The journal exposes the whole ladder so the frontend
/// can render the attempt PAGER (◀ attempt 2 / 3 ▶) and the lineage: which attempt reran, from where, and how each ended.
/// A turn that was never reran has a single attempt (or none — the frontend shows a pager only when there's more than one).
/// The FOCUSED attempt is the one currently walked into the turn's steps; deep-linking a prior attempt's run focuses it.
/// </summary>
public sealed record JournalAttempt
{
    /// <summary>1-based ordinal in the ladder — the "attempt N" the pager shows.</summary>
    public required int AttemptNumber { get; init; }

    /// <summary>The attempt's run — the frontend deep-links / re-navigates to focus it.</summary>
    public required Guid RunId { get; init; }

    /// <summary>How this attempt ended (or its live state) — its OWN status, not the turn's newest.</summary>
    public required WorkflowRunStatus Status { get; init; }

    /// <summary>When the attempt was enqueued — the pager's per-attempt timestamp + chronological order.</summary>
    public required DateTimeOffset At { get; init; }

    /// <summary>What KIND of attempt it was — the rerun source (a full rerun, a replay, a rerun-from-node). The original attempt carries the run's own source type.</summary>
    public required string SourceType { get; init; }

    /// <summary>The node this attempt reran FROM, when it was a rerun-from-node (the fork point the lineage names — "reran from step X"). Null for a full rerun / the original attempt.</summary>
    public string? RerunFromNodeId { get; init; }

    /// <summary>Whether this is the newest attempt (the one the turn shows by default). Exactly one is latest.</summary>
    public required bool IsLatest { get; init; }

    /// <summary>Whether this attempt is the one CURRENTLY focused (walked into the turn's steps). Set only on the focused turn; false on a collapsed turn's ladder.</summary>
    public bool Focused { get; init; }
}
