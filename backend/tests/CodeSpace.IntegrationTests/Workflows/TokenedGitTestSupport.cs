using System.Text;
using CodeSpace.Messages.Agents;

namespace CodeSpace.IntegrationTests.Workflows;

/// <summary>
/// What a test needs to see a tokened git command from outside, spelled out literally rather than through
/// <c>TokenedGitCommand</c>: whether any argv element carries the token or a URL's userinfo, and the command as it would run
/// with one part of its environment taken away — the positive controls. Without the credential-helper reset and with trace2
/// on, the operator's helpers and trace2 targets see the token; without the remote pin, the operator's URL rewrites move
/// the command; with a helper that ignores erase, git-lfs retries a refused token until the command times out.
/// </summary>
internal static class TokenedGitControls
{
    /// <summary>The tokened command's credential helper as it was before it recorded a refusal: it answers every get, so git-lfs retries a refused token without end.</summary>
    public const string HelperIgnoringErase = """!f() { test "$1" = get || return 0; printf "username=%s\npassword=%s\n" "$CODESPACE_GIT_USERNAME" "$CODESPACE_GIT_PASSWORD"; }; f""";

    private static readonly string[] TraceOff = { "GIT_TRACE2", "GIT_TRACE2_EVENT", "GIT_TRACE2_PERF" };

    /// <summary>True when an argv element carries <paramref name="token"/> — raw, percent-encoded, or base64-encoded as a Basic credential carries it — or names an http(s) URL with userinfo.</summary>
    public static bool ArgvCarriesACredential(SandboxSpec spec, string token) =>
        spec.Args.Any(a => a.Contains(token, StringComparison.Ordinal) || a.Contains(Uri.EscapeDataString(token), StringComparison.Ordinal) || Base64Forms(token).Any(f => a.Contains(f, StringComparison.Ordinal)) || NamesUserInfo(a));

    /// <summary>
    /// What any base64 text that encodes <paramref name="secret"/> contains, whatever precedes it (a Basic credential's
    /// <c>user:</c>, of any length): one string per alignment of the secret against base64's three-byte groups, each the
    /// encoding of the groups that hold the secret's bytes alone.
    /// </summary>
    public static IReadOnlyList<string> Base64Forms(string secret) => Enumerable.Range(0, 3).Select(shift => Base64Form(secret, shift)).Where(f => f.Length >= 8).ToList();

    /// <summary><paramref name="environment"/> without the credential-helper reset among its <c>GIT_CONFIG_COUNT</c> entries (renumbered) and without trace2 off.</summary>
    public static Dictionary<string, string> WithoutTheReset(IReadOnlyDictionary<string, string> environment)
    {
        var env = WithEntries(environment, e => IsHelperReset(e.Key, e.Value) ? null : e);
        foreach (var key in TraceOff) env.Remove(key);

        return env;
    }

    /// <summary><paramref name="environment"/> without the entries that map the remote's URL to itself, so the operator's <c>url.&lt;base&gt;.insteadOf</c> and <c>pushInsteadOf</c> rules apply to it.</summary>
    public static Dictionary<string, string> WithoutThePin(IReadOnlyDictionary<string, string> environment) => WithEntries(environment, e => e.Key.StartsWith("url.", StringComparison.Ordinal) ? null : e);

    /// <summary><paramref name="environment"/> with <see cref="HelperIgnoringErase"/> in place of the credential helper.</summary>
    public static Dictionary<string, string> WithTheHelperIgnoringErase(IReadOnlyDictionary<string, string> environment) => WithEntries(environment, e => IsHelper(e.Key) && e.Value.Length > 0 ? (e.Key, HelperIgnoringErase) : e);

    /// <summary>True when the command runs with no part of a tokened command's environment: no config entries, no credential variables, trace2 left alone.</summary>
    public static bool RunsUntokened(SandboxSpec spec) => !spec.Environment.Keys.Any(k => k.StartsWith("GIT_CONFIG_", StringComparison.Ordinal) || k.StartsWith("CODESPACE_GIT_", StringComparison.Ordinal) || TraceOff.Contains(k)) && spec.ConfigHomeEnvVars.Count == 0;

    /// <summary><paramref name="environment"/> with each <c>GIT_CONFIG_COUNT</c> entry passed through <paramref name="map"/> — dropped where it returns null — and the rest renumbered.</summary>
    private static Dictionary<string, string> WithEntries(IReadOnlyDictionary<string, string> environment, Func<(string Key, string Value), (string Key, string Value)?> map)
    {
        var env = new Dictionary<string, string>(environment);

        if (!env.TryGetValue("GIT_CONFIG_COUNT", out var raw)) return env;

        var count = int.Parse(raw);
        var kept = Enumerable.Range(0, count).Select(i => map((env[$"GIT_CONFIG_KEY_{i}"], env[$"GIT_CONFIG_VALUE_{i}"]))).OfType<(string Key, string Value)>().ToList();

        for (var i = 0; i < count; i++) { env.Remove($"GIT_CONFIG_KEY_{i}"); env.Remove($"GIT_CONFIG_VALUE_{i}"); }
        for (var i = 0; i < kept.Count; i++) { env[$"GIT_CONFIG_KEY_{i}"] = kept[i].Key; env[$"GIT_CONFIG_VALUE_{i}"] = kept[i].Value; }
        env["GIT_CONFIG_COUNT"] = kept.Count.ToString();

        return env;
    }

    private static bool IsHelper(string key) => key.StartsWith("credential.", StringComparison.Ordinal) && key.EndsWith(".helper", StringComparison.Ordinal);

    private static bool IsHelperReset(string key, string value) => IsHelper(key) && value.Length == 0;

    /// <summary>The base64 of <paramref name="secret"/> after <paramref name="shift"/> other bytes, cut to the groups that hold the secret's bytes alone.</summary>
    private static string Base64Form(string secret, int shift)
    {
        var bytes = Encoding.UTF8.GetBytes(secret);
        var encoded = Convert.ToBase64String(new byte[shift].Concat(bytes).ToArray());
        var start = shift == 0 ? 0 : 4;
        var end = (shift + bytes.Length) / 3 * 4;

        return end > start ? encoded[start..end] : "";
    }

    private static bool NamesUserInfo(string arg) =>
        Uri.TryCreate(arg, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps) && uri.UserInfo.Length > 0;
}

/// <summary>
/// Scans every file under a root — or, given a prefix, under each of the root's directories so named that appeared after the
/// watch began — for a secret, raw or base64-encoded as a Basic credential carries it, over and over until disposed,
/// recording each file it was found in. So a test can say a token never reached the disk at any point during an operation,
/// not only once the operation ended.
/// </summary>
internal sealed class TokenOnDiskWatch : IAsyncDisposable
{
    private const long LargestFileScanned = 4 * 1024 * 1024;

    private readonly byte[][] _needles;
    private readonly string _root;
    private readonly string? _childPrefix;
    private readonly HashSet<string> _before;
    private readonly HashSet<string> _hits = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _stopping = new();
    private readonly Task _scanning;
    private int _scans;

    public TokenOnDiskWatch(string secret, string root, string? childPrefix = null)
    {
        _needles = TokenedGitControls.Base64Forms(secret).Prepend(secret).Select(Encoding.UTF8.GetBytes).ToArray();
        _root = root;
        _childPrefix = childPrefix;
        _before = childPrefix is null ? new(StringComparer.Ordinal) : Directories(root).ToHashSet(StringComparer.Ordinal);
        _scanning = Task.Run(ScanUntilStoppedAsync);
    }

    /// <summary>Completed passes over the tree — a fixture check that the watch was looking while the operation ran.</summary>
    public int Scans => Volatile.Read(ref _scans);

    /// <summary>Every file the secret was seen in, at any point.</summary>
    public IReadOnlyList<string> Hits { get { lock (_hits) return _hits.Order(StringComparer.Ordinal).ToList(); } }

    private async Task ScanUntilStoppedAsync()
    {
        while (!_stopping.IsCancellationRequested)
        {
            ScanOnce();
            Interlocked.Increment(ref _scans);

            try { await Task.Delay(2, _stopping.Token); }
            catch (OperationCanceledException) { }
        }
    }

    private void ScanOnce()
    {
        foreach (var file in WatchedRoots().SelectMany(Files))
            if (Holds(file)) lock (_hits) _hits.Add(file);
    }

    private IEnumerable<string> WatchedRoots() => _childPrefix is null ? new[] { _root } : Directories(_root).Where(d => Path.GetFileName(d).StartsWith(_childPrefix, StringComparison.Ordinal) && !_before.Contains(d)).ToList();

    private static IEnumerable<string> Directories(string root)
    {
        try { return Directory.Exists(root) ? Directory.EnumerateDirectories(root).ToList() : Array.Empty<string>(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return Array.Empty<string>(); }
    }

    /// <summary>The files under <paramref name="root"/> right now; a tree changing under the walk yields what was read before it moved.</summary>
    private static IReadOnlyList<string> Files(string root)
    {
        var files = new List<string>();

        try
        {
            if (Directory.Exists(root)) files.AddRange(Directory.EnumerateFiles(root, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint }));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }

        return files;
    }

    private bool Holds(string file)
    {
        try
        {
            var info = new FileInfo(file);
            return info.Exists && info.Length <= LargestFileScanned && Contains(File.ReadAllBytes(file));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    private bool Contains(byte[] content) => _needles.Any(n => content.AsSpan().IndexOf(n) >= 0);

    /// <summary>Stop scanning, after one last pass over what is on disk now. Idempotent.</summary>
    public async Task StopAsync()
    {
        if (_stopping.IsCancellationRequested) return;

        _stopping.Cancel();
        await _scanning;
        ScanOnce();
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _stopping.Dispose();
    }
}
