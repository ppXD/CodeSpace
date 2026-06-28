using CodeSpace.Messages.Enums;

namespace CodeSpace.Messages.Dtos.Sessions;

/// <summary>
/// One row of the sessions index — a work thread, rich enough for a Claude/Codex-style sidebar without loading the
/// conversation. <see cref="LastActivityAt"/> is the MRU sort key (newest first); the live signals
/// (<see cref="LatestRunId"/> / <see cref="LatestRunStatus"/> / <see cref="HasPendingDecision"/>) let the row show a
/// status dot + deep-link to the Run Room without a second fetch. Enums serialise as strings (the global JSON config).
/// </summary>
public sealed record SessionSummary
{
    public required Guid Id { get; init; }

    public required string Title { get; init; }

    /// <summary>The thread's product semantic — what TYPE OF WORK it solves (Task / Pr / Issue / Workflow / Schedule / Custom).</summary>
    public required WorkSessionKind Kind { get; init; }

    /// <summary>Lifecycle ONLY (Open / Archived) — never a run status.</summary>
    public required WorkSessionStatus Status { get; init; }

    /// <summary>How many top-level turns the thread has (the session's atomic turn counter).</summary>
    public required int TurnCount { get; init; }

    public required DateTimeOffset CreatedDate { get; init; }

    /// <summary>The thread's most-recent activity instant — the MRU ordering key.</summary>
    public required DateTimeOffset LastActivityAt { get; init; }

    /// <summary>The thread's most-recent run — deep-links the row to the Run Room. Null for a session with no runs.</summary>
    public Guid? LatestRunId { get; init; }

    /// <summary>The latest run's lifecycle status (a live badge). Null when there is no run.</summary>
    public WorkflowRunStatus? LatestRunStatus { get; init; }

    /// <summary>The latest run's projection mode (single-agent / supervisor / …). Null for an authored / no-run thread.</summary>
    public string? LatestProjectionKind { get; init; }

    /// <summary>True when any run in the thread is parked on a pending decision — the "needs you" badge.</summary>
    public required bool HasPendingDecision { get; init; }
}
