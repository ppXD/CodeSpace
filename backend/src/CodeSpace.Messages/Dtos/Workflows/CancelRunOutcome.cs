using CodeSpace.Messages.Enums;

namespace CodeSpace.Messages.Dtos.Workflows;

/// <summary>
/// The result of an operator run-cancel. <see cref="Cancelled"/> is true when this call won the
/// non-terminal → <c>Cancelled</c> CAS and tore the run down; false when the run was already
/// terminal (the idempotent no-op) — <see cref="Status"/> then carries the run's existing terminal
/// state so the caller can show "already finished" rather than a spurious success. <see cref="AgentRunsCancelled"/>
/// is how many of the run's branch agent runs the kill-wave flipped to Cancelled (Queued + Running).
/// </summary>
public sealed record CancelRunOutcome
{
    public required bool Cancelled { get; init; }

    /// <summary>The run's status AFTER the call — <c>Cancelled</c> on a successful flip, or the pre-existing terminal status on an already-terminal no-op.</summary>
    public required WorkflowRunStatus Status { get; init; }

    /// <summary>Count of the run's branch agent runs the kill-wave moved to Cancelled this call. 0 when the run had no branch agents or was already terminal.</summary>
    public required int AgentRunsCancelled { get; init; }
}
