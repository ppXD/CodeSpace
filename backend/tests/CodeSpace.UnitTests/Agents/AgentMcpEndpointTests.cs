using CodeSpace.Core.Services.Agents.Authority;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Agents.Mcp;
using CodeSpace.Core.Services.Agents.Tools;
using CodeSpace.Messages.Agents;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace CodeSpace.UnitTests.Agents;

/// <summary>
/// Pins the per-run UDS MCP endpoint (<see cref="AgentMcpEndpoint"/>): the enabling env-var literal (Rule 8), and that
/// <see cref="AgentMcpEndpoint.DisposeAsync"/> is IDEMPOTENT and NEVER throws — after a clean connection end AND after
/// a cancel with no connection. Dispose drops the run from the connect registry, disposes the dedicated scope, AND
/// unlinks the socket file. A connection that presents a WRONG token is closed without ever serving JSON-RPC. A tool that
/// times out on its own is answered as a tool error and the same connection keeps serving. Tier 🟢:
/// real production endpoint over a real <c>AF_UNIX</c> socket in a temp dir. Skips on a host without UDS support.
/// </summary>
[Trait("Category", "Unit")]
public class AgentMcpEndpointTests
{
    [Fact]
    public void The_full_tool_catalog_is_the_committed_default()
    {
        // The endpoint opens for every run; this decides whether it serves the whole fabric or only the read-only
        // slice. It used to be an environment flag, so the tool surface an agent saw depended on a deployment
        // variable. Committed now — a run narrows out of it explicitly via AgentTask.EnableMcpEndpoint.
        AgentRunExecutor.FullToolCatalogByDefault.ShouldBeTrue();
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

        var endpoint = new AgentMcpEndpoint(runId, new EmptyRegistry(), AgentAutonomyLevel.Confined, Guid.NewGuid(), SecretRedactor.None, socketPath, token, connects, scope, CancellationToken.None, NullLogger.Instance);

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

        (await RecordDisposeAsync(endpoint)).ShouldBeNull("dispose after a clean connection end must be quiet — a TaskCanceledException is the accept loop's own cancel escaping DisposeAsync, a TimeoutException a pump that never drained");
        (await RecordDisposeAsync(endpoint)).ShouldBeNull("idempotent: a second dispose is a no-op and must not throw either");

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
        var endpoint = new AgentMcpEndpoint(runId, new EmptyRegistry(), AgentAutonomyLevel.Standard, Guid.NewGuid(), SecretRedactor.None, socketPath, "tok", connects, scope, CancellationToken.None, NullLogger.Instance);

        (await RecordDisposeAsync(endpoint)).ShouldBeNull("dispose cancels the accept loop blocked in AcceptAsync — that cancel must end inside DisposeAsync, never reach the caller as a TaskCanceledException");
        (await RecordDisposeAsync(endpoint)).ShouldBeNull("idempotent: a second dispose is a no-op and must not throw either");

        connects.TryConnect(runId, out _).ShouldBeFalse();
        scope.Disposed.ShouldBeTrue();
        File.Exists(socketPath).ShouldBeFalse(customMessage: "dispose must unlink the socket file even with no connection");
    }

    [Fact]
    public async Task The_fabric_evidence_observes_the_handshake_and_tool_calls()
    {
        if (!Socket.OSSupportsUnixDomainSockets) return;

        using var dir = new TempDir();
        var socketPath = Path.Combine(dir.Path, "mcp.sock");
        var connects = new AgentMcpConnectRegistry();
        var scope = new TrackingScope();
        const string token = "the-token";

        var endpoint = new AgentMcpEndpoint(Guid.NewGuid(), new EmptyRegistry(), AgentAutonomyLevel.Standard, Guid.NewGuid(), SecretRedactor.None, socketPath, token, connects, scope, CancellationToken.None, NullLogger.Instance);

        endpoint.HandshakeObserved.ShouldBeFalse("nothing has connected yet");
        endpoint.ObservedToolCalls.ShouldBe(0);
        var digest = endpoint.EffectiveCatalogDigest();
        digest.ShouldNotBeNullOrEmpty();
        endpoint.EffectiveCatalogDigest().ShouldBe(digest, "the catalog digest is deterministic per (registry, autonomy, mode)");

        using (var client = await ConnectAsync(socketPath))
        {
            await SendLineAsync(client, token);
            await SendLineAsync(client, """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{}}""");
            await SendLineAsync(client, """{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"mcp__codespace__nope","arguments":{}}}""");
            await Task.Delay(200);
            client.Shutdown(SocketShutdown.Both);
        }
        await Task.Delay(100);

        endpoint.HandshakeObserved.ShouldBeTrue("an authenticated client served initialize — THE fabric-connected fact");
        endpoint.ObservedToolCalls.ShouldBe(1, "every dispatched tools/call counts, even one that resolves no tool — the call ARRIVED");

        await endpoint.DisposeAsync();
    }

    [Fact]
    public async Task An_unauthenticated_client_never_moves_the_fabric_evidence()
    {
        if (!Socket.OSSupportsUnixDomainSockets) return;

        using var dir = new TempDir();
        var socketPath = Path.Combine(dir.Path, "mcp.sock");
        var endpoint = new AgentMcpEndpoint(Guid.NewGuid(), new EmptyRegistry(), AgentAutonomyLevel.Standard, Guid.NewGuid(), SecretRedactor.None, socketPath, "right-token", new AgentMcpConnectRegistry(), new TrackingScope(), CancellationToken.None, NullLogger.Instance);

        using (var client = await ConnectAsync(socketPath))
        {
            await SendLineAsync(client, "wrong-token");
            await TrySendLineAsync(client, """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{}}""");
            await Task.Delay(150);
        }

        endpoint.HandshakeObserved.ShouldBeFalse("a rejected connection serves nothing — evidence must not count it");

        await endpoint.DisposeAsync();
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

        await using var endpoint = new AgentMcpEndpoint(runId, new EmptyRegistry(), AgentAutonomyLevel.Standard, Guid.NewGuid(), SecretRedactor.None, socketPath, "the-real-token", connects, scope, CancellationToken.None, NullLogger.Instance);

        using var client = await ConnectAsync(socketPath);
        // Capture the read side while the socket is known-connected. The endpoint is allowed to close as soon as it
        // reads the bad token; constructing NetworkStream after the sends races that correct close and can fail before
        // the assertion observes the only property under test: no JSON-RPC response was served.
        await using var net = new NetworkStream(client, ownsSocket: false);
        await SendLineAsync(client, "the-WRONG-token");

        // Send a real JSON-RPC request AFTER the bad token, so a still-serving endpoint would have something to
        // reply TO. The send itself is best-effort: if the close already landed, writing to the dead peer throws
        // Broken pipe — which is that same close observed from the write side, not a failure of the property.
        await TrySendLineAsync(client, "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\"}");

        var response = await ReadReplyOrNullIfClosedAsync(net, TimeSpan.FromSeconds(5));
        response.ShouldBeNull(customMessage: "a wrong token must close the connection before any JSON-RPC reply");
    }

    [Fact]
    public async Task A_tools_own_timeout_is_answered_and_the_same_connection_keeps_serving()
    {
        if (!Socket.OSSupportsUnixDomainSockets) return;

        using var dir = new TempDir();
        using var upstream = new HungUpstream();
        var socketPath = Path.Combine(dir.Path, "mcp.sock");
        const string token = "the-token";

        await using var endpoint = new AgentMcpEndpoint(Guid.NewGuid(), new SingleToolRegistry(new HungUpstreamFetchTool(upstream.Url)), AgentAutonomyLevel.Standard, Guid.NewGuid(), SecretRedactor.None, socketPath, token, new AgentMcpConnectRegistry(), new TrackingScope(new AllowingAuthorityStub()), CancellationToken.None, NullLogger.Instance);

        using var client = await ConnectAsync(socketPath);
        await using var net = new NetworkStream(client, ownsSocket: false);
        using var reader = new StreamReader(net, Encoding.UTF8);
        await SendLineAsync(client, token);

        // The tool's real HTTP call times out on its own (HttpClient.Timeout → TaskCanceledException) while the run is live.
        await SendLineAsync(client, """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"hung.fetch","arguments":{}}}""");
        var timedOut = await ReadLineOrNullIfClosedAsync(reader, TimeSpan.FromSeconds(10));

        timedOut.ShouldNotBeNull("the tool's own timeout closed the run's MCP connection instead of answering — the model would lose every tool for the rest of the run");
        var result = JsonDocument.Parse(timedOut).RootElement.GetProperty("result");
        result.GetProperty("isError").GetBoolean().ShouldBeTrue("a tool's own timeout comes back as a tool error the model can retry");

        // The SAME connection serves the next request — the pump survived the tool's fault.
        await SendLineAsync(client, """{"jsonrpc":"2.0","id":2,"method":"tools/list"}""");
        var listed = await ReadLineOrNullIfClosedAsync(reader, TimeSpan.FromSeconds(10));

        listed.ShouldNotBeNull("the connection must still be serving after a tool's own timeout");
        var reply = JsonDocument.Parse(listed).RootElement;
        reply.GetProperty("id").GetInt32().ShouldBe(2);
        reply.GetProperty("result").GetProperty("tools")[0].GetProperty("name").GetString().ShouldBe("hung.fetch");
    }

    /// <summary>
    /// Read one reply, treating a HARD close as the same "served nothing" a graceful EOF is. The endpoint decides to
    /// close the moment it sees a bad token, and this test writes a second line AFTER that — writing to a peer that
    /// has already closed makes its stack answer with RST rather than FIN, so the read throws
    /// <c>Connection reset by peer</c> instead of returning null. Which of the two arrives is a race between the
    /// server's close and the client's write, and it flaked exactly that way on PR #1308's CI, on a diff touching
    /// only the supervisor spawn path.
    ///
    /// <para>Both forms mean the one thing under test — the endpoint served no JSON-RPC. The old assertion demanded
    /// EOF specifically, which is narrower than its own intent. The teeth are unchanged: an actual reply returns a
    /// non-null line and reds, and a hang still reds because the timeout is NOT absorbed.</para>
    /// </summary>
    private static async Task<string?> ReadReplyOrNullIfClosedAsync(NetworkStream net, TimeSpan timeout)
    {
        using var reader = new StreamReader(net, Encoding.UTF8);

        return await ReadLineOrNullIfClosedAsync(reader, timeout);
    }

    /// <summary>The next line off a reader the test keeps for the whole connection, or null when the endpoint closed it — a graceful EOF or a reset, for the reason <see cref="ReadReplyOrNullIfClosedAsync"/> gives. A hang still reds: the timeout is not absorbed.</summary>
    private static async Task<string?> ReadLineOrNullIfClosedAsync(StreamReader reader, TimeSpan timeout)
    {
        try
        {
            return await reader.ReadLineAsync().WaitAsync(timeout);
        }
        catch (IOException ex) when (ex.InnerException is SocketException { SocketErrorCode: SocketError.ConnectionReset or SocketError.ConnectionAborted })
        {
            return null;
        }
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

        await using var endpoint = new AgentMcpEndpoint(runId, new EmptyRegistry(), AgentAutonomyLevel.Standard, Guid.NewGuid(), SecretRedactor.None, socketPath, "the-token", connects, scope, CancellationToken.None, NullLogger.Instance);

        // Connect then immediately close WITHOUT sending the token line → the endpoint reads EOF before any token,
        // fails closed (silent close), and never serves JSON-RPC. Dispose must still be clean.
        using (var client = await ConnectAsync(socketPath))
        {
            client.Shutdown(SocketShutdown.Both);
        }
        await Task.Delay(50);

        (await RecordDisposeAsync(endpoint)).ShouldBeNull("a connection that closed before its token line must still leave dispose quiet — neither the accept loop's cancel nor a torn socket may escape it");
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

        await using var endpoint = new AgentMcpEndpoint(runId, new EmptyRegistry(), AgentAutonomyLevel.Standard, Guid.NewGuid(), SecretRedactor.None, socketPath, "tok", connects, scope, CancellationToken.None, NullLogger.Instance);

        // Real OS state: the socket inode must be 0600 so no other local user can connect to the run's endpoint.
        File.GetUnixFileMode(socketPath).ShouldBe(UnixFileMode.UserRead | UnixFileMode.UserWrite,
            customMessage: "the per-run socket must be owner-only (0600) — another local user must not be able to connect");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// What escaped one <see cref="AgentMcpEndpoint.DisposeAsync"/>, or null. Recorded rather than asserted with
    /// <c>Should.NotThrowAsync</c>, which passes a CANCELED task without a word — and dispose cancels its own accept
    /// loop, so an <see cref="OperationCanceledException"/> leaking out of it is the very escape "never throws" has to
    /// catch. Bounded, so a dispose that never drains its pumps fails with a TimeoutException instead of hanging the run.
    /// </summary>
    private static Task<Exception?> RecordDisposeAsync(AgentMcpEndpoint endpoint) => Record.ExceptionAsync(() => endpoint.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10)));

    private static async Task<Socket> ConnectAsync(string socketPath)
    {
        var client = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        await client.ConnectAsync(new UnixDomainSocketEndPoint(socketPath));
        return client;
    }

    /// <summary>
    /// Send a line, tolerating the peer having ALREADY closed. The close this test is about races the write as well
    /// as the read: #1309 taught the READ side that a hard close arrives as RST instead of EOF, but the same race
    /// hits the second write, where it surfaces as Broken pipe — which reddened CI on PR #1313, a heartbeat change
    /// touching nothing near sockets. Both are the endpoint having closed, which is the property under test.
    ///
    /// <para>Only used for a send whose whole purpose is "give a still-serving endpoint something to reply to".
    /// The FIRST send must still throw if it fails — that one has to reach the endpoint for the test to mean
    /// anything, which is why <see cref="SendLineAsync"/> stays strict. EVERY post-rejection send belongs here:
    /// the evidence test kept the strict helper for its second write and reddened main the same way, on a wave
    /// touching nothing near sockets.</para>
    /// </summary>
    private static async Task TrySendLineAsync(Socket socket, string line)
    {
        try
        {
            await SendLineAsync(socket, line);
        }
        catch (SocketException ex) when (ex.SocketErrorCode is SocketError.Shutdown or SocketError.NotConnected or SocketError.ConnectionReset or SocketError.ConnectionAborted)
        {
        }
        catch (IOException ex) when (ex.InnerException is SocketException { SocketErrorCode: SocketError.Shutdown or SocketError.NotConnected or SocketError.ConnectionReset or SocketError.ConnectionAborted })
        {
        }
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

    private sealed class SingleToolRegistry(IAgentTool tool) : IAgentToolRegistry
    {
        public IReadOnlyList<IAgentTool> All { get; } = new[] { tool };
        public IAgentTool? Resolve(string kind) => kind == tool.Kind ? tool : null;
    }

    /// <summary>A loopback listener that completes the TCP handshake (the kernel backlog) and never answers — an upstream that hangs. The port is the OS's pick, never a hardcoded one.</summary>
    private sealed class HungUpstream : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);

        public HungUpstream() => _listener.Start();

        public string Url => $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/";

        public void Dispose() => _listener.Stop();
    }

    /// <summary>A read-only tool whose call is a real HTTP request to a hung upstream, so what reaches the handler is the exact <see cref="TaskCanceledException"/> <c>HttpClient.Timeout</c> throws — Octokit's GitHub calls throw the same — with the run's token never cancelled.</summary>
    private sealed class HungUpstreamFetchTool(string url) : IAgentTool
    {
        public string Kind => "hung.fetch";
        public string Description => "fetch from an upstream that never answers";
        public JsonElement InputSchema { get; } = JsonDocument.Parse("""{"type":"object"}""").RootElement.Clone();
        public JsonElement OutputSchema { get; } = JsonDocument.Parse("{}").RootElement.Clone();
        public bool IsReadOnly => true;
        public bool IsDestructive => false;

        public AgentToolValidation ValidateInput(JsonElement input) => AgentToolValidation.Valid;

        public async Task<AgentToolResult> CallAsync(AgentToolCall call, CancellationToken cancellationToken)
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMilliseconds(200) };

            await http.GetAsync(url, cancellationToken);

            return AgentToolResult.Ok(JsonDocument.Parse("{}").RootElement.Clone(), 2);
        }
    }

    // The endpoint takes an IServiceScope (it mints per-connection child scopes for the ledger when governance is on).
    // These tests run governance OFF, so the provider is never asked for the ledger — an empty provider suffices.
    // These unit tests exercise framing with an empty registry. No fake principal authorizes a tool — except in the
    // tool-timeout test, whose subject is the pump surviving a tool's fault, so it must reach a tool at all;
    // real receipt/revocation behavior is exercised by AgentExecutionAuthorityFlowTests against PostgreSQL.
    private sealed class TransportAuthorityStub : IAgentAuthorityCallGuard
    {
        public Task<AuthorityCallFailure?> CheckAsync(Guid runId, Guid teamId, string toolKind, CancellationToken cancellationToken) => Task.FromResult<AuthorityCallFailure?>(new("agent.authority_denied", "transport-test", "No execution principal in this transport fixture.", false));
    }

    private sealed class AllowingAuthorityStub : IAgentAuthorityCallGuard
    {
        public Task<AuthorityCallFailure?> CheckAsync(Guid runId, Guid teamId, string toolKind, CancellationToken cancellationToken) => Task.FromResult<AuthorityCallFailure?>(null);
    }

    private sealed class TrackingScope(IAgentAuthorityCallGuard? guard = null) : IServiceScope
    {
        public bool Disposed { get; private set; }
        public IServiceProvider ServiceProvider { get; } = new ServiceCollection().AddSingleton(guard ?? new TransportAuthorityStub()).BuildServiceProvider();
        public void Dispose() => Disposed = true;
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "cs-mcp-ep-" + Guid.NewGuid().ToString("N")[..12]);
        public TempDir() => Directory.CreateDirectory(Path);
        public void Dispose() { try { Directory.Delete(Path, recursive: true); } catch { /* best-effort */ } }
    }
}
