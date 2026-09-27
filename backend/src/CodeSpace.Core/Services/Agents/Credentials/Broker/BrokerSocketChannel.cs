using System.Net;
using System.Net.Sockets;
using CodeSpace.Core.Services.Agents.Sandbox;
using Microsoft.Extensions.Logging;

namespace CodeSpace.Core.Services.Agents.Credentials.Broker;

/// <summary>
/// One lease's second door: a per-run Unix socket whose every accepted connection is spliced, byte for byte, to the
/// lease's own listener on <c>127.0.0.1:&lt;port&gt;</c>. A child in a network namespace of its own cannot reach the
/// host's TCP port, but it can reach a socket whose directory is bound into its sandbox — and through the splice its
/// request is answered by exactly the code a TCP caller meets: the bearer and route check, the path allowlist, the
/// relay that attaches the tenant's key. The splice parses nothing, so SSE streams through untouched, and the source
/// the lease sees is loopback.
///
/// <para><b>Closing takes the socket's address away, never its directory.</b> <see cref="Close"/> stops accepting,
/// cuts every spliced connection and deletes the socket FILE; the directory stays, because a running sandbox's bind
/// pins its inode and a worker that re-opens this path later must land in the same one (<see cref="RunSocket"/>).</para>
/// </summary>
internal sealed class BrokerSocketChannel
{
    /// <summary>Connections waiting to be accepted. A CLI opens a few at once (a streamed turn beside a token count), and each is accepted as soon as it lands, so this only has to outlast a burst.</summary>
    private const int Backlog = 16;

    private const int CopyBufferBytes = 16 * 1024;

    private readonly Socket _listener;
    private readonly IPEndPoint _lease;
    private readonly CancellationTokenSource _closing = new();
    private int _closed;

    private BrokerSocketChannel(string path, Socket listener, int port)
    {
        Path = path;
        _listener = listener;
        _lease = new IPEndPoint(IPAddress.Loopback, port);
    }

    /// <summary>The socket this channel serves — what the lease reports, and the durable handle records, for a re-attach to re-open.</summary>
    public string Path { get; }

    /// <summary>Bind <paramref name="socketPath"/> for splicing to <c>127.0.0.1:<paramref name="port"/></c>, or throw with nothing left bound. Nothing is accepted until <see cref="Serve"/>; a connect before then waits in the backlog. The caller must already own the path — on a re-bind, by holding the lease's port first.</summary>
    public static BrokerSocketChannel Open(string socketPath, int port, Guid runId, ILogger logger) => new(socketPath, RunSocket.Listen(socketPath, Backlog, runId, logger), port);

    /// <summary>
    /// Start accepting. <paramref name="onStopped"/> is called once, with the failure, if the acceptor stops for any
    /// reason but <see cref="Close"/> — the caller's cue to stop CLAIMING the lease, because through this socket the
    /// lease is a sandboxed child's only door and a socket nothing accepts on reads, from inside, as a model that
    /// never answers. Started by whoever installs the lease, so that callback has a lease to act on.
    /// </summary>
    public void Serve(Action<Exception> onStopped) => _ = AcceptLoopAsync(onStopped);

    /// <summary>
    /// Stop accepting, cut every spliced connection and delete the socket FILE — its directory stays (see the type
    /// remarks). Idempotent: a second close deletes nothing. It deletes whatever file is at <see cref="Path"/> when it
    /// runs, so it is this channel's own only because the broker never binds a path one of its live leases still serves.
    /// </summary>
    public void Close()
    {
        if (Interlocked.Exchange(ref _closed, 1) == 1) return;

        _closing.Cancel();
        Quietly(_listener.Dispose);
        RunSocket.Remove(Path);
    }

    /// <summary>Test seam: fail the acceptor the way a platform error would — the listener gone without <see cref="Close"/> having been asked for.</summary>
    internal void BreakAcceptorForTest() => Quietly(_listener.Dispose);

    private async Task AcceptLoopAsync(Action<Exception> onStopped)
    {
        while (true)
        {
            Socket connection;

            try { connection = await _listener.AcceptAsync(_closing.Token).ConfigureAwait(false); }
            catch (Exception) when (_closing.IsCancellationRequested) { return; }   // closed — the lease is going away
            catch (Exception exception)
            {
                onStopped(exception);
                return;
            }

            _ = SpliceAsync(connection);
        }
    }

    /// <summary>Join one sandbox connection to a fresh connection to the lease's loopback listener, until both directions end. A listener that is gone is answered by closing the sandbox's connection — the same refusal a closed port gives a TCP caller.</summary>
    private async Task SpliceAsync(Socket sandbox)
    {
        using var client = sandbox;
        using var lease = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);

        try { await lease.ConnectAsync(_lease, _closing.Token).ConfigureAwait(false); }
        catch (Exception) { return; }

        await Task.WhenAll(PumpAsync(client, lease), PumpAsync(lease, client)).ConfigureAwait(false);
    }

    /// <summary>
    /// Copy one direction until its source ends, then HALF-close the destination so the far side sees the end while the
    /// other direction keeps flowing — an HTTP client that shuts its sending side after the request still gets the
    /// whole response. Any failure (either side gone, or the channel closing) tears down BOTH sockets, which ends the
    /// other direction too rather than leaving it waiting on a peer that is never coming back.
    /// </summary>
    private async Task PumpAsync(Socket from, Socket to)
    {
        var buffer = new byte[CopyBufferBytes];

        try
        {
            int read;

            while ((read = await from.ReceiveAsync(buffer, SocketFlags.None, _closing.Token).ConfigureAwait(false)) > 0)
                await SendAllAsync(to, buffer.AsMemory(0, read)).ConfigureAwait(false);

            to.Shutdown(SocketShutdown.Send);
        }
        catch (Exception)
        {
            Quietly(from.Dispose);
            Quietly(to.Dispose);
        }
    }

    private async Task SendAllAsync(Socket to, ReadOnlyMemory<byte> data)
    {
        while (!data.IsEmpty) data = data[await to.SendAsync(data, SocketFlags.None, _closing.Token).ConfigureAwait(false)..];
    }

    private static void Quietly(Action action)
    {
        try { action(); } catch (Exception) { /* already gone */ }
    }
}
