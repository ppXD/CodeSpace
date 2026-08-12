namespace CodeSpace.Core.Services.Agents.Mcp;

/// <summary>
/// P0-B2: the endpoint-lifetime observation cell the per-connection handlers increment — one instance per
/// <see cref="AgentMcpEndpoint"/>, shared across concurrent connections (Interlocked). It records the two facts
/// nothing durable captured before: whether an authenticated client actually served the MCP <c>initialize</c>
/// handshake, and how many <c>tools/call</c> requests the in-process server dispatched — including the read-only
/// calls the governance ledger never sees.
/// </summary>
public sealed class McpFabricCounters
{
    private int _handshakes;
    private int _toolCalls;

    public void MarkHandshake() => Interlocked.Increment(ref _handshakes);

    public void MarkToolCall() => Interlocked.Increment(ref _toolCalls);

    public int Handshakes => Volatile.Read(ref _handshakes);

    public int ToolCalls => Volatile.Read(ref _toolCalls);
}
