using System.Text.Json;
using CodeSpace.Core.Services.Agents.Sandbox;
using CodeSpace.Messages.Agents;

namespace CodeSpace.Core.Services.Agents.Mcp;

/// <summary>
/// An <see cref="IMcpRequestHandler"/> decorator that HOLDS the run's progress lease
/// (<see cref="AgentProgressSignal.PlatformRequest"/>) for as long as the wrapped handler is working on one request,
/// then releases it the instant the request answers.
///
/// <para>This is the fix for the collision the no-progress watchdog used to lose: a side-effecting tool call parks on a
/// human approval and BLOCKS its <c>tools/call</c> for up to <see cref="McpRequestHandler.DefaultApprovalBoundSeconds"/>
/// — the same 600s as the default no-progress window — while emitting nothing to the run's spool. The watchdog saw
/// silence and killed a run whose approval could still land. A run waiting on an authorised human decision is making
/// progress by definition, so the wait RENEWS the lease instead of racing it.</para>
///
/// <para>It renews rather than suspends indefinitely, and what it can defer is bounded from the READING side, not from
/// here: the hold cannot outlive the request (the approval / decision bound returns a pending ticket, after which
/// nothing renews), the run's execution wall deadline is checked BEFORE the lease is consulted, and a run with NO wall
/// deadline has its lease refused outright by the observer — a pending ticket instructs the agent to re-issue the call
/// to keep waiting, so successive holds chain, and only a wall deadline makes an unbounded chain safe. This decorator
/// therefore renews unconditionally and stays honest about one thing only: a request is in flight. A decorator rather
/// than a change inside <see cref="McpRequestHandler"/> because that is exactly what this seam observes; the protocol
/// core stays pure.</para>
/// </summary>
public sealed class ProgressLeaseRenewingHandler : IMcpRequestHandler
{
    private readonly IMcpRequestHandler _inner;
    private readonly AgentProgressLease _lease;

    public ProgressLeaseRenewingHandler(IMcpRequestHandler inner, AgentProgressLease lease)
    {
        _inner = inner;
        _lease = lease;
    }

    public Task<JsonElement?> HandleAsync(JsonElement request, CancellationToken cancellationToken) =>
        _lease.HoldAsync(AgentProgressSignal.PlatformRequest, () => _inner.HandleAsync(request, cancellationToken), cancellationToken);
}
