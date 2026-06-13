using System.Collections.Concurrent;
using CodeSpace.Core.DependencyInjection;

namespace CodeSpace.Core.Services.Agents.Mcp;

/// <summary>
/// In-process implementation of <see cref="IAgentMcpConnectRegistry"/> — a thread-safe run-id → client-connect map.
/// A DI SINGLETON (one map shared across the backend) so a consumer resolving it sees the entry the executor's
/// dedicated MCP scope registered, even though they run on different scopes. The map holds only OPEN endpoints:
/// the executor removes a run on endpoint dispose, so a closed/never-opened run resolves to nothing (fail-closed).
/// </summary>
public sealed class AgentMcpConnectRegistry : IAgentMcpConnectRegistry, ISingletonDependency
{
    private readonly ConcurrentDictionary<Guid, IAgentMcpClientConnect> _byRun = new();

    public void Register(Guid runId, IAgentMcpClientConnect connect) => _byRun[runId] = connect;

    public void Remove(Guid runId) => _byRun.TryRemove(runId, out _);

    public bool TryConnect(Guid runId, out IAgentMcpClientConnect connect) => _byRun.TryGetValue(runId, out connect!);
}
