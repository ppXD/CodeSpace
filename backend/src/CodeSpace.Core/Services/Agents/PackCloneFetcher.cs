using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Services.Agents.Sandbox;
using CodeSpace.Core.Services.Agents.Sandbox.Runners;
using CodeSpace.Core.Services.Agents.Workspace;
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
/// </summary>
public sealed class PackCloneFetcher : IPackSourceFetcher, IWorkspaceJanitor, ISingletonDependency
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

        var args = BuildCloneArgs(url, reference, dir);

        SandboxResult result;
        try
        {
            result = await _runners.Resolve(LocalProcessRunner.LocalKind)
                .RunAsync(new SandboxSpec { Command = "git", Args = args, TimeoutSeconds = CloneTimeoutSeconds }, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            TryDeleteDirectory(dir);   // never leak a partial clone, even on an unexpected throw / cancellation
            throw;
        }

        if (result.Status != SandboxStatus.Success)
        {
            TryDeleteDirectory(dir);   // clone failed → reclaim the partial dir immediately
            throw new PackImportException($"git clone of '{url}' failed ({result.Status}, exit {result.ExitCode}): {Summarize(result.Stderr)}");
        }

        return new PackCheckout(dir);
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

    private static string Summarize(string stderr) =>
        string.IsNullOrWhiteSpace(stderr) ? "(no stderr)" : stderr.Trim().Replace("\n", " ");
}
