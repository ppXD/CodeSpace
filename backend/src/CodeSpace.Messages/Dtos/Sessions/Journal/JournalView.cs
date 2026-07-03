using CodeSpace.Messages.Enums;

namespace CodeSpace.Messages.Dtos.Sessions.Journal;

/// <summary>
/// The backend-authored SESSION JOURNAL — a work thread as a stream of turns, each a user message + the AI's reply
/// rendered as a CHRONOLOGICAL journal of steps (a decision, a tool call, a file edit…) in true execution order. The
/// frontend renders it structurally (turns → steps by <see cref="JournalStep.Kind"/>) and owns no copy / order / status;
/// the backend does. Entered by session id, or by any run id (which anchors <see cref="AnchorTurnIndex"/> to that run's
/// turn so the UI scrolls to it). The heavy per-step walk runs ONLY for the focused turn; the rest are light cards.
/// </summary>
public sealed record JournalView
{
    public required Guid SessionId { get; init; }
    public required string Title { get; init; }
    public required WorkSessionKind Kind { get; init; }
    public required WorkSessionStatus Status { get; init; }

    /// <summary>The delta HEAD — the focused turn's newest step cursor, which the client echoes back as <c>?since=</c> for the next delta. Empty when the focused turn has no steps yet.</summary>
    public required string Cursor { get; init; }

    /// <summary>When entered by a run id, the turn index to scroll to (the turn that run belongs to). Null when entered by session id.</summary>
    public int? AnchorTurnIndex { get; init; }

    /// <summary>The thread's turns, oldest first.</summary>
    public required IReadOnlyList<JournalTurn> Turns { get; init; }
}

/// <summary>
/// One turn of the journal — a top-level run shown like a chat exchange: the user's message (the run's launch goal) and
/// the AI's reply. The FOCUSED turn carries its full chronological <see cref="Steps"/> (the walk); every other turn is a
/// light card (<see cref="Steps"/> empty, its <see cref="Summary"/> + status the whole story — refocus = a cheap
/// re-navigation to that run). A reran turn surfaces its newest attempt here.
/// </summary>
public sealed record JournalTurn
{
    public required int TurnIndex { get; init; }

    /// <summary>The turn's stable identity — the original run of the lineage.</summary>
    public required Guid TurnRunId { get; init; }

    /// <summary>The run shown + walked — the newest attempt of the turn (equals <see cref="TurnRunId"/> when never reran).</summary>
    public required Guid RunId { get; init; }

    public required WorkflowRunStatus Status { get; init; }

    /// <summary>The user's message for this turn — the run's launch goal. Null if absent / malformed.</summary>
    public string? UserMessage { get; init; }

    /// <summary>The turn's result headline (the AI's reply lead) — shown above the steps on the focused turn, and as the card body on a collapsed one. Null while in-progress.</summary>
    public string? Summary { get; init; }

    /// <summary>When the user sent this turn.</summary>
    public DateTimeOffset? At { get; init; }

    /// <summary>The turn's wall-clock — final once terminal, else live elapsed at projection time. Null before it starts.</summary>
    public long? DurationMs { get; init; }

    /// <summary>True when this turn is the FOCUSED one (walked into <see cref="Steps"/>); false for a light card.</summary>
    public bool Focused { get; init; }

    /// <summary>The turn's chronological journal steps (the walk) — populated ONLY for the focused turn; empty for a collapsed card. In a <c>?since=</c> DELTA response this is TRIMMED to the steps after the cursor; the full total is <see cref="StepCount"/>.</summary>
    public required IReadOnlyList<JournalStep> Steps { get; init; }

    /// <summary>
    /// The focused turn's TOTAL step count — BEFORE any <c>?since=</c> trim. The delta self-heal signal: after applying a
    /// delta, a client whose accumulated step count is LESS than this saw fewer steps than exist, so a step landed BELOW
    /// its cursor (an out-of-order backfill the append-only delta can't carry) → it re-fetches the full journal. 0 for a
    /// collapsed turn (never walked).
    /// </summary>
    public int StepCount { get; init; }
}
