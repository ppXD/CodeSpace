using CodeSpace.Messages.Enums;

namespace CodeSpace.Messages.Dtos.Sessions;

/// <summary>
/// One work thread as a conversation — the session's identity + rolling summary + its ordered TURNS. Each turn is one
/// top-level run (the user's message = the run's launch goal; the reply = the run's result), with any reruns of that
/// turn nested as <see cref="SessionTurn.Attempts"/> rather than as separate turns. Entered by session id, or by any
/// run id (the run-anchored entry sets <see cref="AnchorTurnIndex"/> to the turn that run belongs to, so the UI scrolls
/// to it).
/// </summary>
public sealed record SessionDetail
{
    public required Guid Id { get; init; }
    public required string Title { get; init; }
    public required WorkSessionKind Kind { get; init; }
    public required WorkSessionStatus Status { get; init; }
    public required DateTimeOffset CreatedDate { get; init; }

    /// <summary>The rolling LLM digest of older turns (those scrolled out of the recent window). Null for a short thread.</summary>
    public string? Summary { get; init; }

    /// <summary>The highest turn the <see cref="Summary"/> already covers. Null when there is no summary.</summary>
    public int? SummaryThroughTurnIndex { get; init; }

    /// <summary>When entered by a run id, the turn that run belongs to (so the UI scrolls to it). Null when entered by session id, or when the run is not a turn in this thread.</summary>
    public int? AnchorTurnIndex { get; init; }

    /// <summary>The thread's turns, oldest first (by turn ordinal).</summary>
    public required IReadOnlyList<SessionTurn> Turns { get; init; }
}

/// <summary>
/// One turn of a thread — a top-level run shown like a chat exchange. <see cref="UserMessage"/> is the user's words
/// (the run's launch goal); the run's outcome (<see cref="Result"/> / <see cref="ProducedBranch"/> / status) is the
/// reply. A reran turn surfaces its NEWEST attempt's outcome here, with the full ladder in <see cref="Attempts"/>.
/// </summary>
public sealed record SessionTurn
{
    public required int TurnIndex { get; init; }

    /// <summary>The turn's stable identity — the original run of the lineage (<c>RootRunId ?? Id</c>).</summary>
    public required Guid TurnRunId { get; init; }

    /// <summary>The run to show + deep-link — the newest attempt of the turn (equals <see cref="TurnRunId"/> when never reran).</summary>
    public required Guid RunId { get; init; }

    /// <summary>The user's message for this turn — the run's launch goal. Null if the goal payload is absent / malformed.</summary>
    public string? UserMessage { get; init; }

    public required WorkflowRunStatus RunStatus { get; init; }

    /// <summary>The turn's projection mode (single-agent / plan-map-synth / supervisor / …). Null for an authored run.</summary>
    public string? ProjectionKind { get; init; }

    /// <summary>The turn's result text — the assistant's reply, read generically across projection shapes (clipped).</summary>
    public string? Result { get; init; }

    /// <summary>The single produced branch (single-repo turn). Null for a multi-repo turn (see <see cref="RepositoryResults"/>) or an analysis-only turn.</summary>
    public string? ProducedBranch { get; init; }

    /// <summary>Per-repo produced branches (multi-repo turn). Null for a single-repo / analysis-only turn.</summary>
    public IReadOnlyList<SessionTurnRepoResult>? RepositoryResults { get; init; }

    /// <summary>True when this turn's latest run is parked on a pending decision — the per-turn "needs you" marker.</summary>
    public required bool HasPendingDecision { get; init; }

    /// <summary>When the user sent this turn (the turn run's creation instant).</summary>
    public required DateTimeOffset CreatedDate { get; init; }

    public DateTimeOffset? StartedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public string? Error { get; init; }

    /// <summary>How many runs make up this turn (1 = never reran).</summary>
    public required int AttemptCount { get; init; }

    /// <summary>The attempt ladder, oldest first, when the turn was reran (<see cref="AttemptCount"/> &gt; 1); null otherwise.</summary>
    public IReadOnlyList<SessionTurnAttempt>? Attempts { get; init; }
}

/// <summary>One attempt of a turn — a fork (replay / rerun) of the turn's run. Mirrors the run-attempt ladder shape.</summary>
public sealed record SessionTurnAttempt
{
    public required Guid RunId { get; init; }

    /// <summary>1-based ordinal within the turn (1 = the original turn run).</summary>
    public required int AttemptNumber { get; init; }

    public required WorkflowRunStatus Status { get; init; }

    /// <summary>This attempt's own source — "replay" / "rerun" for a fork, the original's source for attempt 1.</summary>
    public required string SourceType { get; init; }

    /// <summary>The node this attempt re-ran from; null for the original or a whole-run replay.</summary>
    public string? RerunFromNodeId { get; init; }

    public required DateTimeOffset CreatedDate { get; init; }

    /// <summary>True for the newest attempt — the one the turn shows by default.</summary>
    public required bool IsLatest { get; init; }

    /// <summary>This attempt's terminal failure reason, when it failed — so a reader (or the next attempt's fork note) sees WHY it was reran. Null when it succeeded / is in flight.</summary>
    public string? Error { get; init; }
}

/// <summary>One repo's produced branch within a multi-repo turn.</summary>
public sealed record SessionTurnRepoResult
{
    public required Guid RepositoryId { get; init; }
    public required string ProducedBranch { get; init; }
}
