using System.Net;
using System.Net.Sockets;

namespace CodeSpace.IntegrationTests.Webhooks;

/// <summary>
/// A provider that has stopped answering: it accepts connections, reads the request, and says nothing — a GitLab behind a
/// proxy that holds the line, or one that is up and wedged. Not a refusal and not a failure, which is what makes it the case
/// the health sweep must survive: a call to it never returns on its own, and a caller that does not give up holds its thread
/// for the HTTP client's whole timeout. <see cref="StubProviderHost"/> answers one request at a time and always answers, so
/// it cannot stand in for this.
/// </summary>
internal sealed class SilentProviderHost : IDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _stopping = new();
    private readonly List<TcpClient> _held = new();
    private int _connections;

    public SilentProviderHost()
    {
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();

        BaseUrl = $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}";

        _ = Task.Run(AcceptAsync);
    }

    public string BaseUrl { get; }

    /// <summary>How many connections the provider has been called on.</summary>
    public int Connections => Volatile.Read(ref _connections);

    public void Dispose()
    {
        _stopping.Cancel();
        _listener.Stop();

        lock (_held)
        {
            foreach (var client in _held) client.Dispose();
        }
    }

    private async Task AcceptAsync()
    {
        while (!_stopping.IsCancellationRequested)
        {
            TcpClient client;

            try
            {
                client = await _listener.AcceptTcpClientAsync(_stopping.Token).ConfigureAwait(false);
            }
            catch (Exception)
            {
                return;   // the listener was stopped — the only way out of the accept
            }

            Interlocked.Increment(ref _connections);

            lock (_held) _held.Add(client);
        }
    }
}
