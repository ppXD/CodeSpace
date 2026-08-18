using CodeSpace.Messages.Enums;

namespace CodeSpace.Messages.Workflows;

/// <summary>
/// The COMPLETE terminal a run's arbitrated compare-and-swap writes: the four values that must land as one row
/// transition or not at all. They travel together because splitting them is exactly how the outputs went missing —
/// status, error and outcome rode the <c>ExecuteUpdate</c> while the outputs were left on the tracked entity, which
/// an <c>ExecuteUpdate</c> never flushes, so a run that succeeded under the strictest completion regime persisted
/// no declared outputs unless something unrelated in the same scope happened to save first.
/// </summary>
public sealed record ArbitratedTerminal
{
    /// <summary>The terminal status the arbitration settled on.</summary>
    public required WorkflowRunStatus Status { get; init; }

    /// <summary>The operator-facing failure text, or null on a success.</summary>
    public string? Error { get; init; }

    /// <summary>The completion outcome the contract derived, or null when none was derived.</summary>
    public string? Outcome { get; init; }

    /// <summary>The run's declared outputs, serialized. Never null — a run that declared none serializes an empty object, so the column is written on every terminal rather than left at whatever the row happened to hold.</summary>
    public required string OutputsJson { get; init; }
}
