using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Text;
using CodeSpace.Core.Services.Agents.Tools;
using CodeSpace.Messages.Agents;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CodeSpace.Core.Services.Agents.Mcp;

/// <summary>
/// One run's live MCP endpoint over a PER-RUN Unix-domain socket: it binds + listens on the run's socket path, accepts
/// connections in a loop, and for each connection validates the per-run <c>CODESPACE_RUN_TOKEN</c> on the FIRST line
/// before serving — then pumps one <see cref="McpFramingLoop"/> (a fresh <see cref="McpRequestHandler"/> bound to the
/// run's tool registry + autonomy + team + secret redactor) over the socket's <see cref="NetworkStream"/>. Every
/// tool-result text the handler returns is run through the run's <see cref="SecretRedactor"/>, so an echoed model key
/// never reaches the model. The connect descriptor
/// (socket path + token) is registered with the <see cref="IAgentMcpConnectRegistry"/> under the run id so a consumer
/// (the integration test today, the <c>codespace mcp</c> proxy later) reaches exactly this run's endpoint. The
/// endpoint's life is bounded to the harness span — the executor opens it immediately before the (synchronous) harness
/// run and disposes it unconditionally afterwards.
///
/// <para><see cref="DisposeAsync"/> is IDEMPOTENT and NEVER throws regardless of how the accept loop / connection pumps
/// ended: it cancels the linked CTS, disposes the listener (breaking a blocked accept), awaits the accept loop then all
/// connection pumps while swallowing the expected teardown exceptions, removes the run from the connect registry (so a
/// consumer never resolves a closed listener), unlinks the socket file, then disposes the dedicated DI scope + CTS.</para>
/// </summary>
public sealed class AgentMcpEndpoint : IAsyncDisposable
{
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    private readonly Guid _runId;
    private readonly IAgentToolRegistry _registry;
    private readonly AgentAutonomyLevel _autonomy;
    private readonly Guid _teamId;
    private readonly SecretRedactor _redactor;
    private readonly string _socketPath;
    private readonly string _token;
    private readonly IAgentMcpConnectRegistry _connects;
    private readonly IServiceScope _scope;
    private readonly long _fenceEpoch;
    private readonly bool _governanceEnabled;
    private readonly Guid? _approvalConversationId;
    private readonly CancellationTokenSource _cts;
    private readonly Socket _listener;
    private readonly Task _acceptLoop;
    private readonly ConcurrentBag<Task> _connections = new();

    private bool _disposed;

    public AgentMcpEndpoint(Guid runId, IAgentToolRegistry registry, AgentAutonomyLevel autonomy, Guid teamId, SecretRedactor redactor, string socketPath, string token, IAgentMcpConnectRegistry connects, IServiceScope scope, CancellationToken ct, ILogger logger, long fenceEpoch = 0, bool governanceEnabled = false, Guid? approvalConversationId = null)
    {
        _runId = runId;
        _registry = registry;
        _autonomy = autonomy;
        _teamId = teamId;
        _redactor = redactor;
        _socketPath = socketPath;
        _token = token;
        _connects = connects;
        _scope = scope;
        _fenceEpoch = fenceEpoch;
        _governanceEnabled = governanceEnabled;
        _approvalConversationId = approvalConversationId;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);

        // On any setup throw, dispose the listener + cts (an fd would otherwise orphan) and rethrow; the opener
        // disposes the dedicated scope and fail-softs (so a degraded host is a logged Warning, not a failed run).
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(socketPath)!);

            // Clear a stale socket file from a crashed prior incarnation. CONCURRENCY NOTE: a second reattach racing the
            // first could unlink a LIVE socket the first just bound — bounded today by the reconciler's single-flight
            // CAS (epoch guard), which lets only the current-epoch reattach reach here; the loser's later Bind fails and
            // the opener's fail-soft swallows it. A self-contained epoch guard inside the endpoint is deferred to a
            // later slice (matching the design).
            Quietly(() => File.Delete(socketPath));

            _listener.Bind(new UnixDomainSocketEndPoint(socketPath));

            // Tighten to 0600 BEFORE the listener is reachable: a connect() before Listen cannot succeed, so this
            // closes the window where the inode is group/other-writable under a permissive umask.
            SetOwnerOnly(socketPath, logger);

            _listener.Listen(backlog: 4);
        }
        catch
        {
            _listener.Dispose();
            _cts.Dispose();
            throw;
        }

        _connects.Register(runId, new AgentMcpConnect(socketPath, token));

        _acceptLoop = AcceptLoopAsync(_cts.Token);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        _cts.Cancel();

        Quietly(() => _listener.Dispose());   // breaks a blocked AcceptAsync that didn't observe the cancel

        try { await _acceptLoop.ConfigureAwait(false); }
        catch (OperationCanceledException) { /* cancel unwound the accept */ }
        catch (SocketException) { /* listener disposed under the accept */ }
        catch (ObjectDisposedException) { /* listener disposed under the accept */ }

        // Belt-and-braces: each ServeConnectionAsync already swallows its own faults, so this should not throw —
        // but a connection added between the loop's last iteration and the cancel is still awaited here.
        try { await Task.WhenAll(_connections).ConfigureAwait(false); }
        catch { /* every pump swallows its own teardown exception */ }

        // Remove AFTER the pumps are drained (fail-closed: the registry never points at a closed listener).
        _connects.Remove(_runId);

        Quietly(() => File.Delete(_socketPath));

        _scope.Dispose();
        _cts.Dispose();
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            Socket conn;

            try { conn = await _listener.AcceptAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
            catch (SocketException) { break; }
            catch (ObjectDisposedException) { break; }

            _connections.Add(ServeConnectionAsync(conn, ct));
        }
    }

    private async Task ServeConnectionAsync(Socket conn, CancellationToken ct)
    {
        using var _ = conn;
        await using var stream = new NetworkStream(conn, ownsSocket: false);

        // Utf8NoBom + AutoFlush=false + NewLine="\n" mirror the wire the in-memory channel produced, so the framing is
        // byte-identical to the prior slice.
        using var reader = new StreamReader(stream, Utf8NoBom, detectEncodingFromByteOrderMarks: false);
        await using var writer = new StreamWriter(stream, Utf8NoBom) { AutoFlush = false, NewLine = "\n" };

        if (!await IsAuthenticatedAsync(reader, ct).ConfigureAwait(false)) return;   // silent close, no oracle, before any handler

        // The ledger service is SCOPED (its own DbContext), and the accept loop can serve concurrent connections, so
        // a shared instance would race the (thread-unsafe) DbContext. Mint a FRESH per-connection scope here and
        // dispose it when this connection's pump ends. Null when governance is off → the handler is byte-identical.
        using var connectionScope = _governanceEnabled ? _scope.ServiceProvider.CreateScope() : null;
        var ledger = connectionScope?.ServiceProvider.GetRequiredService<IToolCallLedgerService>();

        var handler = new McpRequestHandler(_registry, _autonomy, _teamId, _redactor, _runId, ledger, _fenceEpoch, _governanceEnabled, _approvalConversationId);
        var loop = new McpFramingLoop(handler);

        try { await loop.RunAsync(reader, writer, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { /* cancel unwound the pump */ }
        catch (IOException) { /* a torn-down socket mid-read/write */ }
        catch (ObjectDisposedException) { /* a stream disposed under the pump */ }
    }

    /// <summary>Read the connection's FIRST line and constant-time-compare it to the run token; an EOF / wrong token fails closed (the caller silently closes).</summary>
    private async Task<bool> IsAuthenticatedAsync(StreamReader reader, CancellationToken ct)
    {
        var presented = await reader.ReadLineAsync(ct).ConfigureAwait(false);

        return presented is not null && McpRunToken.Matches(_token, presented);
    }

    /// <summary>Restrict the socket file to the owner (0600) so another local user can't connect to the run's endpoint. A no-op on Windows where unix file modes don't apply. Best-effort — a chmod failure must NOT fail the endpoint (the 256-bit token is the authoritative gate), but it's logged as a Warning so it isn't fully silent.</summary>
    private void SetOwnerOnly(string socketPath, ILogger logger)
    {
        if (OperatingSystem.IsWindows()) return;

        try { File.SetUnixFileMode(socketPath, UnixFileMode.UserRead | UnixFileMode.UserWrite); }
        catch (Exception ex) { logger.LogWarning(ex, "Agent run {RunId}: could not restrict the MCP socket to 0600; it may be group/other-accessible on this host", _runId); }
    }

    private static void Quietly(Action action)
    {
        try { action(); } catch { /* best-effort teardown / setup */ }
    }
}
