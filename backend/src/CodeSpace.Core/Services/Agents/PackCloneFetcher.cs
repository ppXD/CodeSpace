using System.Text.RegularExpressions;
using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Services.Agents.Sandbox;
using CodeSpace.Core.Services.Agents.Workspace;
using CodeSpace.Core.Services.Agents.Workspace.Providers;
using CodeSpace.Messages.Agents;
using Microsoft.Extensions.Logging;

namespace CodeSpace.Core.Services.Agents;

/// <summary>
/// Default <see cref="IPackSourceFetcher"/> — clones a pack URL (allowlist-guarded) into a transient dir under
/// <see cref="PackClonesRoot"/> via the "local" sandbox runner's <c>git clone --depth 1</c> (the SAME proven git
/// path the workspace provider uses; the clone is a trusted PLATFORM op — its CONTENT is later walked read-only).
///
/// <para>Three-layered disk hygiene so transient clones never accumulate into an out-of-disk: (1) the returned
/// <see cref="PackCheckout"/> deletes its dir on dispose (the caller's <c>using</c>, the happy path); (2) a clone
/// FAILURE (or any throw mid-clone) reclaims the partial dir immediately; (3) <see cref="IWorkspaceJanitor"/> — the
/// crash-safety backstop: the recurring sweep (which fans out over every janitor) ages out a clone orphaned by a
/// worker that died between clone and dispose.</para>
///
/// <para>A pasted URL can carry a credential in its userinfo (<see cref="PastedSecret"/>). The clone runs as a tokened command:
/// it names the remote without the credential and carries it in its environment, so no argv carries it, git writes none into
/// the checkout's origin, and the operator's credential helpers and trace2 targets never see it. The clone still runs in a
/// directory only this worker's uid can read, and origin is still rewritten to the URL without the credential once cloned,
/// as belts; a clone failure names the URL without it and redacts it from git's stderr, since that message reaches the API
/// error body, the UI and the mediator's error log.</para>
/// </summary>
public sealed partial class PackCloneFetcher : IPackSourceFetcher, IWorkspaceJanitor, ISingletonDependency
{
    /// <summary>Operators tune how long an orphaned pack clone lingers before the janitor reclaims it (a TimeSpan, e.g. "00:30:00"); default 1h. Pinned by a test (Rule 8). MUST exceed the maximum possible import duration so the age-based sweep never deletes a live clone.</summary>
    public const string StaleThresholdEnvVar = "CODESPACE_PACK_CLONE_STALE_THRESHOLD";

    private static readonly TimeSpan DefaultStaleThreshold = TimeSpan.FromHours(1);

    private const int CloneTimeoutSeconds = 120;

    /// <summary>Root for transient pack clones, under the worker's temp dir — a dedicated namespace the janitor reclaims wholesale.</summary>
    internal static readonly string PackClonesRoot = Path.Combine(Path.GetTempPath(), "codespace-pack-clones");

    private readonly IPackHostAllowlist _allowlist;
    private readonly ISandboxRunnerRegistry _runners;
    private readonly ILogger<PackCloneFetcher> _logger;

    public PackCloneFetcher(IPackHostAllowlist allowlist, ISandboxRunnerRegistry runners, ILogger<PackCloneFetcher> logger)
    {
        _allowlist = allowlist;
        _runners = runners;
        _logger = logger;
    }

    /// <summary>The janitor family this reclaims (a label; not a workspace provider kind).</summary>
    public string Kind => "pack-source";

    public async Task<PackCheckout> FetchAsync(string url, string? reference, CancellationToken cancellationToken)
    {
        _allowlist.EnsureAllowed(url);   // egress guard — refuse a non-allowlisted / non-https host BEFORE any clone

        Directory.CreateDirectory(PackClonesRoot);
        var dir = Path.Combine(PackClonesRoot, Guid.NewGuid().ToString("N"));

        try
        {
            CreateOwnerOnlyDirectory(dir);
            await CloneAsync(url, reference, dir, cancellationToken).ConfigureAwait(false);
            await StripPastedCredentialAsync(url, dir, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            TryDeleteDirectory(dir);   // never leak a partial clone, or one still holding a pasted credential, even on an unexpected throw / cancellation
            throw;
        }

        return new PackCheckout(dir);
    }

    /// <summary>
    /// The clone's directory, readable by this worker's uid alone, before git runs — a belt: the clone names the remote
    /// without the pasted credential, so git writes none into <c>.git/config</c>, and the checkout stays the import's
    /// private copy until it is walked (or until the janitor reclaims it when the worker dies mid-clone).
    /// </summary>
    private static void CreateOwnerOnlyDirectory(string dir)
    {
        Directory.CreateDirectory(dir);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    /// <summary>Run the clone; a failure throws a <see cref="PackImportException"/> that names no pasted credential.</summary>
    private async Task CloneAsync(string url, string? reference, string dir, CancellationToken cancellationToken)
    {
        var result = await _runners.Resolve(SandboxKinds.Local).RunAsync(BuildCloneSpec(url, reference, dir), cancellationToken).ConfigureAwait(false);

        if (result.Status != SandboxStatus.Success)
            throw new PackImportException(CloneFailedMessage(url, result));
    }

    /// <summary>
    /// The import walks this checkout (a worker that dies mid-import leaves it on disk for the janitor), so its origin must
    /// hold no credential. The clone already named the URL without it; as a belt, rewrite origin to that URL through the
    /// workspace provider's own strip: set-url, else remove origin, else a <see cref="WorkspaceException"/> — and the caller
    /// deletes the clone on the way out.
    /// </summary>
    private async Task StripPastedCredentialAsync(string url, string dir, CancellationToken cancellationToken)
    {
        var cleanUrl = WithoutPastedCredential(url);

        if (cleanUrl == url) return;

        await LocalGitWorkspaceProvider.StripTokenFromRemoteAsync(_runners.Resolve(SandboxKinds.Local), CloneTimeoutSeconds, _logger, cleanUrl, dir, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The git argv for a hardened shallow clone. <c>-c http.followRedirects=false</c> keeps the transport on the
    /// allowlist-validated host: without it git follows the initial smart-HTTP probe's 30x and uses the redirected
    /// URL as the base for follow-ups, so an allowlisted host could bounce egress to an internal host (a bypass at
    /// the transport layer, BELOW the URL-host allowlist) — a redirect now errors the clone instead. (A same-host
    /// redirect, e.g. a renamed GitHub repo's 301, also errors; the operator re-pastes the current URL — a small,
    /// safe cost for closing the cross-host SSRF vector.) <c>--</c> ends options so a url/ref beginning with <c>-</c>
    /// can never smuggle a git flag. Pure + internal so the hardening is pinned by a test (Rule 8).
    /// </summary>
    internal static IReadOnlyList<string> BuildCloneArgs(string url, string? reference, string dir)
    {
        var args = new List<string> { "-c", "http.followRedirects=false", "clone", "--depth", "1" };

        if (!string.IsNullOrWhiteSpace(reference)) { args.Add("--branch"); args.Add(reference); }

        args.Add("--");
        args.Add(url);
        args.Add(dir);

        return args;
    }

    /// <summary>
    /// The clone as the runner gets it: <see cref="BuildCloneArgs"/> in <paramref name="dir"/>, with the network. A pasted URL
    /// carrying a credential (<see cref="PastedSecret"/>) clones as a <see cref="TokenedGitCommand"/>: its argv names the URL
    /// without the userinfo, and the whole userinfo travels in its environment — a token pasted as the user alone too, which
    /// a stored URL's bare user would not, since that names an account.
    /// </summary>
    internal static SandboxSpec BuildCloneSpec(string url, string? reference, string dir)
    {
        var remote = PastedSecret(url) is null ? new TokenedGitCommand.Remote(url, null, null) : TokenedGitCommand.FromUserInfo(url);

        return TokenedGitCommand.Spec(remote, new SandboxSpec { Command = "git", Args = BuildCloneArgs(remote.Url, reference, dir), WorkingDirectory = dir, TimeoutSeconds = CloneTimeoutSeconds, AllowNetwork = true });
    }

    // ── IWorkspaceJanitor: reclaim pack clones orphaned by a crashed worker ──────────────────────────

    public Task<int> SweepStaleAsync(CancellationToken cancellationToken) =>
        Task.FromResult(SweepStale(PackClonesRoot, ReadStaleThreshold(), DateTime.UtcNow, cancellationToken));

    /// <summary>The configured staleness threshold, or the 1h default when the env var is absent / unparseable / non-positive. Pure + internal so it's unit-pinned.</summary>
    internal static TimeSpan ReadStaleThreshold()
    {
        var raw = Environment.GetEnvironmentVariable(StaleThresholdEnvVar);

        return TimeSpan.TryParse(raw, out var parsed) && parsed > TimeSpan.Zero ? parsed : DefaultStaleThreshold;
    }

    /// <summary>A clone is stale when the time since its last write exceeds the threshold (the threshold far exceeds any import, so the sweep can never touch a live clone). Pure + internal so it's unit-pinned.</summary>
    internal static bool IsStale(DateTime lastWriteUtc, DateTime nowUtc, TimeSpan olderThan) => nowUtc - lastWriteUtc > olderThan;

    /// <summary>The filesystem sweep, parameterised on root + clock so it's driven by an isolated test against a controlled temp dir. Returns the count reclaimed.</summary>
    internal static int SweepStale(string root, TimeSpan olderThan, DateTime nowUtc, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(root)) return 0;

        var reclaimed = 0;

        foreach (var directory in Directory.EnumerateDirectories(root))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!IsStale(Directory.GetLastWriteTimeUtc(directory), nowUtc, olderThan)) continue;

            TryDeleteDirectory(directory);
            if (!Directory.Exists(directory)) reclaimed++;
        }

        return reclaimed;
    }

    private static void TryDeleteDirectory(string directory)
    {
        try
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
        catch
        {
            // Best-effort: a leaked temp dir on the worker's ephemeral disk is reclaimed by the next janitor sweep.
        }
    }

    /// <summary>
    /// The clone failure as the operator reads it — in the API error body, the UI and the mediator's error log: the URL
    /// without its pasted credential, and git's stderr with that credential redacted. git hides a password, but when a token
    /// is pasted as the user alone it asks for a password and names that user. Pure + internal so it is unit-pinned.
    /// </summary>
    internal static string CloneFailedMessage(string url, SandboxResult result) =>
        $"git clone of '{WithoutPastedCredential(url)}' failed ({result.Status}, exit {result.ExitCode}): {RedactPastedCredential(url, Summarize(result.Stderr))}";

    /// <summary>
    /// The part of a pasted http(s) URL's userinfo that carries its credential: the password when one is given
    /// (<c>x-access-token:&lt;token&gt;@</c>, <c>oauth2:&lt;token&gt;@</c>), else the user — a token pasted as the user alone
    /// (<c>&lt;token&gt;@</c>). Null for a URL without userinfo and for any other scheme: git never sends an ssh URL's user as a
    /// credential, and its <c>git@</c> names an account.
    /// </summary>
    private static string? PastedSecret(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)) return null;

        var (user, password) = uri.UserInfo.Split(':', 2) is [var u, var p] ? (u, p) : (uri.UserInfo, "");
        var secret = password.Length > 0 ? password : user;

        return secret.Length > 0 ? secret : null;
    }

    /// <summary>
    /// <paramref name="text"/> without the pasted credential. First the userinfo of every http(s) URL in it (<see cref="UrlUserInfo"/>),
    /// whichever part carries the token — <c>&lt;token&gt;:x-oauth-basic@</c> puts it in the user — and in whatever spelling. Then
    /// <see cref="PastedSecret"/> as bare text in each spelling git or a remote may echo it: a decoded user can hold the '/' or
    /// '@' that ends a URL's userinfo, and a remote can echo the token it was handed. A user beside a password is not bare
    /// text to redact: it names an account, and git names it only inside a URL, since it asks for a password only when none
    /// was given — masking it would mask every 'a' in git's reason, or the owner in the repository's path.
    /// </summary>
    private static string RedactPastedCredential(string url, string text) =>
        new SecretRedactor(Spellings(PastedSecret(url))).Redact(UrlUserInfo().Replace(text, "${scheme}" + SecretRedactor.Placeholder + "@"));

    /// <summary><paramref name="secret"/> in each spelling git may echo it: as written, decoded (git 2.33 names a user decoded) and re-encoded (later git re-encodes it).</summary>
    private static IEnumerable<string> Spellings(string? secret) =>
        secret is null ? Array.Empty<string>() : new[] { secret, Uri.UnescapeDataString(secret), Uri.EscapeDataString(Uri.UnescapeDataString(secret)) };

    /// <summary>An http(s) URL's userinfo in free text: what follows the scheme up to an '@', with no '/', '?', '#', whitespace or quote between.</summary>
    [GeneratedRegex(@"(?<scheme>https?://)[^/?#@\s'""]+@", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UrlUserInfo();

    /// <summary>The URL origin keeps and an error names: without its userinfo (<see cref="RemoteTipResolver.SanitizeUrl"/>) when that carries a <see cref="PastedSecret"/>, otherwise as written.</summary>
    private static string WithoutPastedCredential(string url) => PastedSecret(url) is null ? url : RemoteTipResolver.SanitizeUrl(url);

    private static string Summarize(string stderr) =>
        string.IsNullOrWhiteSpace(stderr) ? "(no stderr)" : stderr.Trim().Replace("\n", " ");
}
