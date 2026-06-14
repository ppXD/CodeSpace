namespace CodeSpace.Messages.Agents;

/// <summary>
/// The outcome of <c>IToolCallLedgerService.TryClaimAsync</c> — whether the caller won the right to run a
/// side-effecting tool call, or a prior/concurrent row already owns it. The arbiter is the unique
/// <c>(agent_run_id, idempotency_key)</c> index, so exactly one caller for a given key ever gets
/// <see cref="ToolCallClaimOutcome.Proceed"/>.
/// </summary>
public enum ToolCallClaimOutcome
{
    /// <summary>We INSERTed a fresh Pending row — the caller runs the side effect, then records the terminal.</summary>
    Proceed,

    /// <summary>A TERMINAL row for (run, key) already exists — return its stored result, do NOT re-run the side effect.</summary>
    Duplicate,

    /// <summary>A non-terminal row (Pending / AwaitingApproval) exists — a concurrent or prior-suspended call owns the key; the caller must NOT double-run.</summary>
    InFlight,
}

/// <summary>
/// The result of trying to claim the right to execute a side-effecting tool call (a data noun, Rule 18.1 — carries
/// only primitives, never the Core entity). On <see cref="ToolCallClaimOutcome.Proceed"/> the caller runs the side
/// effect under <see cref="LedgerId"/>; on <see cref="ToolCallClaimOutcome.Duplicate"/> it returns the prior result
/// (<see cref="PriorResultJson"/> / <see cref="PriorError"/>) WITHOUT re-running.
/// </summary>
public sealed record ToolCallClaim
{
    public required ToolCallClaimOutcome Outcome { get; init; }

    /// <summary>The ledger row this claim refers to (the freshly-inserted Pending row on Proceed, the existing row otherwise). Empty only on a degenerate no-row case (never produced by the service).</summary>
    public Guid LedgerId { get; init; }

    /// <summary>On Duplicate: the prior row's terminal status.</summary>
    public ToolCallLedgerStatus PriorStatus { get; init; }

    /// <summary>On a Duplicate success: the prior row's ALREADY-REDACTED result content (null on a duplicate failure).</summary>
    public string? PriorResultJson { get; init; }

    /// <summary>On a Duplicate failure/denial: the prior row's ALREADY-REDACTED error (null on a duplicate success).</summary>
    public string? PriorError { get; init; }

    public static ToolCallClaim Proceed(Guid ledgerId) => new() { Outcome = ToolCallClaimOutcome.Proceed, LedgerId = ledgerId };
    public static ToolCallClaim InFlight(Guid ledgerId) => new() { Outcome = ToolCallClaimOutcome.InFlight, LedgerId = ledgerId };
    public static ToolCallClaim Duplicate(Guid ledgerId, ToolCallLedgerStatus priorStatus, string? priorResultJson, string? priorError) =>
        new() { Outcome = ToolCallClaimOutcome.Duplicate, LedgerId = ledgerId, PriorStatus = priorStatus, PriorResultJson = priorResultJson, PriorError = priorError };
}
