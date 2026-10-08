using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace CodeSpace.IntegrationTests.Workflows.Infrastructure;

/// <summary>
/// A raw HTTP/1.1 responder on 127.0.0.1 AND ::1 at one ephemeral port, standing in for a service on the worker's own
/// loopback (a metadata endpoint, an admin port). It counts ACCEPTED CONNECTIONS, not just requests, so a test can
/// prove the guard refused before any socket reached it — a refused-at-connect destination and one that answered
/// would otherwise look alike from the client. It accepts any Host header, so every spelling of loopback reaches it.
/// </summary>
public sealed class LoopbackResponder : IAsyncDisposable
{
    private readonly List<TcpListener> _listeners;
    private readonly Func<string, string> _respond;
    private readonly CancellationTokenSource _stop = new();
    private readonly ConcurrentQueue<string> _requests = new();
    private int _accepted;

    private LoopbackResponder(List<TcpListener> listeners, Func<string, string> respond)
    {
        _listeners = listeners;
        _respond = respond;
        Port = ((IPEndPoint)listeners[0].LocalEndpoint).Port;

        foreach (var listener in listeners) _ = AcceptLoopAsync(listener);
    }

    public int Port { get; }

    public int Accepted => Volatile.Read(ref _accepted);

    /// <summary>Each request's head (request line + headers) in arrival order.</summary>
    public IReadOnlyCollection<string> Requests => _requests;

    /// <summary>Start a responder whose reply to each request head is <paramref name="respond"/>'s raw HTTP response.</summary>
    public static LoopbackResponder Start(Func<string, string> respond)
    {
        var v4 = new TcpListener(IPAddress.Loopback, 0);
        v4.Start();

        return new LoopbackResponder(new List<TcpListener> { v4 }.Concat(TryListenIpv6(((IPEndPoint)v4.LocalEndpoint).Port)).ToList(), respond);
    }

    public static string Ok(string body) => $"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {Encoding.UTF8.GetByteCount(body)}\r\nConnection: close\r\n\r\n{body}";

    public static string Redirect(string location) => $"HTTP/1.1 302 Found\r\nLocation: {location}\r\nContent-Length: 0\r\nConnection: close\r\n\r\n";

    /// <summary>The IPv6 twin on the same port, when this host has IPv6 loopback. Without it a [::1] request would fail with "connection refused" even if the guard let it through — hiding the very bug the test looks for.</summary>
    private static IEnumerable<TcpListener> TryListenIpv6(int port)
    {
        if (!Socket.OSSupportsIPv6) return Array.Empty<TcpListener>();

        try
        {
            var v6 = new TcpListener(IPAddress.IPv6Loopback, port);
            v6.Start();
            return new[] { v6 };
        }
        catch (SocketException)
        {
            return Array.Empty<TcpListener>();
        }
    }

    private async Task AcceptLoopAsync(TcpListener listener)
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;

            try { client = await listener.AcceptTcpClientAsync(_stop.Token).ConfigureAwait(false); }
            catch (Exception) { return; }

            Interlocked.Increment(ref _accepted);
            _ = ServeAsync(client);
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        using var _ = client;

        try
        {
            var stream = client.GetStream();
            var head = await ReadHeadAsync(stream).ConfigureAwait(false);

            _requests.Enqueue(head);

            await stream.WriteAsync(Encoding.UTF8.GetBytes(_respond(head)), _stop.Token).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // A client that hung up mid-request is not this responder's failure to report.
        }
    }

    private async Task<string> ReadHeadAsync(NetworkStream stream)
    {
        var received = new StringBuilder();
        var buffer = new byte[8192];

        while (!received.ToString().Contains("\r\n\r\n"))
        {
            var read = await stream.ReadAsync(buffer, _stop.Token).ConfigureAwait(false);

            if (read == 0) break;

            received.Append(Encoding.ASCII.GetString(buffer, 0, read));
        }

        return received.ToString();
    }

    public ValueTask DisposeAsync()
    {
        _stop.Cancel();

        foreach (var listener in _listeners) listener.Stop();

        _stop.Dispose();

        return ValueTask.CompletedTask;
    }
}
