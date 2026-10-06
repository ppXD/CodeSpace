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
/// is anonymous (<c>GIT_HTTP_EXPORT_ALL</c>); <c>git-receive-pack</c> and every LFS endpoint demand
/// <c>x-access-token:&lt;FakeToken&gt;</c> basic auth, so a push that arrives proves the real credential was presented.
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

    private readonly HttpListener _listener = new();
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

    /// <summary>Count of authenticated <c>git-receive-pack</c> requests — a push that validated the real credential.</summary>
    public int AuthenticatedPushRequests { get; private set; }

    /// <summary>OIDs the remote received over the LFS upload endpoint.</summary>
    public List<string> UploadedLfsOids { get; } = new();

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
            _listener.Prefixes.Clear();
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
        var bytes = await File.ReadAllBytesAsync(stored);
        context.Response.ContentLength64 = bytes.Length;
        await context.Response.OutputStream.WriteAsync(bytes);
    }

    private async Task ServeGitAsync(HttpListenerContext context, string path)
    {
        var isPush = path.EndsWith("/git-receive-pack", StringComparison.Ordinal) || context.Request.QueryString["service"] == "git-receive-pack";

        if (isPush)
        {
            if (!AuthOk(context)) { Unauthorized(context); return; }
            lock (_gate) AuthenticatedPushRequests++;
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
