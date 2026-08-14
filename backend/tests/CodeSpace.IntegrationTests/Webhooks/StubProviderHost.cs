using System.Net;
using System.Net.Sockets;
using System.Text;

namespace CodeSpace.IntegrationTests.Webhooks;

/// <summary>
/// A real HTTP server on loopback that answers like a provider's API. Used so the connection-hook
/// registration tests drive the REAL GitLab and GitHub provider classes — NGitLab and Octokit
/// composing the URL, serialising the body, and parsing the answer — rather than a double standing
/// in for them. The URL a provider actually calls and the shape it actually sends are the part of
/// this work most likely to be wrong, and a double cannot check either.
///
/// <para>Port comes from the OS (port 0) so parallel runs cannot collide.</para>
/// </summary>
internal sealed class StubProviderHost : IDisposable
{
    private readonly HttpListener _listener;
    private readonly List<StubResponse> _responses = new();
    private readonly List<RecordedRequest> _requests = new();
    private readonly object _lock = new();
    private readonly CancellationTokenSource _stopping = new();

    public StubProviderHost()
    {
        var port = AllocateLoopbackPort();
        BaseUrl = $"http://127.0.0.1:{port}";

        _listener = new HttpListener();
        _listener.Prefixes.Add($"{BaseUrl}/");
        _listener.Start();

        _ = Task.Run(ServeAsync);
    }

    public string BaseUrl { get; }

    public IReadOnlyList<RecordedRequest> Requests { get { lock (_lock) { return _requests.ToList(); } } }

    /// <summary>Answer any request whose path contains <paramref name="pathFragment"/> with this status and body. First match wins, so a test can stack a narrow rule before a broad one.</summary>
    public StubProviderHost Answer(string method, string pathFragment, int statusCode, string body)
    {
        lock (_lock) { _responses.Add(new StubResponse(method, pathFragment, statusCode, body)); }
        return this;
    }

    private async Task ServeAsync()
    {
        while (!_stopping.IsCancellationRequested)
        {
            HttpListenerContext context;

            try
            {
                context = await _listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (Exception)
            {
                return;   // listener stopped — the only way out of GetContextAsync
            }

            await RespondAsync(context).ConfigureAwait(false);
        }
    }

    private async Task RespondAsync(HttpListenerContext context)
    {
        var body = await new StreamReader(context.Request.InputStream).ReadToEndAsync().ConfigureAwait(false);
        var recorded = new RecordedRequest(context.Request.HttpMethod, context.Request.Url!.PathAndQuery, body, context.Request.Headers["Authorization"], context.Request.Headers["PRIVATE-TOKEN"]);

        StubResponse? match;
        lock (_lock)
        {
            _requests.Add(recorded);
            match = _responses.FirstOrDefault(r => r.Matches(recorded));
        }

        // 501 for an unstubbed route rather than 404: 404 is a meaningful provider answer in these
        // tests (GitLab hides a Premium endpoint behind one), so it must never be what "the test
        // forgot to stub this" looks like.
        var payload = Encoding.UTF8.GetBytes(match?.Body ?? """{"message":"no stub configured for this route"}""");
        context.Response.StatusCode = match?.StatusCode ?? 501;
        context.Response.ContentType = "application/json";
        context.Response.ContentLength64 = payload.Length;
        await context.Response.OutputStream.WriteAsync(payload).ConfigureAwait(false);
        context.Response.Close();
    }

    private static int AllocateLoopbackPort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    public void Dispose()
    {
        _stopping.Cancel();
        _listener.Close();
        _stopping.Dispose();
    }

    internal sealed record RecordedRequest(string Method, string PathAndQuery, string Body, string? AuthorizationHeader, string? PrivateTokenHeader);

    private sealed record StubResponse(string Method, string PathFragment, int StatusCode, string Body)
    {
        public bool Matches(RecordedRequest request) =>
            string.Equals(Method, request.Method, StringComparison.OrdinalIgnoreCase) && request.PathAndQuery.Contains(PathFragment, StringComparison.OrdinalIgnoreCase);
    }
}
