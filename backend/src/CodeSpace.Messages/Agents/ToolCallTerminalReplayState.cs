namespace CodeSpace.Messages.Agents;

/// <summary>
/// The exact terminal wire authority for replay after a terminal-record CAS is lost. Primitives only: the focused
/// read needs status plus the already-redacted result/error and deliberately carries no approval, decision,
/// idempotency or tool metadata from the execution ledger.
/// </summary>
public sealed record ToolCallTerminalReplayState
{
    public required ToolCallLedgerStatus Status { get; init; }

    public string? ResultJson { get; init; }

    public string? Error { get; init; }
}
