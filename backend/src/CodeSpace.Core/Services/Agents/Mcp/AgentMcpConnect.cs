namespace CodeSpace.Core.Services.Agents.Mcp;

/// <summary>
/// The connect descriptor a consumer resolves from <see cref="IAgentMcpConnectRegistry"/>: the run's UDS socket path
/// plus the per-run token it must present as the connection's first line. A plain value carrier — the endpoint owns
/// the listener; this just hands a reachable consumer the path + token to connect with.
/// </summary>
public sealed class AgentMcpConnect : IAgentMcpClientConnect
{
    public AgentMcpConnect(string socketPath, string token)
    {
        SocketPath = socketPath;
        Token = token;
    }

    public string SocketPath { get; }

    public string Token { get; }
}
