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

    private const int MaxBindAttempts = 5;

    public StubProviderHost()
    {
        (_listener, BaseUrl) = ListenOnFreeLoopbackPort();

        _ = Task.Run(ServeAsync);
    }

    public string BaseUrl { get; }

    public IReadOnlyList<RecordedRequest> Requests { get { lock (_lock) { return _requests.ToList(); } } }

    /// <summary>Answer any request whose path contains <paramref name="pathFragment"/> with this status and body. First match wins, so a test can stack a narrow rule before a broad one.</summary>
    public StubProviderHost Answer(string method, string pathFragment, int statusCode, string body) => Answer(method, pathFragment, _ => new StubReply(statusCode, body));

    /// <summary>Answer with whatever <paramref name="respond"/> decides for this request — for a route whose answer depends on what earlier requests did: a create that lands, then a list that has to show it. Same first-match rule as the fixed answers.</summary>
    public StubProviderHost Answer(string method, string pathFragment, Func<RecordedRequest, StubReply> respond)
    {
        lock (_lock) { _responses.Add(new StubResponse(method, pathFragment, respond)); }
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
        var reply = match?.Respond(recorded) ?? new StubReply(501, """{"message":"no stub configured for this route"}""");

        if (reply.DropsConnection)
        {
            await DropMidResponseAsync(context.Response).ConfigureAwait(false);
            return;
        }

        var payload = Encoding.UTF8.GetBytes(reply.Body);
        context.Response.StatusCode = reply.StatusCode;
        context.Response.ContentType = "application/json";
        context.Response.ContentLength64 = payload.Length;
        await context.Response.OutputStream.WriteAsync(payload).ConfigureAwait(false);
        context.Response.Close();
    }

    /// <summary>
    /// Start an answer and cut it off: headers that promise more body than is ever sent, then the socket closes.
    /// A bare <see cref="HttpListenerResponse.Abort"/> is not enough — the managed listener still flushes a
    /// complete, empty 200 before closing, which the caller reads as success. The short body is what the caller
    /// sees as a dropped connection: an HttpRequestException, after the request was read and acted on.
    /// </summary>
    private static async Task DropMidResponseAsync(HttpListenerResponse response)
    {
        var partial = Encoding.UTF8.GetBytes("{");

        response.StatusCode = 200;
        response.ContentType = "application/json";
        response.ContentLength64 = 1024;
        await response.OutputStream.WriteAsync(partial).ConfigureAwait(false);
        await response.OutputStream.FlushAsync().ConfigureAwait(false);
        response.Abort();
    }

    /// <summary>
    /// The OS picks the port, but the probe has to let go of it before the listener can bind it, and in that gap a
    /// parallel test can take it — or the probe, which reuses addresses, can be handed a port a plain bind still
    /// refuses. Either is a fact about the machine, not about the test, so binding again on a fresh port is the whole
    /// remedy. Seen once in a full unit run as a test failing in its constructor.
    /// </summary>
    private static (HttpListener Listener, string BaseUrl) ListenOnFreeLoopbackPort()
    {
        for (var attempt = 1; ; attempt++)
        {
            var baseUrl = $"http://127.0.0.1:{AllocateLoopbackPort()}";
            var listener = new HttpListener();
            listener.Prefixes.Add($"{baseUrl}/");

            try
            {
                listener.Start();
                return (listener, baseUrl);
            }
            catch (Exception) when (attempt < MaxBindAttempts)
            {
                // HttpListenerException on macOS / Windows, a bare SocketException on Linux — which one is the OS's call.
                listener.Close();
            }
        }
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

    /// <summary>What the stub answers. <see cref="DropConnection"/> closes the socket partway through the answer — the request was read and acted on, and the caller never gets a response it can read.</summary>
    internal sealed record StubReply(int StatusCode, string Body)
    {
        public static StubReply DropConnection { get; } = new(0, string.Empty);

        public bool DropsConnection => StatusCode == 0;
    }

    private sealed record StubResponse(string Method, string PathFragment, Func<RecordedRequest, StubReply> Respond)
    {
        public bool Matches(RecordedRequest request) =>
            string.Equals(Method, request.Method, StringComparison.OrdinalIgnoreCase) && request.PathAndQuery.Contains(PathFragment, StringComparison.OrdinalIgnoreCase);
    }
}
