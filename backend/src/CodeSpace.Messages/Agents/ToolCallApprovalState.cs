namespace CodeSpace.Messages.Agents;

/// <summary>
/// The focused, team-scoped snapshot a blocked handler call re-reads AFTER its bounded approval wait wakes — the
/// durable <c>ToolCallLedger</c> row is the authority (the in-memory waiter TCS is only a latency fast-path), so the
/// handler always re-reads this to decide the outcome (a data noun, Rule 18.1 — primitives only, never the Core
/// entity). <see cref="ApprovedAt"/> distinguishes a still-pending <c>AwaitingApproval</c> row (<c>null</c> → emit the
/// pending-ticket) from an approved-but-not-yet-executed one (<c>non-null</c> → run the side effect once).
/// </summary>
public sealed record ToolCallApprovalState
{
    public required ToolCallLedgerStatus Status { get; init; }

    /// <summary>Non-null once a human approved (the row still sits at AwaitingApproval until the handler flips it terminal). Null = no decision yet.</summary>
    public DateTimeOffset? ApprovedAt { get; init; }

    /// <summary>The already-redacted terminal result content on a Succeeded row (null otherwise).</summary>
    public string? ResultJson { get; init; }

    /// <summary>The already-redacted terminal error/refusal reason on a Failed/Expired row (null otherwise).</summary>
    public string? Error { get; init; }
}
