namespace CodeSpace.Core.Services.Agents.Mcp;

/// <summary>
/// The per-run connect seam for the MCP endpoint: maps a run id → the CLIENT end of its in-process channel while the
/// run's endpoint is open. The executor <see cref="Register"/>s a run's client-connect on open and <see cref="Remove"/>s
/// it on dispose; a consumer (today the integration test, later the <c>codespace mcp</c> proxy given the run's id/token)
/// reaches its run's endpoint via <see cref="TryConnect"/>. Keyed by run id so a run can ONLY reach its own channel —
/// the same isolation a per-run UDS socket path gives later, which swaps in behind this seam without touching callers.
/// </summary>
public interface IAgentMcpConnectRegistry
{
    /// <summary>Record the client-connect factory for an open run's endpoint. Overwrites any stale entry for the run id.</summary>
    void Register(Guid runId, IAgentMcpClientConnect connect);

    /// <summary>Drop a run's entry on endpoint dispose. Idempotent — removing an absent run id is a no-op.</summary>
    void Remove(Guid runId);

    /// <summary>Resolve a run's client-connect; false when no endpoint is open for that run id (closed / never opened).</summary>
    bool TryConnect(Guid runId, out IAgentMcpClientConnect connect);
}

/// <summary>The client end a consumer talks to over a run's MCP channel — a newline-delimited JSON-RPC reader/writer pair.</summary>
public interface IAgentMcpClientConnect
{
    /// <summary>Writes JSON-RPC requests to the run's endpoint.</summary>
    TextWriter Writer { get; }

    /// <summary>Reads JSON-RPC responses from the run's endpoint.</summary>
    TextReader Reader { get; }
}
