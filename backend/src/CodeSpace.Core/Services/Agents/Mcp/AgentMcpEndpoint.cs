using CodeSpace.Core.Services.Agents.Tools;
using CodeSpace.Messages.Agents;

namespace CodeSpace.Core.Services.Agents.Mcp;

/// <summary>
/// One run's live MCP endpoint: it news up a <see cref="McpRequestHandler"/> (bound to the run's tool registry +
/// autonomy + team), wraps it in a <see cref="McpFramingLoop"/>, and pumps that loop over the SERVER end of an
/// in-process <see cref="AgentMcpChannel"/> on a background task. The CLIENT end is registered with the
/// <see cref="IAgentMcpConnectRegistry"/> under the run id so a consumer can reach exactly this run's endpoint. The
/// endpoint's life is bounded to the harness span — the executor opens it immediately before the (synchronous) harness
/// run and disposes it unconditionally afterwards.
///
/// <para><see cref="DisposeAsync"/> is IDEMPOTENT and NEVER throws regardless of how the pump ended (clean EOF, a
/// cancel-driven fault): it cancels the loop's linked CTS, awaits the pump while swallowing the expected
/// teardown exceptions, removes the run from the connect registry, then disposes the channel and the dedicated DI
/// scope it was handed. It does NOT dispose the writer to signal EOF — cancelling the CTS unwinds the in-memory
/// reader; EOF-signalling is a UDS-transport concern for a later slice.</para>
/// </summary>
public sealed class AgentMcpEndpoint : IAsyncDisposable
{
    private readonly Guid _runId;
    private readonly AgentMcpChannel _channel;
    private readonly IAgentMcpConnectRegistry _connects;
    private readonly IDisposable _scope;
    private readonly CancellationTokenSource _cts;
    private readonly Task _pump;

    private bool _disposed;

    public AgentMcpEndpoint(Guid runId, IAgentToolRegistry registry, AgentAutonomyLevel autonomy, Guid teamId, AgentMcpChannel channel, IAgentMcpConnectRegistry connects, IDisposable scope, CancellationToken ct)
    {
        _runId = runId;
        _channel = channel;
        _connects = connects;
        _scope = scope;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        var loop = new McpFramingLoop(new McpRequestHandler(registry, autonomy, teamId));

        _connects.Register(runId, new ClientConnect(channel));

        _pump = loop.RunAsync(channel.ServerReader, channel.ServerWriter, _cts.Token);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        _cts.Cancel();

        // OperationCanceledException covers TaskCanceledException (its subtype) — the cancel unwinding the loop's
        // ReadLineAsync. IOException / ObjectDisposedException cover a pipe/stream torn down under a mid-flight read.
        try { await _pump.ConfigureAwait(false); }
        catch (OperationCanceledException) { /* cancel unwound the read */ }
        catch (IOException) { /* a torn-down pipe mid-read */ }
        catch (ObjectDisposedException) { /* a stream disposed under the read */ }

        _connects.Remove(_runId);

        _channel.Dispose();
        _scope.Dispose();
        _cts.Dispose();
    }

    private sealed class ClientConnect : IAgentMcpClientConnect
    {
        private readonly AgentMcpChannel _channel;

        public ClientConnect(AgentMcpChannel channel) { _channel = channel; }

        public TextWriter Writer => _channel.ClientWriter;
        public TextReader Reader => _channel.ClientReader;
    }
}
