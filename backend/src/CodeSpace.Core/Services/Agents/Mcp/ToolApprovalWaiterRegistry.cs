using System.Collections.Concurrent;
using CodeSpace.Core.DependencyInjection;
using CodeSpace.Messages.Agents;

namespace CodeSpace.Core.Services.Agents.Mcp;

/// <summary>
/// In-process implementation of <see cref="IToolApprovalWaiterRegistry"/> — a thread-safe ledger-id → waiter map. A DI
/// SINGLETON (one map shared across the backend) so the resolver (one scope) wakes the waiter a blocked handler call
/// registered on another scope, mirroring <see cref="AgentMcpConnectRegistry"/>. A pure latency fast-path over the
/// durable <c>ToolCallLedger</c> row (the authority) — a restart that drops the map loses no decision.
/// </summary>
public sealed class ToolApprovalWaiterRegistry : IToolApprovalWaiterRegistry, ISingletonDependency
{
    private readonly ConcurrentDictionary<Guid, Waiter> _byLedger = new();

    public IToolApprovalWaiter Register(Guid ledgerId)
    {
        var waiter = new Waiter();

        // Last-writer-wins per ledger id. Safe because this is purely a wake latency fast-path: a clobbered signal just
        // means the other blocked call wakes on its bound instead, and both re-read the durable row + race the
        // single-winner execution-claim CAS — exactly one runs the side effect (see IToolApprovalWaiterRegistry.Register).
        _byLedger[ledgerId] = waiter;

        return waiter;
    }

    public bool TrySignal(Guid ledgerId, ToolApprovalOutcome outcome) =>
        _byLedger.TryGetValue(ledgerId, out var waiter) && waiter.TrySet(outcome);

    public void Remove(Guid ledgerId) => _byLedger.TryRemove(ledgerId, out _);

    private sealed class Waiter : IToolApprovalWaiter
    {
        // RunContinuationsAsynchronously so completing the TCS from the resolver never inlines the awaiting handler's
        // continuation onto the resolver's thread (it would hold the resolver's DB scope / transaction).
        private readonly TaskCompletionSource<ToolApprovalOutcome> _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<ToolApprovalOutcome> Completion => _tcs.Task;

        public bool TrySet(ToolApprovalOutcome outcome) => _tcs.TrySetResult(outcome);
    }
}
