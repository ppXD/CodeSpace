using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows;

/// <summary>
/// A loopback smart-HTTP git remote (the real <c>git http-backend</c>) that ALSO speaks the Git-LFS batch API, with a
/// FAKE token required for every write — the legitimate destination an agent's produced branch must reach. Fetch/clone
/// is anonymous (<c>GIT_HTTP_EXPORT_ALL</c>) unless <see cref="AuthenticateReads"/> is set; <c>git-receive-pack</c> and
/// every LFS endpoint demand <c>x-access-token:&lt;FakeToken&gt;</c> basic auth, so a push that arrives proves the real
/// credential was presented.
/// It records every request so a test can assert what the remote saw — the authenticated push, the LFS objects uploaded,
/// and that no agent-injected header (<see cref="HostileHeader"/>) was ever sent to it. Fixture setup runs real git out
/// of band; only the production provider's commands run through the sandbox runner under test.
/// </summary>
internal sealed class GitPublishRemoteFixture : IAsyncDisposable
{
    /// <summary>The push/LFS credential the fixture demands — a fake value that only lives in this test's remote and the token it hands the provider.</summary>
    public const string FakeToken = "fake-publish-token-0123456789";

    /// <summary>A request header an agent's <c>http.extraHeader</c> would inject; the clean publish repo must never send it to the remote.</summary>
    public const string HostileHeader = "X-Codespace-Exfil";

    private HttpListener _listener = new();
    private readonly CancellationTokenSource _stopping = new();
    private readonly List<Task> _requests = new();
    private readonly object _gate = new();
    private Task? _accept;

    public string Root { get; } = Directory.CreateTempSubdirectory("cs-pub-remote-").FullName;
    public string Remote => Path.Combine(Root, "remote.git");
    private string Seed => Path.Combine(Root, "seed");
    private string LfsStore => Path.Combine(Root, "lfsstore");
    public string Url { get; private set; } = "";
    public string BaseSha { get; private set; } = "";

    /// <summary>When set, every git request — a clone, a fetch, an <c>ls-remote</c> — demands the token too, so a read that succeeds proves it authenticated. Off by default: reads are anonymous.</summary>
    public bool AuthenticateReads { get; init; }

    /// <summary>Count of authenticated <c>git-receive-pack</c> requests — a push that validated the real credential.</summary>
    public int AuthenticatedPushRequests { get; private set; }

    /// <summary>OIDs the remote received over the LFS upload endpoint.</summary>
    public List<string> UploadedLfsOids { get; } = new();

    /// <summary>OIDs the remote served over the LFS download endpoint, to an authenticated request.</summary>
    public List<string> DownloadedLfsOids { get; } = new();

    /// <summary>True if any request to the remote carried the agent-injected <see cref="HostileHeader"/>.</summary>
    public bool SawHostileHeader { get; private set; }

    public async Task StartAsync()
    {
        await GitAsync(Root, new[] { "init", "--bare", "-b", "main", Remote });
        await GitAsync(Root, new[] { "--git-dir", Remote, "config", "http.receivepack", "true" });
        Directory.CreateDirectory(LfsStore);

        Directory.CreateDirectory(Seed);
        await GitAsync(Seed, new[] { "init", "-b", "main" });
        await GitAsync(Seed, new[] { "config", "user.name", "Fixture" });
        await GitAsync(Seed, new[] { "config", "user.email", "fixture@example.test" });
        await GitAsync(Seed, new[] { "config", "commit.gpgsign", "false" });

        // Two commits, so a depth-1 clone's boundary commit has a parent the clone lacks — the shape every real repo with
        // history has, and the one that makes the publish need the clone's shallow boundary.
        foreach (var content in new[] { "base\n", "base, revised\n" })
        {
            await File.WriteAllTextAsync(Path.Combine(Seed, "README.md"), content);
            await GitAsync(Seed, new[] { "add", "." });
            await GitAsync(Seed, new[] { "commit", "-m", content.Trim() });
        }

        await PublishSeedAsync();

        for (var attempt = 0; ; attempt++)
        {
            using var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            Url = $"http://127.0.0.1:{port}/remote.git";

            // A failed Start closes the listener for good (Prefixes then throws ObjectDisposedException), so each attempt
            // at a fresh port needs a fresh listener.
            if (attempt > 0) _listener = new HttpListener();
            _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            try { _listener.Start(); break; }
            catch (HttpListenerException) when (attempt < 4) { }
        }

        _accept = AcceptAsync();
    }

    /// <summary>The number of objects in the remote's LFS store that match <paramref name="oid"/>.</summary>
    public bool HasLfsObject(string oid) => File.Exists(Path.Combine(LfsStore, oid));

    /// <summary>
    /// Move main to a commit whose tree reuses a LEGACY subtree: one written with a zero-padded file mode
    /// (<c>0100644</c>), as old git versions and some hosting web editors did. Strict fsck rejects that object
    /// (<c>zeroPaddedFilemode</c>), yet it is already on the remote and every later commit that leaves the directory
    /// alone reuses it. Call before the clone.
    /// </summary>
    public async Task AddLegacySubtreeCommitAsync()
    {
        var blob = await HashObjectAsync("blob", Encoding.UTF8.GetBytes("written by an old git\n"));
        var legacy = await HashObjectAsync("tree", TreeEntry("0100644", "legacy.txt", blob));
        var readme = (await GitAsync(Seed, new[] { "rev-parse", "HEAD:README.md" })).Trim();
        var root = await HashObjectAsync("tree", TreeEntry("100644", "README.md", readme).Concat(TreeEntry("40000", "legacy", legacy)).ToArray());
        var commit = (await GitAsync(Seed, new[] { "commit-tree", root, "-p", "HEAD", "-m", "legacy subtree" })).Trim();

        await GitAsync(Seed, new[] { "update-ref", "refs/heads/main", commit });
        await PublishSeedAsync();
    }

    /// <summary>
    /// Give main LFS history: <c>big.bin</c> at a new commit (<see cref="LfsHistory.BaseSha"/>), a different version of it
    /// at main's tip after that, and one more object in the LFS store that no commit names yet, for a patch to point at.
    /// The seed has no LFS filters, so the pointers are committed as plain text; every object lives only in the remote's
    /// LFS store. Call before the clone.
    /// </summary>
    public async Task<LfsHistory> AddLfsHistoryAsync()
    {
        await File.WriteAllTextAsync(Path.Combine(Seed, ".gitattributes"), "*.bin filter=lfs diff=lfs merge=lfs -text\n");
        var atBase = await CommitLfsFileAsync("lfs payload v1\n");
        var baseSha = (await GitAsync(Seed, new[] { "rev-parse", "HEAD" })).Trim();

        await CommitLfsFileAsync("lfs payload v2\n");
        var unreferenced = await StoreLfsObjectAsync("lfs payload v3\n");

        await PublishSeedAsync();
        return new LfsHistory(baseSha, atBase, unreferenced);
    }

    private async Task<LfsObject> CommitLfsFileAsync(string payload)
    {
        var lfs = await StoreLfsObjectAsync(payload);
        await File.WriteAllTextAsync(Path.Combine(Seed, "big.bin"), lfs.Pointer);
        await GitAsync(Seed, new[] { "add", "." });
        await GitAsync(Seed, new[] { "commit", "-m", payload.Trim() });
        return lfs;
    }

    private async Task<LfsObject> StoreLfsObjectAsync(string payload)
    {
        var bytes = Encoding.UTF8.GetBytes(payload);
        var oid = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();
        await File.WriteAllBytesAsync(Path.Combine(LfsStore, oid), bytes);
        return new LfsObject(oid, bytes.Length);
    }

    /// <summary>One raw tree entry: <c>&lt;mode&gt; &lt;name&gt;\0&lt;20-byte sha&gt;</c>, written exactly as given (no mode normalisation).</summary>
    public static byte[] TreeEntry(string mode, string name, string sha) => Encoding.ASCII.GetBytes($"{mode} {name}\0").Concat(Convert.FromHexString(sha)).ToArray();

    /// <summary>Write an object verbatim (<c>--literally</c>, so git does not validate it) into <paramref name="repository"/> or the seed; returns its sha.</summary>
    public async Task<string> HashObjectAsync(string type, byte[] content, string? repository = null)
    {
        var file = Path.Combine(Root, "object-" + Guid.NewGuid().ToString("N"));
        await File.WriteAllBytesAsync(file, content);
        return (await GitAsync(repository ?? Seed, new[] { "hash-object", "-w", "--literally", "-t", type, file })).Trim();
    }

    private async Task PublishSeedAsync()
    {
        BaseSha = (await GitAsync(Seed, new[] { "rev-parse", "HEAD" })).Trim();
        await GitAsync(Seed, new[] { "push", "--force", Remote, "main" });
    }

    private async Task AcceptAsync()
    {
        try
        {
            while (!_stopping.IsCancellationRequested)
            {
                var context = await _listener.GetContextAsync().WaitAsync(_stopping.Token);
                _requests.Add(ServeAsync(context));
            }
        }
        catch (OperationCanceledException) when (_stopping.IsCancellationRequested) { }
        catch (HttpListenerException) when (_stopping.IsCancellationRequested) { }
        catch (ObjectDisposedException) when (_stopping.IsCancellationRequested) { }
    }

    private async Task ServeAsync(HttpListenerContext context)
    {
        try
        {
            if (context.Request.Headers[HostileHeader] is not null) lock (_gate) SawHostileHeader = true;

            var path = context.Request.Url!.AbsolutePath;

            if (path.EndsWith("/info/lfs/objects/batch", StringComparison.Ordinal)) { await ServeLfsBatchAsync(context); return; }
            if (path.Contains("/lfs-object/", StringComparison.Ordinal)) { await ServeLfsObjectAsync(context, path); return; }

            await ServeGitAsync(context, path);
        }
        finally { context.Response.Close(); }
    }

    private static bool AuthOk(HttpListenerContext context) =>
        string.Equals(context.Request.Headers["Authorization"], "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"x-access-token:{FakeToken}")), StringComparison.Ordinal);

    private void Unauthorized(HttpListenerContext context)
    {
        context.Response.StatusCode = 401;
        context.Response.Headers["WWW-Authenticate"] = "Basic realm=\"fixture\"";
    }

    private async Task ServeLfsBatchAsync(HttpListenerContext context)
    {
        if (!AuthOk(context)) { Unauthorized(context); return; }

        using var reader = new StreamReader(context.Request.InputStream);
        var document = JsonDocument.Parse(await reader.ReadToEndAsync());
        var operation = document.RootElement.GetProperty("operation").GetString();
        var origin = context.Request.Url!.GetLeftPart(UriPartial.Authority);

        var objects = new List<object>();
        foreach (var o in document.RootElement.GetProperty("objects").EnumerateArray())
        {
            var oid = o.GetProperty("oid").GetString()!;
            var size = o.GetProperty("size").GetInt64();
            var present = File.Exists(Path.Combine(LfsStore, oid));

            var actions = operation == "upload"
                ? present ? new Dictionary<string, object>() : new Dictionary<string, object> { ["upload"] = new { href = $"{origin}/lfs-object/{oid}" } }
                : new Dictionary<string, object> { ["download"] = new { href = $"{origin}/lfs-object/{oid}" } };

            objects.Add(new { oid, size, actions });
        }

        var body = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { transfer = "basic", objects }));
        context.Response.StatusCode = 200;
        context.Response.ContentType = "application/vnd.git-lfs+json";
        context.Response.ContentLength64 = body.Length;
        await context.Response.OutputStream.WriteAsync(body);
    }

    private async Task ServeLfsObjectAsync(HttpListenerContext context, string path)
    {
        if (!AuthOk(context)) { Unauthorized(context); return; }

        var oid = path[(path.LastIndexOf('/') + 1)..];

        if (context.Request.HttpMethod == "PUT")
        {
            using var body = new MemoryStream();
            await context.Request.InputStream.CopyToAsync(body);
            await File.WriteAllBytesAsync(Path.Combine(LfsStore, oid), body.ToArray());
            lock (_gate) UploadedLfsOids.Add(oid);
            context.Response.StatusCode = 200;
            return;
        }

        var stored = Path.Combine(LfsStore, oid);
        if (!File.Exists(stored)) { context.Response.StatusCode = 404; return; }
        lock (_gate) DownloadedLfsOids.Add(oid);
        var bytes = await File.ReadAllBytesAsync(stored);
        context.Response.ContentLength64 = bytes.Length;
        await context.Response.OutputStream.WriteAsync(bytes);
    }

    private async Task ServeGitAsync(HttpListenerContext context, string path)
    {
        var isPush = path.EndsWith("/git-receive-pack", StringComparison.Ordinal) || context.Request.QueryString["service"] == "git-receive-pack";

        if (isPush || AuthenticateReads)
        {
            if (!AuthOk(context)) { Unauthorized(context); return; }
            if (isPush) lock (_gate) AuthenticatedPushRequests++;
        }

        using var input = new MemoryStream();
        await context.Request.InputStream.CopyToAsync(input, _stopping.Token);

        var info = StartInfo(Root, new[] { "http-backend" });
        info.RedirectStandardInput = true;
        info.Environment["GIT_PROJECT_ROOT"] = Root;
        info.Environment["GIT_HTTP_EXPORT_ALL"] = "1";
        info.Environment["PATH_INFO"] = path;
        info.Environment["QUERY_STRING"] = context.Request.Url!.Query.TrimStart('?');
        info.Environment["REQUEST_METHOD"] = context.Request.HttpMethod;
        info.Environment["CONTENT_TYPE"] = context.Request.ContentType ?? "";
        info.Environment["CONTENT_LENGTH"] = input.Length.ToString();
        info.Environment["REMOTE_USER"] = "fixture";

        using var process = Process.Start(info).ShouldNotBeNull();
        using var output = new MemoryStream();
        var read = process.StandardOutput.BaseStream.CopyToAsync(output, _stopping.Token);
        var error = process.StandardError.ReadToEndAsync(_stopping.Token);
        input.Position = 0;
        await input.CopyToAsync(process.StandardInput.BaseStream, _stopping.Token);
        process.StandardInput.Close();
        try { await Task.WhenAll(read, error, process.WaitForExitAsync(_stopping.Token)).WaitAsync(TimeSpan.FromSeconds(30)); }
        catch { if (!process.HasExited) process.Kill(entireProcessTree: true); throw; }
        process.ExitCode.ShouldBe(0, await error);

        var bytes = output.ToArray();
        var boundary = FindHeadersEnd(bytes);
        boundary.ShouldBeGreaterThan(0, "git http-backend must emit CGI headers");
        foreach (var header in Encoding.ASCII.GetString(bytes, 0, boundary).Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = header.TrimEnd('\r').Split(':', 2);
            if (pair.Length != 2) continue;
            if (pair[0].Equals("Status", StringComparison.OrdinalIgnoreCase)) context.Response.StatusCode = int.Parse(pair[1].Trim().Split(' ')[0]);
            else if (pair[0].Equals("Content-Type", StringComparison.OrdinalIgnoreCase)) context.Response.ContentType = pair[1].Trim();
            else context.Response.Headers[pair[0]] = pair[1].Trim();
        }
        context.Response.ContentLength64 = bytes.Length - boundary;
        await context.Response.OutputStream.WriteAsync(bytes.AsMemory(boundary), _stopping.Token);
    }

    private static int FindHeadersEnd(byte[] bytes)
    {
        for (var index = 1; index < bytes.Length; index++)
        {
            if (bytes[index - 1] == '\n' && bytes[index] == '\n') return index + 1;
            if (index >= 3 && bytes[index - 3] == '\r' && bytes[index - 2] == '\n' && bytes[index - 1] == '\r' && bytes[index] == '\n') return index + 1;
        }
        return -1;
    }

    public static async Task<string> GitAsync(string cwd, IReadOnlyList<string> args)
    {
        using var process = Process.Start(StartInfo(cwd, args)).ShouldNotBeNull();
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        try { await Task.WhenAll(output, error, process.WaitForExitAsync()).WaitAsync(TimeSpan.FromSeconds(30)); }
        catch { if (!process.HasExited) process.Kill(entireProcessTree: true); throw; }
        process.ExitCode.ShouldBe(0, await error);
        return await output;
    }

    private static ProcessStartInfo StartInfo(string cwd, IReadOnlyList<string> args)
    {
        var info = new ProcessStartInfo("git") { WorkingDirectory = cwd, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var arg in args) info.ArgumentList.Add(arg);
        info.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        info.Environment["GIT_CONFIG_GLOBAL"] = Path.Combine(cwd, "absent-global-config");
        info.Environment["GIT_TERMINAL_PROMPT"] = "0";
        return info;
    }

    public async ValueTask DisposeAsync()
    {
        _stopping.Cancel();
        _listener.Close();
        try
        {
            if (_accept is not null) await _accept;
            await Task.WhenAll(_requests);
        }
        finally { _stopping.Dispose(); try { Directory.Delete(Root, recursive: true); } catch { /* best-effort */ } }
    }
}

/// <summary>An LFS object the fixture's remote stores, and the pointer a commit names it by.</summary>
internal sealed record LfsObject(string Oid, long Size)
{
    public string Pointer => $"version https://git-lfs.github.com/spec/v1\noid sha256:{Oid}\nsize {Size}\n";
}

/// <summary>What <see cref="GitPublishRemoteFixture.AddLfsHistoryAsync"/> made: the commit holding <see cref="AtBase"/>, and an object no commit names.</summary>
internal sealed record LfsHistory(string BaseSha, LfsObject AtBase, LfsObject Unreferenced);

/// <summary>A loopback endpoint that accepts and records every connection, answering nothing useful — the attacker a redirect (insteadOf / proxy / a hostile .lfsconfig) would reach. The publish must send it NOTHING.</summary>
internal sealed class LoopbackSink : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private int _connections;

    public LoopbackSink()
    {
        _listener.Start();
        _ = AcceptAsync();
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
    public int Connections => Volatile.Read(ref _connections);

    private async Task AcceptAsync()
    {
        try
        {
            while (true)
            {
                using var client = await _listener.AcceptTcpClientAsync();
                Interlocked.Increment(ref _connections);
            }
        }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException) { }
    }

    public void Dispose() => _listener.Stop();
}

/// <summary>
/// A loopback forward proxy that answers 407 to every request without <see cref="User"/>'s Basic proxy credential and
/// relays the rest to the origin, one request per connection — an operator's proxy whose password lives in their
/// credential helper. Counts the requests it relayed, so a test can tell git went through it rather than around it.
/// </summary>
internal sealed class AuthenticatingProxy : IAsyncDisposable
{
    public const string User = "proxyuser";
    public const string Password = "fake-proxy-password";

    private static readonly string Credential = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{User}:{Password}"));
    private static readonly byte[] Challenge = Encoding.ASCII.GetBytes("HTTP/1.1 407 Proxy Authentication Required\r\nProxy-Authenticate: Basic realm=\"fixture-proxy\"\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");

    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stopping = new();
    private readonly List<Task> _connections = new();
    private readonly Task _accept;
    private int _relayed;

    public AuthenticatingProxy()
    {
        _listener.Start();
        _accept = AcceptAsync();
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
    public int RelayedRequests => Volatile.Read(ref _relayed);

    private async Task AcceptAsync()
    {
        try
        {
            while (true)
            {
                var client = await _listener.AcceptTcpClientAsync(_stopping.Token);
                lock (_connections) _connections.Add(ServeAsync(client));
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException) { }
    }

    private async Task ServeAsync(TcpClient client)
    {
        using var _ = client;

        try
        {
            var stream = client.GetStream();
            var (head, body) = await ReadHeadAsync(stream);
            var lines = head.Split("\r\n");
            var request = lines[0].Split(' ');
            var headers = lines.Skip(1).Select(l => l.Split(':', 2)).Where(h => h.Length == 2).ToList();

            if (Header(headers, "Proxy-Authorization") != Credential)
            {
                await DrainBodyAsync(stream, body.Length, headers);
                await stream.WriteAsync(Challenge);
                return;
            }

            Interlocked.Increment(ref _relayed);
            await RelayAsync(stream, request, headers, body);
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or OperationCanceledException) { }
    }

    /// <summary>Forward one request in origin form, without the proxy headers and asking the origin to close after it, then copy the origin's response back until it does.</summary>
    private static async Task RelayAsync(NetworkStream client, string[] request, List<string[]> headers, byte[] body)
    {
        var target = new Uri(request[1]);
        using var upstream = new TcpClient();
        await upstream.ConnectAsync(target.Host, target.Port);
        var origin = upstream.GetStream();

        var forwarded = new StringBuilder($"{request[0]} {target.PathAndQuery} {request[2]}\r\n");
        foreach (var h in headers.Where(h => !h[0].Trim().StartsWith("Proxy-", StringComparison.OrdinalIgnoreCase) && !h[0].Trim().Equals("Connection", StringComparison.OrdinalIgnoreCase))) forwarded.Append($"{h[0]}:{h[1]}\r\n");
        forwarded.Append("Connection: close\r\n\r\n");

        await origin.WriteAsync(Encoding.ASCII.GetBytes(forwarded.ToString()));
        await origin.WriteAsync(body);

        var restOfRequest = client.CopyToAsync(origin);
        await origin.CopyToAsync(client);

        upstream.Close();
        try { await restOfRequest; } catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException) { }
    }

    /// <summary>Read past the request's head; returns it and whatever body bytes arrived with it.</summary>
    private static async Task<(string Head, byte[] Body)> ReadHeadAsync(NetworkStream stream)
    {
        var received = new MemoryStream();
        var chunk = new byte[4096];

        while (true)
        {
            var read = await stream.ReadAsync(chunk);
            if (read == 0) throw new IOException("the client closed before its request head ended");
            received.Write(chunk, 0, read);

            var bytes = received.ToArray();
            var end = bytes.AsSpan().IndexOf("\r\n\r\n"u8);
            if (end >= 0) return (Encoding.ASCII.GetString(bytes, 0, end), bytes[(end + 4)..]);
        }
    }

    /// <summary>Consume a refused request's body so the challenge is read, not reset — unless the client is waiting for a 100 before it sends one.</summary>
    private static async Task DrainBodyAsync(NetworkStream stream, int alreadyRead, List<string[]> headers)
    {
        if (Header(headers, "Expect") is not null) return;

        var remaining = long.TryParse(Header(headers, "Content-Length"), out var length) ? length - alreadyRead : 0;
        var buffer = new byte[8192];

        while (remaining > 0)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)));
            if (read == 0) return;
            remaining -= read;
        }
    }

    private static string? Header(IEnumerable<string[]> headers, string name) => headers.FirstOrDefault(h => h[0].Trim().Equals(name, StringComparison.OrdinalIgnoreCase))?[1].Trim();

    public async ValueTask DisposeAsync()
    {
        _stopping.Cancel();
        _listener.Stop();
        await _accept;

        Task[] open;
        lock (_connections) open = _connections.ToArray();
        try { await Task.WhenAll(open).WaitAsync(TimeSpan.FromSeconds(10)); } catch (TimeoutException) { /* best-effort: a client still holding a connection */ }

        _stopping.Dispose();
    }
}
