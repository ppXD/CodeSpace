using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Agents.Mcp;
using CodeSpace.Core.Services.Agents.Tools;
using CodeSpace.Messages.Agents;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace CodeSpace.UnitTests.Agents;

/// <summary>
/// Pins the per-run UDS MCP endpoint (<see cref="AgentMcpEndpoint"/>): the enabling env-var literal (Rule 8), and that
/// <see cref="AgentMcpEndpoint.DisposeAsync"/> is IDEMPOTENT and NEVER throws — after a clean connection end AND after
/// a cancel with no connection. Dispose drops the run from the connect registry, disposes the dedicated scope, AND
/// unlinks the socket file. A connection that presents a WRONG token is closed without ever serving JSON-RPC. Tier 🟢:
/// real production endpoint over a real <c>AF_UNIX</c> socket in a temp dir. Skips on a host without UDS support.
/// </summary>
[Trait("Category", "Unit")]
public class AgentMcpEndpointTests
{
    [Fact]
    public void Enabling_env_var_literal_is_pinned()
    {
        // Renaming this silently turns the feature off for an operator who enabled it via env (Rule 8). The wiring is
        // FOLDED into this one flag: a non-null endpoint (this flag on + the bind succeeded) is the only gate for
        // writing the declaration — there is no second wiring flag.
        AgentRunExecutor.McpEndpointEnabledEnvVar.ShouldBe("CODESPACE_AGENT_MCP_ENDPOINT_ENABLED");
    }

    [Fact]
    public void McpRunToken_survives_the_SandboxHandle_json_round_trip()
    {
        // The token rides the persisted handle so a re-attach after a worker tear-down re-opens the endpoint with the
        // SAME token the agent's declaration file holds. A silent null here would lock the still-running agent out — so
        // pin that it survives serialize→deserialize through the exact options the executor persists with.
        var handle = new SandboxHandle { Kind = "local", ProcessId = 1, SpoolDirectory = "/tmp/s", Deadline = DateTimeOffset.UtcNow, McpRunToken = "tok" };

        var roundTripped = JsonSerializer.Deserialize<SandboxHandle>(JsonSerializer.Serialize(handle, AgentJson.Options), AgentJson.Options);

        roundTripped!.McpRunToken.ShouldBe("tok", customMessage: "the MCP run token must survive the handle's JSON round-trip — a null would lock the agent out on re-attach");
    }

    [Fact]
    public async Task DisposeAsync_after_a_clean_connection_end_is_idempotent_unlinks_the_socket_and_drops_the_run()
    {
        if (!Socket.OSSupportsUnixDomainSockets) return;

        using var dir = new TempDir();
        var socketPath = Path.Combine(dir.Path, "mcp.sock");
        var connects = new AgentMcpConnectRegistry();
        var scope = new TrackingScope();
        var runId = Guid.NewGuid();
        const string token = "the-token";

        var endpoint = new AgentMcpEndpoint(runId, new EmptyRegistry(), AgentAutonomyLevel.Confined, Guid.NewGuid(), socketPath, token, connects, scope, CancellationToken.None, NullLogger.Instance);

        connects.TryConnect(runId, out var connect).ShouldBeTrue(customMessage: "open endpoint must be reachable through the connect registry");
        connect.SocketPath.ShouldBe(socketPath);
        connect.Token.ShouldBe(token);
        File.Exists(socketPath).ShouldBeTrue(customMessage: "the listener must have bound the socket file");

        // Connect, authenticate, then close the write end → the server sees EOF → the per-connection pump returns cleanly.
        using (var client = await ConnectAsync(socketPath))
        {
            await SendLineAsync(client, token);
            await Task.Delay(50);
            client.Shutdown(SocketShutdown.Both);
        }
        await Task.Delay(50);

        await Should.NotThrowAsync(async () => await endpoint.DisposeAsync());
        await Should.NotThrowAsync(async () => await endpoint.DisposeAsync());   // idempotent: a second dispose is a no-op

        connects.TryConnect(runId, out _).ShouldBeFalse(customMessage: "dispose must drop the run from the connect registry");
        scope.Disposed.ShouldBeTrue(customMessage: "dispose must release the dedicated DI scope");
        File.Exists(socketPath).ShouldBeFalse(customMessage: "dispose must unlink the socket file");
    }

    [Fact]
    public async Task DisposeAsync_after_cancel_with_no_connection_is_idempotent_and_unlinks_the_socket()
    {
        if (!Socket.OSSupportsUnixDomainSockets) return;

        using var dir = new TempDir();
        var socketPath = Path.Combine(dir.Path, "mcp.sock");
        var connects = new AgentMcpConnectRegistry();
        var scope = new TrackingScope();
        var runId = Guid.NewGuid();

        // No connection: the accept loop is blocked in AcceptAsync; DisposeAsync cancels + disposes the listener.
        var endpoint = new AgentMcpEndpoint(runId, new EmptyRegistry(), AgentAutonomyLevel.Standard, Guid.NewGuid(), socketPath, "tok", connects, scope, CancellationToken.None, NullLogger.Instance);

        await Should.NotThrowAsync(async () => await endpoint.DisposeAsync());
        await Should.NotThrowAsync(async () => await endpoint.DisposeAsync());

        connects.TryConnect(runId, out _).ShouldBeFalse();
        scope.Disposed.ShouldBeTrue();
        File.Exists(socketPath).ShouldBeFalse(customMessage: "dispose must unlink the socket file even with no connection");
    }

    [Fact]
    public async Task A_connection_presenting_a_wrong_token_is_closed_without_serving()
    {
        if (!Socket.OSSupportsUnixDomainSockets) return;

        using var dir = new TempDir();
        var socketPath = Path.Combine(dir.Path, "mcp.sock");
        var connects = new AgentMcpConnectRegistry();
        var scope = new TrackingScope();
        var runId = Guid.NewGuid();

        await using var endpoint = new AgentMcpEndpoint(runId, new EmptyRegistry(), AgentAutonomyLevel.Standard, Guid.NewGuid(), socketPath, "the-real-token", connects, scope, CancellationToken.None, NullLogger.Instance);

        using var client = await ConnectAsync(socketPath);
        await SendLineAsync(client, "the-WRONG-token");

        // Send a real JSON-RPC request AFTER the bad token; the endpoint must never reply — the read returns EOF.
        await SendLineAsync(client, "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\"}");

        await using var net = new NetworkStream(client, ownsSocket: false);
        using var reader = new StreamReader(net, Encoding.UTF8);

        var response = await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5));
        response.ShouldBeNull(customMessage: "a wrong token must close the connection (EOF) before any JSON-RPC reply");
    }

    [Fact]
    public async Task A_connection_that_closes_before_sending_any_line_serves_no_json_rpc_and_disposes_cleanly()
    {
        if (OperatingSystem.IsWindows() || !Socket.OSSupportsUnixDomainSockets) return;

        using var dir = new TempDir();
        var socketPath = Path.Combine(dir.Path, "mcp.sock");
        var connects = new AgentMcpConnectRegistry();
        var scope = new TrackingScope();
        var runId = Guid.NewGuid();

        await using var endpoint = new AgentMcpEndpoint(runId, new EmptyRegistry(), AgentAutonomyLevel.Standard, Guid.NewGuid(), socketPath, "the-token", connects, scope, CancellationToken.None, NullLogger.Instance);

        // Connect then immediately close WITHOUT sending the token line → the endpoint reads EOF before any token,
        // fails closed (silent close), and never serves JSON-RPC. Dispose must still be clean.
        using (var client = await ConnectAsync(socketPath))
        {
            client.Shutdown(SocketShutdown.Both);
        }
        await Task.Delay(50);

        await Should.NotThrowAsync(async () => await endpoint.DisposeAsync());
    }

    [Fact]
    public async Task The_bound_socket_is_restricted_to_owner_only_0600()
    {
        if (OperatingSystem.IsWindows() || !Socket.OSSupportsUnixDomainSockets) return;

        using var dir = new TempDir();
        var socketPath = Path.Combine(dir.Path, "mcp.sock");
        var connects = new AgentMcpConnectRegistry();
        var scope = new TrackingScope();
        var runId = Guid.NewGuid();

        await using var endpoint = new AgentMcpEndpoint(runId, new EmptyRegistry(), AgentAutonomyLevel.Standard, Guid.NewGuid(), socketPath, "tok", connects, scope, CancellationToken.None, NullLogger.Instance);

        // Real OS state: the socket inode must be 0600 so no other local user can connect to the run's endpoint.
        File.GetUnixFileMode(socketPath).ShouldBe(UnixFileMode.UserRead | UnixFileMode.UserWrite,
            customMessage: "the per-run socket must be owner-only (0600) — another local user must not be able to connect");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static async Task<Socket> ConnectAsync(string socketPath)
    {
        var client = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        await client.ConnectAsync(new UnixDomainSocketEndPoint(socketPath));
        return client;
    }

    private static async Task SendLineAsync(Socket socket, string line)
    {
        var bytes = Encoding.UTF8.GetBytes(line + "\n");
        await socket.SendAsync(bytes, SocketFlags.None);
    }

    private sealed class EmptyRegistry : IAgentToolRegistry
    {
        public IReadOnlyList<IAgentTool> All { get; } = Array.Empty<IAgentTool>();
        public IAgentTool? Resolve(string kind) => null;
    }

    private sealed class TrackingScope : IDisposable
    {
        public bool Disposed { get; private set; }
        public void Dispose() => Disposed = true;
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "cs-mcp-ep-" + Guid.NewGuid().ToString("N")[..12]);
        public TempDir() => Directory.CreateDirectory(Path);
        public void Dispose() { try { Directory.Delete(Path, recursive: true); } catch { /* best-effort */ } }
    }
}
