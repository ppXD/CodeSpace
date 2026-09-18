using CodeSpace.Messages.Enums;

namespace CodeSpace.Messages.Dtos.Workflows;

/// <summary>
/// The result of an operator run-cancel. <see cref="Cancelled"/> is true when this call won the
/// non-terminal → <c>Cancelled</c> CAS and tore the run down; false when the run was already
/// terminal (the idempotent no-op) — <see cref="Status"/> then carries the run's existing terminal
/// state so the caller can show "already finished" rather than a spurious success. <see cref="AgentRunsCancelled"/>
/// is how many of the run's branch agent runs the kill-wave was HANDED (Queued + Running at the flip).
/// </summary>
public sealed record CancelRunOutcome
{
    public required bool Cancelled { get; init; }

    /// <summary>The run's status AFTER the call — <c>Cancelled</c> on a successful flip, or the pre-existing terminal status on an already-terminal no-op.</summary>
    public required WorkflowRunStatus Status { get; init; }

    /// <summary>
    /// Count of the run's branch agent runs that were still Queued or Running when the cancel flipped, i.e. what the
    /// kill-wave was handed. 0 when the run had no branch agents or was already terminal.
    ///
    /// <para>Read at the flip rather than tallied by the wave, because the wave runs after this call's transaction
    /// commits and cannot report back into this response. It can therefore exceed what was actually flipped, by the
    /// agents a worker legitimately landed terminal in the same instant; the wave logs its own tally.</para>
    /// </summary>
    public required int AgentRunsCancelled { get; init; }
}
