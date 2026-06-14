using CodeSpace.Messages.Agents;

namespace CodeSpace.Core.Services.Agents.Mcp;

/// <summary>
/// A per-(ledger row) in-memory rendezvous between a tool-call handler blocked awaiting an approval decision (item D2)
/// and the resolver that records the human's verdict. The blocked handler <see cref="Register"/>s its ledger id and
/// awaits the returned <see cref="IToolApprovalWaiter.Completion"/>; the resolver <see cref="TrySignal"/>s the same id
/// to wake it. This is a pure LATENCY fast-path: the durable <c>ToolCallLedger</c> row is the authority — a process
/// restart that drops every in-memory waiter loses no decision, the handler re-reads the row and resumes from it.
/// </summary>
public interface IToolApprovalWaiterRegistry
{
    /// <summary>Register a waiter for a ledger row; the caller awaits the handle's <see cref="IToolApprovalWaiter.Completion"/>. Overwrites any stale waiter for the id (mirrors <c>AgentMcpConnectRegistry.Register</c>).</summary>
    IToolApprovalWaiter Register(Guid ledgerId);

    /// <summary>Wake the waiter for a ledger row with <paramref name="outcome"/>; returns whether a live waiter was present (false when none — the common D0 case, harmless).</summary>
    bool TrySignal(Guid ledgerId, ToolApprovalOutcome outcome);

    /// <summary>Drop a ledger row's waiter. Idempotent — removing an absent id is a no-op.</summary>
    void Remove(Guid ledgerId);
}

/// <summary>A registered waiter's handle: await <see cref="Completion"/> for the approval verdict.</summary>
public interface IToolApprovalWaiter
{
    /// <summary>Completes with the verdict when the resolver (or the reaper) signals this ledger row.</summary>
    Task<ToolApprovalOutcome> Completion { get; }
}
