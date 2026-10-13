using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace CodeSpace.E2ETests.Infrastructure;

/// <summary>
/// A git provider on a loopback port, for tests that need to see which token reached the provider and through which call.
/// It answers GitLab's token-revocation endpoint (<c>POST /oauth/revoke</c>) with 200, GitHub's OAuth App revocation
/// endpoints (<c>DELETE /api/v3/applications/{client_id}/grant</c> and <c>/token</c>) with 204 as GitHub does, GitHub's
/// token endpoint as <see cref="AnswerRefresh"/> sets it, and every other request with an empty JSON list — enough for the
/// real GitLab provider to list repositories — and records each request whole: method, path and query, headers and body.
/// The production clients speak real HTTP to it. Requests are served concurrently.
/// </summary>
internal sealed class RecordingProviderStub : IAsyncDisposable
{
    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _stopping = new();
    private readonly ConcurrentQueue<RecordedRequest> _requests = new();
    private readonly Task _accept;
    private RefreshAnswer? _refresh;

    private RecordingProviderStub()
    {
        // Reserve an ephemeral loopback port; retry binding only if another process won the close/bind race.
        for (var attempt = 0; ; attempt++)
        {
            var port = FreeLoopbackPort();
            BaseUrl = $"http://127.0.0.1:{port}";
            _listener.Prefixes.Clear();
            _listener.Prefixes.Add($"{BaseUrl}/");
            try { _listener.Start(); break; }
            catch (HttpListenerException) when (attempt < 4) { }
        }

        _accept = Task.Run(AcceptAsync);
    }

    public string BaseUrl { get; }

    public IReadOnlyCollection<RecordedRequest> Requests => _requests;

    /// <summary>Every request that carried <paramref name="secret"/> anywhere — its query, a header, or its body.</summary>
    public IReadOnlyList<RecordedRequest> Carrying(string secret) => _requests.Where(r => r.Whole.Contains(secret, StringComparison.Ordinal)).ToList();

    public static RecordingProviderStub Start() => new();

    /// <summary>
    /// Answer GitHub's token endpoint (<c>POST /login/oauth/access_token</c>) with the pair <paramref name="accessToken"/>,
    /// <paramref name="refreshToken"/> once <paramref name="whileRefreshing"/> has run — a write that lands while GitHub
    /// holds a refresh. Requests are served concurrently, so that write may reach this stub itself.
    /// </summary>
    public void AnswerRefresh(string accessToken, string refreshToken, Func<Task> whileRefreshing) => _refresh = new RefreshAnswer(accessToken, refreshToken, whileRefreshing);

    /// <summary>A loopback port with nothing listening on it — a provider that is down. Allocated by the OS, then released.</summary>
    public static int FreeLoopbackPort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private async Task AcceptAsync()
    {
        try
        {
            while (!_stopping.IsCancellationRequested)
            {
                var context = await _listener.GetContextAsync().WaitAsync(_stopping.Token);
                _ = ServeAsync(context);
            }
        }
        catch (Exception) when (_stopping.IsCancellationRequested) { }
    }

    private async Task ServeAsync(HttpListenerContext context)
    {
        try
        {
            using var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8);
            var body = await reader.ReadToEndAsync(_stopping.Token);
            var headers = string.Join("\n", context.Request.Headers.AllKeys.Select(key => $"{key}: {context.Request.Headers[key]}"));
            var pathAndQuery = context.Request.Url!.PathAndQuery;

            _requests.Enqueue(new RecordedRequest(context.Request.HttpMethod, pathAndQuery, headers, body));

            var (status, answer) = await AnswerAsync(pathAndQuery);
            var reply = Encoding.UTF8.GetBytes(answer);
            context.Response.StatusCode = status;
            context.Response.ContentType = "application/json";
            context.Response.ContentLength64 = reply.Length;
            await context.Response.OutputStream.WriteAsync(reply, _stopping.Token);
        }
        catch (Exception) when (_stopping.IsCancellationRequested) { }
        finally
        {
            context.Response.Close();
        }
    }

    private async Task<(int Status, string Body)> AnswerAsync(string pathAndQuery)
    {
        if (pathAndQuery.StartsWith("/api/v3/applications/", StringComparison.Ordinal)) return (204, string.Empty);
        if (pathAndQuery.StartsWith("/oauth/revoke", StringComparison.Ordinal)) return (200, "{}");
        if (_refresh != null && pathAndQuery.StartsWith("/login/oauth/access_token", StringComparison.Ordinal)) return (200, await RefreshAsync(_refresh));

        return (200, "[]");
    }

    private static async Task<string> RefreshAsync(RefreshAnswer refresh)
    {
        await refresh.WhileRefreshing();

        return JsonSerializer.Serialize(new { access_token = refresh.AccessToken, refresh_token = refresh.RefreshToken, expires_in = 28800, token_type = "bearer", scope = "" });
    }

    public async ValueTask DisposeAsync()
    {
        _stopping.Cancel();
        _listener.Stop();
        _listener.Close();

        try { await _accept; }
        catch (Exception) { /* the accept loop ends with the listener */ }

        _stopping.Dispose();
    }

    private sealed record RefreshAnswer(string AccessToken, string RefreshToken, Func<Task> WhileRefreshing);

    internal sealed record RecordedRequest(string Method, string PathAndQuery, string Headers, string Body)
    {
        public string Whole => $"{Method} {PathAndQuery}\n{Headers}\n\n{Body}";

        /// <summary>The form fields of a url-encoded body, decoded.</summary>
        public IReadOnlyDictionary<string, string> Form => Body.Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(pair => pair.Split('=', 2))
            .ToDictionary(pair => WebUtility.UrlDecode(pair[0]), pair => pair.Length > 1 ? WebUtility.UrlDecode(pair[1]) : string.Empty, StringComparer.Ordinal);
    }
}
