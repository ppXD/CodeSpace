using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Services.Agents.Sandbox;
using CodeSpace.Core.Services.Agents.Sandbox.Runners;
using CodeSpace.Messages.Agents;
using Microsoft.Extensions.Logging;

namespace CodeSpace.Core.Services.Agents.Workspace.Providers;

/// <summary>
/// v0 workspace provider: prepares the working copy as a <c>git clone</c> on the worker's own
/// filesystem, run THROUGH the "local" <see cref="ISandboxRunner"/> so it inherits the same process /
/// timeout / cancellation handling (workspace prep is just sandboxed git). Pairs with
/// <c>LocalProcessRunner</c> — both <see cref="Kind"/> "local". A future K8s provider clones into the
/// pod volume behind the same contract.
///
/// <para><b>Secret hygiene:</b> the access token is embedded in the clone URL for the clone command
/// only, then the origin remote is rewritten to the tokenless URL so the persisted <c>.git/config</c>
/// never retains it, and any token text is redacted from surfaced error output. (The transient argv
/// exposure is acceptable on a single-tenant local worker; the K8s runner injects via an in-pod
/// credential helper instead.)</para>
/// </summary>
public sealed class LocalGitWorkspaceProvider : IWorkspaceProvider, IWorkspaceJanitor, IWorkspacePathCapture, ISingletonDependency
{
    private const int CloneTimeoutSeconds = 300;
    private const int CaptureTimeoutSeconds = 120;
    private const int PushTimeoutSeconds = 300;
    /// <summary>Root for transient agent scratch clones (agent workspaces + branch-integration clones), under the worker's temp dir. Internal so the <c>LocalGitBranchIntegrator</c> stages its integration clone here too and the same janitor reclaims a leaked one.</summary>
    internal static readonly string WorkspacesRoot = Path.Combine(Path.GetTempPath(), "codespace-agent-workspaces");

    /// <summary>
    /// Operators tune how long an orphaned workspace lingers before the janitor reclaims it (a TimeSpan,
    /// e.g. "02:00:00"); default 2h. Pinned by a test (Rule 8). MUST exceed the maximum possible run
    /// duration so the age-based sweep can never delete a live workspace.
    /// </summary>
    public const string StaleThresholdEnvVar = "CODESPACE_AGENT_WORKSPACE_STALE_THRESHOLD";

    private static readonly TimeSpan DefaultStaleThreshold = TimeSpan.FromHours(2);

    private readonly ISandboxRunnerRegistry _runners;
    private readonly ILogger<LocalGitWorkspaceProvider> _logger;

    public LocalGitWorkspaceProvider(ISandboxRunnerRegistry runners, ILogger<LocalGitWorkspaceProvider> logger)
    {
        _runners = runners;
        _logger = logger;
    }

    public string Kind => LocalProcessRunner.LocalKind;

    public async Task<IWorkspaceHandle> PrepareAsync(WorkspaceProvisionRequest request, CancellationToken cancellationToken)
    {
        if (request.Repositories.Count == 0)
            throw new WorkspaceException("Workspace provision has no repositories to clone.");

        Directory.CreateDirectory(WorkspacesRoot);

        var workspaceRoot = Path.Combine(WorkspacesRoot, Guid.NewGuid().ToString("N"));
        var single = request.Repositories.Count == 1;

        // Fail loud BEFORE any clone if a multi-repo provision's per-repo mount segments are unsafe or collide.
        // This is the universal choke point every caller (resolver, run-command, a future planner that AUTHORS specs)
        // funnels through, so a spec can never traverse outside the workspace root nor clobber a sibling clone.
        if (!single) ValidateMultiRepoLayout(request);

        try
        {
            // Multi-repo: pre-create the root so each repo can clone into its own <root>/<path> subdir. Single-repo:
            // leave the root uncreated and clone FLAT into it (git creates the dir) — byte-identical to before.
            if (!single) Directory.CreateDirectory(workspaceRoot);

            var materialized = new List<MaterializedRepo>(request.Repositories.Count);

            foreach (var repo in request.Repositories)
            {
                cancellationToken.ThrowIfCancellationRequested();
                materialized.Add(await MaterializeAsync(repo, RepoDirectory(workspaceRoot, repo, single), cancellationToken).ConfigureAwait(false));
            }

            // Only a genuine multi-repo workspace gets a manifest at the root; a single-repo clone is left pristine.
            if (!single) await WriteWorkspaceManifestAsync(workspaceRoot, request, materialized, cancellationToken).ConfigureAwait(false);

            var primaryAlias = (request.Primary ?? throw new WorkspaceException("Workspace provision has no resolvable primary repository.")).Alias;
            var cwd = ResolveCwd(request.CwdMode, workspaceRoot, materialized, primaryAlias, single);

            return new LocalWorkspaceHandle(workspaceRoot, cwd, materialized, primaryAlias, _runners.Resolve(Kind), _logger);
        }
        catch
        {
            TryDeleteDirectory(workspaceRoot);
            throw;
        }
    }

    /// <summary>The on-disk directory a repo clones into: the workspace root itself for a single-repo provision (flat, byte-identical), else <c>&lt;root&gt;/&lt;path ?? alias&gt;</c> — the segment is <see cref="ValidateMultiRepoLayout"/>-checked to be a single safe directory name, so the combine can never escape the root.</summary>
    private static string RepoDirectory(string workspaceRoot, WorkspaceRepositoryProvision repo, bool single) =>
        single ? workspaceRoot : Path.Combine(workspaceRoot, string.IsNullOrWhiteSpace(repo.Path) ? repo.Alias : repo.Path);

    /// <summary>
    /// Validate a multi-repo provision's per-repo mount layout BEFORE cloning: every alias is unique, every mount
    /// segment (<c>Path ?? Alias</c>) is a SAFE single directory name, and no two repos map to the same subdir.
    /// Throws a clear <see cref="WorkspaceException"/> on any violation — a fail-loud author-time-style error rather
    /// than a path traversal outside the workspace root, a silent sibling clobber, or an opaque mid-clone git error.
    /// Single-repo provisions are exempt (they clone flat into the GUID root regardless of alias/path).
    /// </summary>
    private static void ValidateMultiRepoLayout(WorkspaceProvisionRequest request)
    {
        var aliases = new HashSet<string>(StringComparer.Ordinal);
        var segments = new HashSet<string>(StringComparer.Ordinal);

        foreach (var repo in request.Repositories)
        {
            if (!aliases.Add(repo.Alias))
                throw new WorkspaceException($"Duplicate repository alias '{repo.Alias}' in the workspace provision — each repository needs a unique alias.");

            var segment = string.IsNullOrWhiteSpace(repo.Path) ? repo.Alias : repo.Path;

            if (!IsSafeMountSegment(segment))
                throw new WorkspaceException($"Unsafe repository mount path '{segment}' (alias '{repo.Alias}') — a repository path must be a single directory name, never rooted, '.', '..', or containing a path separator.");

            if (!segments.Add(segment))
                throw new WorkspaceException($"Two repositories map to the same mount path '{segment}' in the workspace provision — each repository needs a distinct path.");
        }
    }

    /// <summary>A safe mount segment is a single directory NAME: non-empty, not <c>.</c>/<c>..</c>, not rooted, and free of path separators — so <c>Path.Combine(root, segment)</c> can never resolve outside <c>root</c>. Pure + internal so it's unit-pinned.</summary>
    internal static bool IsSafeMountSegment(string segment) =>
        !string.IsNullOrWhiteSpace(segment)
        && segment != "." && segment != ".."
        && segment.IndexOf('/') < 0 && segment.IndexOf('\\') < 0
        && !Path.IsPathRooted(segment);

    /// <summary>Clone one repo, strip its token from the persisted remote, and read its base revision — the per-repo unit of the workspace.</summary>
    private async Task<MaterializedRepo> MaterializeAsync(WorkspaceRepositoryProvision repo, string directory, CancellationToken cancellationToken)
    {
        await CloneAsync(repo.CloneRequest, directory, cancellationToken).ConfigureAwait(false);

        if (!string.IsNullOrEmpty(repo.CloneRequest.Token))
            await StripTokenFromRemoteAsync(repo.CloneRequest.RepositoryUrl, directory, cancellationToken).ConfigureAwait(false);

        var baseSha = await ReadBaseShaAsync(directory, cancellationToken).ConfigureAwait(false);

        // Carry the SAME short-lived clone credential forward (in-memory only, never persisted / never in .git/config —
        // origin was stripped) so a later push re-injects auth into the push argv without a second auth round-trip.
        return new MaterializedRepo(repo.Alias, directory, repo.Access, repo.CloneRequest.RepositoryUrl, repo.CloneRequest.TokenUsername, repo.CloneRequest.Token, baseSha, repo.CloneRequest.Ref);
    }

    /// <summary>Where the harness runs: Auto → the primary repo's dir for one repo (the invariant), the workspace root for many; or the explicit mode.</summary>
    private static string ResolveCwd(WorkspaceCwdMode mode, string workspaceRoot, IReadOnlyList<MaterializedRepo> repos, string primaryAlias, bool single) => mode switch
    {
        WorkspaceCwdMode.WorkspaceRoot => workspaceRoot,
        WorkspaceCwdMode.PrimaryRepo => PrimaryDir(repos, primaryAlias),
        _ => single ? PrimaryDir(repos, primaryAlias) : workspaceRoot,   // Auto
    };

    private static string PrimaryDir(IReadOnlyList<MaterializedRepo> repos, string primaryAlias) =>
        repos.First(r => r.Alias == primaryAlias).Directory;

    /// <summary>Write a WORKSPACE.md at the multi-repo root so the harness knows it is in a multi-repo workspace, which repos are present, which is primary, and each one's access. Best-effort: a manifest write failure must not fail provisioning (the clones already succeeded).</summary>
    private async Task WriteWorkspaceManifestAsync(string workspaceRoot, WorkspaceProvisionRequest request, IReadOnlyList<MaterializedRepo> repos, CancellationToken cancellationToken)
    {
        var primaryAlias = request.Primary?.Alias;

        var lines = repos.Select(r =>
        {
            var role = r.Alias == primaryAlias ? "primary, " : "";
            var access = r.Access == WorkspaceAccess.Write ? "writable" : "read-only context";
            return $"- `{r.Alias}/` ({role}{access})";
        });

        var body = $"# Workspace\n\nThis is a MULTI-REPO workspace. The harness runs at the workspace root; each repository is a sibling folder below.\n\n{string.Join('\n', lines)}\n\nMake coordinated changes only in the writable repositories; treat read-only repositories as context.\n";

        try
        {
            await File.WriteAllTextAsync(Path.Combine(workspaceRoot, "WORKSPACE.md"), body, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to write WORKSPACE.md to {Root}; the multi-repo workspace is materialised but unannotated", workspaceRoot);
        }
    }

    /// <summary>One cloned repo's runtime state (Rule 18-adjacent — a private provider noun): alias, on-disk dir, access, the in-memory clone credential carried forward for a later push, the cloned base SHA, and the base ref (the ref the clone was performed at — usually a branch, but a tag when one was authored) the produced branch should target.</summary>
    private sealed record MaterializedRepo(string Alias, string Directory, WorkspaceAccess Access, string RepositoryUrl, string? TokenUsername, string? Token, string BaseSha, string? BaseBranch);

    /// <summary>Record the cloned HEAD revision so <see cref="LocalWorkspaceHandle.CaptureChangesAsync"/> can diff the agent's work against it — robust whether the agent commits or leaves changes uncommitted.</summary>
    private async Task<string> ReadBaseShaAsync(string directory, CancellationToken cancellationToken)
    {
        var result = await _runners.Resolve(Kind).RunAsync(
            new SandboxSpec { Command = "git", Args = new[] { "-C", directory, "rev-parse", "HEAD" }, TimeoutSeconds = CloneTimeoutSeconds }, cancellationToken).ConfigureAwait(false);

        if (result.Status != SandboxStatus.Success)
            throw new WorkspaceException($"Could not read the workspace base revision (exit {result.ExitCode}): {Summarize(result.Stderr)}");

        return result.Stdout.Trim();
    }

    // ── IWorkspacePathCapture: capture with no live handle (the re-attach path) ──────────────────────

    /// <summary>
    /// The re-attach path's capture: same git-diff shape as <see cref="LocalWorkspaceHandle.CaptureRepoChangesAsync"/>,
    /// against a bare <paramref name="directory"/> + <paramref name="baseSha"/> instead of a <c>MaterializedRepo</c> —
    /// there is no clone credential to redact here (re-attach never re-resolves a push token), so errors surface the
    /// raw stderr. Throws <see cref="WorkspaceException"/> when the directory was already reclaimed by the janitor or
    /// a git command fails — the caller (best-effort, like every other capture) logs and keeps the result unchanged.
    /// </summary>
    public async Task<WorkspaceChanges> CaptureChangesFromPathAsync(string directory, string baseSha, CancellationToken cancellationToken)
    {
        var runner = _runners.Resolve(Kind);

        async Task<string> RunOrThrowAsync(IReadOnlyList<string> args)
        {
            var result = await runner.RunAsync(new SandboxSpec { Command = "git", Args = args, WorkingDirectory = directory, TimeoutSeconds = CaptureTimeoutSeconds }, cancellationToken).ConfigureAwait(false);

            if (result.Status != SandboxStatus.Success)
                throw new WorkspaceException($"git {string.Join(' ', args)} failed (exit {result.ExitCode}): {Summarize(result.Stderr)}");

            return result.Stdout;
        }

        await RunOrThrowAsync(new[] { "add", "-A" }).ConfigureAwait(false);

        var patch = await RunOrThrowAsync(new[] { "diff", "--cached", "--no-color", baseSha }).ConfigureAwait(false);
        var names = await RunOrThrowAsync(new[] { "diff", "--cached", "--name-only", baseSha }).ConfigureAwait(false);
        var numstat = await RunOrThrowAsync(new[] { "diff", "--cached", "--numstat", baseSha }).ConfigureAwait(false);

        return new WorkspaceChanges
        {
            Patch = patch,
            BaseSha = baseSha,
            ChangedFiles = names.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            FileStats = NumstatParser.Parse(numstat),
        };
    }

    // ── IWorkspaceJanitor: reclaim clones orphaned by a crashed worker ───────────────────────────────

    /// <summary>Reclaim local clones older than the staleness threshold. No-op when the root was never created.</summary>
    public Task<int> SweepStaleAsync(CancellationToken cancellationToken) =>
        Task.FromResult(SweepStale(WorkspacesRoot, ReadStaleThreshold(), DateTime.UtcNow, cancellationToken));

    /// <summary>The configured staleness threshold, or the 2h default when the env var is absent / unparseable / non-positive. Pure + internal so it's unit-pinned.</summary>
    internal static TimeSpan ReadStaleThreshold()
    {
        var raw = Environment.GetEnvironmentVariable(StaleThresholdEnvVar);

        return TimeSpan.TryParse(raw, out var parsed) && parsed > TimeSpan.Zero ? parsed : DefaultStaleThreshold;
    }

    /// <summary>
    /// A workspace is stale when the time since its last write exceeds the threshold. Last-write (not
    /// creation) is used because it's settable cross-platform (Linux has no birthtime-set syscall) AND is
    /// sound here: the threshold far exceeds any run, so a dir untouched that long cannot be a live run.
    /// Pure + internal so it's unit-pinned without touching the filesystem or clock.
    /// </summary>
    internal static bool IsStale(DateTime lastWriteUtc, DateTime nowUtc, TimeSpan olderThan) => nowUtc - lastWriteUtc > olderThan;

    /// <summary>The filesystem sweep, parameterised on root + clock so it's driven by an isolated test against a controlled temp dir.</summary>
    internal int SweepStale(string root, TimeSpan olderThan, DateTime nowUtc, CancellationToken cancellationToken)
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

        if (reclaimed > 0)
            _logger.LogInformation("Reclaimed {Count} stale agent workspace(s) older than {Threshold}", reclaimed, olderThan);

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
            // Best-effort: a leaked temp dir on the worker's ephemeral disk is harmless.
        }
    }

    private async Task CloneAsync(WorkspaceRequest request, string directory, CancellationToken cancellationToken)
    {
        var url = BuildAuthenticatedUrl(request.RepositoryUrl, request.TokenUsername, request.Token);

        var checkoutRef = await ResolveCheckoutRefAsync(request, url, cancellationToken).ConfigureAwait(false);

        var args = new List<string> { "clone" };

        if (request.Depth > 0) { args.Add("--depth"); args.Add(request.Depth.ToString()); }
        if (!string.IsNullOrWhiteSpace(checkoutRef)) { args.Add("--branch"); args.Add(checkoutRef); }

        args.Add(url);
        args.Add(directory);

        var result = await RunGitAsync(args, cancellationToken).ConfigureAwait(false);

        if (result.Status != SandboxStatus.Success)
            throw new WorkspaceException($"git clone failed (exit {result.ExitCode}): {Redact(Summarize(result.Stderr), request.Token)}");

        if (!string.IsNullOrWhiteSpace(request.PinnedSha))
            await MaterializePinAsync(request, directory, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// S1: materialize the pinned base EXACTLY, cheapest rung first. (1) The pin is usually the branch tip the
    /// shallow clone just fetched — a local object check + detached checkout keeps the clone SHALLOW, so the common
    /// launch pays nothing over the pre-S1 clone. (2) A tip that advanced since launch: fetch the pin BY SHA
    /// (best-effort — servers without allow-*-sha1-in-want refuse it). (3) Unshallow the cloned branch's history —
    /// the pin is an ancestor of the launch-time tip unless the branch was rewritten. Still absent after every rung
    /// ⇒ the checkout fails LOUD: the pin is a freshness guarantee, never a suggestion (a force-push that orphaned
    /// the pin must surface, never a silent tip fallback).
    /// </summary>
    private async Task MaterializePinAsync(WorkspaceRequest request, string directory, CancellationToken cancellationToken)
    {
        var pin = request.PinnedSha!;

        if (!await CommitExistsLocallyAsync(directory, pin, cancellationToken).ConfigureAwait(false))
        {
            await RunGitAsync(new[] { "-C", directory, "fetch", "origin", pin }, cancellationToken).ConfigureAwait(false);   // best-effort; the checkout below is the arbiter

            if (!await CommitExistsLocallyAsync(directory, pin, cancellationToken).ConfigureAwait(false) && request.Depth > 0)
            {
                await RunGitAsync(new[] { "-C", directory, "fetch", "--unshallow", "origin" }, cancellationToken).ConfigureAwait(false);

                // The shallow clone was SINGLE-BRANCH — a pin living on a branch the clone never fetched (a ref/pin
                // context mismatch, or a reviewer cloning the default while the pin rides the operator's branch)
                // needs the full ref space before the checkout can arbitrate.
                if (!await CommitExistsLocallyAsync(directory, pin, cancellationToken).ConfigureAwait(false))
                    await RunGitAsync(new[] { "-C", directory, "fetch", "origin", "+refs/heads/*:refs/remotes/origin/*" }, cancellationToken).ConfigureAwait(false);
            }
        }

        var checkout = await RunGitAsync(new[] { "-C", directory, "checkout", "--detach", pin }, cancellationToken).ConfigureAwait(false);

        if (checkout.Status != SandboxStatus.Success)
            throw new WorkspaceException($"the pinned base commit '{pin}' could not be checked out (exit {checkout.ExitCode}): {Redact(Summarize(checkout.Stderr), request.Token)} — the pin guarantees every participant sees the SAME immutable base; a stale or unpushed pin must fail the provision, never silently fall back to the tip");
    }

    private async Task<bool> CommitExistsLocallyAsync(string directory, string sha, CancellationToken cancellationToken) =>
        (await RunGitAsync(new[] { "-C", directory, "rev-parse", "--verify", "--quiet", $"{sha}^{{commit}}" }, cancellationToken).ConfigureAwait(false)).Status == SandboxStatus.Success;

    /// <summary>
    /// The ref to actually check out. A SOFT ref (a session-inherited prior branch — <see cref="WorkspaceRequest.DefaultRef"/>
    /// carries the fallback) is pre-flighted against the remote: if it was pruned (a merged PR auto-deletes it) we clone
    /// the default branch instead of failing the continuing run, surfaced as a Warning. A HARD ref (DefaultRef null — the
    /// default branch itself, or any ref with no fallback) is returned verbatim, so an explicit ref is never silently
    /// rewritten and the clone fails loud if it is gone. Byte-identical to before for every hard ref (no pre-flight runs).
    /// </summary>
    private async Task<string?> ResolveCheckoutRefAsync(WorkspaceRequest request, string url, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Ref) || string.IsNullOrWhiteSpace(request.DefaultRef) || string.Equals(request.Ref, request.DefaultRef, StringComparison.Ordinal))
            return request.Ref;

        if (await RefExistsOnRemoteAsync(url, request.Ref!, cancellationToken).ConfigureAwait(false))
            return request.Ref;

        _logger.LogWarning("Session continuity: the prior branch '{PriorRef}' no longer exists on the remote for {RepositoryUrl}; starting the continuing run from the default branch '{DefaultRef}' instead", request.Ref, request.RepositoryUrl, request.DefaultRef);

        return request.DefaultRef;
    }

    /// <summary>
    /// True when <paramref name="ref"/> resolves to a ref (branch or tag) on the remote. Only a clean PROBE that
    /// definitively finds NO matching ref (ls-remote succeeds with empty output) returns false → the soft fallback
    /// fires; a transient git / network failure of the probe is treated as PRESENT (return true) so a flaky probe never
    /// silently downgrades a continuing run to the default branch — the clone itself then surfaces any real failure.
    /// </summary>
    private async Task<bool> RefExistsOnRemoteAsync(string url, string @ref, CancellationToken cancellationToken)
    {
        var result = await RunGitAsync(new[] { "ls-remote", url, @ref }, cancellationToken).ConfigureAwait(false);

        return result.Status != SandboxStatus.Success || !string.IsNullOrWhiteSpace(result.Stdout);
    }

    /// <summary>
    /// Rewrite origin to the tokenless URL so the cloned <c>.git/config</c> never persists credentials.
    /// If the rewrite fails, REMOVE the origin remote outright — the persisted config carrying a token is
    /// the credential-leak we must close, and the run captures changes via the local diff (not origin), so
    /// dropping origin is safe. Only when both fail do we log an error; the workspace janitor is the final
    /// backstop. The clone already succeeded, so this never fails the run.
    /// </summary>
    private Task StripTokenFromRemoteAsync(string cleanUrl, string directory, CancellationToken cancellationToken) =>
        StripTokenFromRemoteAsync(_runners.Resolve(Kind), CloneTimeoutSeconds, _logger, cleanUrl, directory, cancellationToken);

    /// <summary>
    /// The SHARED implementation of <see cref="StripTokenFromRemoteAsync(string, string, CancellationToken)"/> —
    /// internal static (like <see cref="BuildAuthenticatedUrl"/>/<see cref="Redact"/>) so any OTHER caller that
    /// clones an authenticated URL directly (bypassing this provider's own <see cref="MaterializeAsync"/>, e.g.
    /// <c>SupervisorAcceptanceGrader.CloneAtBaseAsync</c>, which must clone at an arbitrary base SHA rather than a
    /// named ref) reuses the EXACT same strip-then-fallback-to-remove logic — a security-sensitive path must have
    /// exactly one implementation, never two copies that can silently drift apart.
    /// </summary>
    internal static async Task StripTokenFromRemoteAsync(ISandboxRunner runner, int timeoutSeconds, ILogger logger, string cleanUrl, string directory, CancellationToken cancellationToken)
    {
        Task<SandboxResult> RunGitAsync(IReadOnlyList<string> args) =>
            runner.RunAsync(new SandboxSpec { Command = "git", Args = args, TimeoutSeconds = timeoutSeconds }, cancellationToken);

        var rewrite = await RunGitAsync(new[] { "-C", directory, "remote", "set-url", "origin", cleanUrl }).ConfigureAwait(false);

        if (rewrite.Status == SandboxStatus.Success) return;

        var remove = await RunGitAsync(new[] { "-C", directory, "remote", "remove", "origin" }).ConfigureAwait(false);

        if (remove.Status == SandboxStatus.Success)
            logger.LogWarning("Token strip via set-url failed (exit {ExitCode}); removed the origin remote so no credential persists in .git/config", rewrite.ExitCode);
        else
            logger.LogError("Could not strip OR remove the tokened origin (set-url exit {SetExit}, remove exit {RemoveExit}); .git/config may retain credentials until the workspace janitor reclaims it", rewrite.ExitCode, remove.ExitCode);
    }

    private Task<SandboxResult> RunGitAsync(IReadOnlyList<string> args, CancellationToken cancellationToken) =>
        _runners.Resolve(Kind).RunAsync(new SandboxSpec { Command = "git", Args = args, TimeoutSeconds = CloneTimeoutSeconds }, cancellationToken);

    /// <summary>Build the HTTPS clone URL with embedded basic-auth credentials. No token → the URL unchanged. Pure + internal so it's unit-pinned.</summary>
    internal static string BuildAuthenticatedUrl(string repositoryUrl, string? tokenUsername, string? token)
    {
        if (string.IsNullOrEmpty(token)) return repositoryUrl;

        var uri = new Uri(repositoryUrl);
        var user = Uri.EscapeDataString(string.IsNullOrEmpty(tokenUsername) ? "x-access-token" : tokenUsername);
        var pass = Uri.EscapeDataString(token);

        return $"{uri.Scheme}://{user}:{pass}@{uri.Authority}{uri.PathAndQuery}";
    }

    private static string Summarize(string stderr) =>
        string.IsNullOrWhiteSpace(stderr) ? "(no stderr)" : stderr.Trim().Replace("\n", " ");

    /// <summary>
    /// Strip any echoed token from surfaced output so it never reaches a log / exception message. Redacts BOTH the raw
    /// token AND its percent-encoded form, because <see cref="BuildAuthenticatedUrl"/> embeds <c>Uri.EscapeDataString(token)</c>
    /// in the push argv — a token with URL-special characters (@ / + = %) appears ENCODED in a failing push command, so
    /// redacting only the raw literal would leak the reversible encoded form. Internal so the <c>LocalGitBranchIntegrator</c>
    /// reuses the SAME redaction over its own git output (co-located secret hygiene).
    /// </summary>
    internal static string Redact(string text, string? token)
    {
        if (string.IsNullOrEmpty(token)) return text;

        var redacted = text.Replace(token, "***");
        var encoded = Uri.EscapeDataString(token);

        return encoded == token ? redacted : redacted.Replace(encoded, "***");
    }

    private sealed class LocalWorkspaceHandle : IWorkspaceHandle, IWorkspacePushHandle
    {
        private readonly string _workspaceRoot;
        private readonly IReadOnlyList<MaterializedRepo> _repos;
        private readonly MaterializedRepo _primary;
        private readonly ISandboxRunner _runner;
        private readonly ILogger _logger;

        public LocalWorkspaceHandle(string workspaceRoot, string cwd, IReadOnlyList<MaterializedRepo> repos, string primaryAlias, ISandboxRunner runner, ILogger logger)
        {
            _workspaceRoot = workspaceRoot;
            Directory = cwd;
            _repos = repos;
            _primary = repos.First(r => r.Alias == primaryAlias);
            _runner = runner;
            _logger = logger;
        }

        public string Directory { get; }

        public string PrimaryAlias => _primary.Alias;

        public IReadOnlyList<WorkspaceRepositoryHandle> Repositories =>
            _repos.Select(r => new WorkspaceRepositoryHandle { Alias = r.Alias, Directory = r.Directory, Access = r.Access, BaseBranch = r.BaseBranch, BaseSha = r.BaseSha }).ToList();

        public Task<WorkspaceChanges> CaptureChangesAsync(CancellationToken cancellationToken) =>
            // The PRIMARY repo's changes. For a single-repo workspace the primary IS the only repo, so this is
            // byte-identical to before; the multi-repo executor captures each writable repo via the alias overload.
            CaptureRepoChangesAsync(_primary, cancellationToken);

        public Task<WorkspaceChanges> CaptureChangesAsync(string alias, CancellationToken cancellationToken) =>
            CaptureRepoChangesAsync(RepoByAlias(alias), cancellationToken);

        /// <summary>Resolve a repo by its workspace alias, failing loud on an unknown alias (a caller bug — the executor iterates <see cref="Repositories"/>).</summary>
        private MaterializedRepo RepoByAlias(string alias) =>
            _repos.FirstOrDefault(r => r.Alias == alias)
                ?? throw new WorkspaceException($"Unknown repository alias '{alias}' in the workspace.");

        private async Task<WorkspaceChanges> CaptureRepoChangesAsync(MaterializedRepo repo, CancellationToken cancellationToken)
        {
            // Stage everything (new, modified, deleted) so the diff vs the cloned base is complete, then
            // read the patch + the changed-file names. `--cached <base>` captures committed AND uncommitted
            // work, so it's robust whether the agent committed or just edited the working tree.
            await RunGitOrThrowAsync(repo, new[] { "add", "-A" }, cancellationToken).ConfigureAwait(false);

            var patch = await RunGitOrThrowAsync(repo, new[] { "diff", "--cached", "--no-color", repo.BaseSha }, cancellationToken).ConfigureAwait(false);
            var names = await RunGitOrThrowAsync(repo, new[] { "diff", "--cached", "--name-only", repo.BaseSha }, cancellationToken).ConfigureAwait(false);
            var numstat = await RunGitOrThrowAsync(repo, new[] { "diff", "--cached", "--numstat", repo.BaseSha }, cancellationToken).ConfigureAwait(false);

            return new WorkspaceChanges
            {
                Patch = patch,
                BaseSha = repo.BaseSha,
                ChangedFiles = names.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                FileStats = NumstatParser.Parse(numstat),
            };
        }

        public Task<string?> PushChangesAsync(string branchName, CancellationToken cancellationToken) =>
            // The PRIMARY repo (byte-identical to before for a single-repo workspace); the multi-repo executor pushes
            // each writable repo via the alias overload.
            PushRepoChangesAsync(_primary, branchName, cancellationToken);

        public Task<string?> PushChangesAsync(string alias, string branchName, CancellationToken cancellationToken)
        {
            var repo = RepoByAlias(alias);

            if (repo.Access != WorkspaceAccess.Write)
                throw new WorkspaceException($"Repository '{alias}' is read-only context and cannot be pushed.");

            return PushRepoChangesAsync(repo, branchName, cancellationToken);
        }

        private async Task<string?> PushRepoChangesAsync(MaterializedRepo repo, string branchName, CancellationToken cancellationToken)
        {
            // An anonymous clone carries no push credential — short-circuit WITHOUT invoking git (no remote to
            // push to, no credential to re-inject). Not a failure: the run simply produces no branch.
            if (string.IsNullOrEmpty(repo.Token)) return null;

            // -B (create-or-reset), not -b: an S6 revise round RE-pushes the same run-derived branch after another
            // pass in the same workspace — the branch already exists locally from the first push, and -b would throw.
            // Round 1 is byte-identical (-B creates when absent); a re-push resets the branch to the CURRENT head,
            // which is exactly the force-overwrite semantics the remote half (push --force) already promises.
            await RunGitOrThrowAsync(repo, new[] { "checkout", "-B", branchName }, cancellationToken).ConfigureAwait(false);
            await RunGitOrThrowAsync(repo, new[] { "add", "-A" }, cancellationToken).ConfigureAwait(false);

            // A run that changed nothing has nothing to push. The agent may either leave its edits for us to commit
            // OR commit them itself — so push when we just made a commit, OR when the branch tip already differs
            // from the cloned base (a harness that committed its own work leaves a clean tree, so there is nothing
            // new to commit, but the branch still carries the change). This mirrors CaptureChangesAsync's base-SHA
            // semantics so push and capture agree on "did this run change anything".
            var committed = await CommitOrDetectEmptyAsync(repo, branchName, cancellationToken).ConfigureAwait(false);

            if (!committed && !await HeadDiffersFromBaseAsync(repo, cancellationToken).ConfigureAwait(false)) return null;

            // Re-inject the SAME clone credential into the push ARGV only (never as a remote, never into
            // .git/config — origin was stripped after clone). Plain --force, not --force-with-lease: the branch
            // name is run-unique so lease protection is vacuous, and its no-remote-tracking-ref semantics vary by
            // git version. The push gets a bounded timeout so a hung push can't delay run completion.
            var authedUrl = BuildAuthenticatedUrl(repo.RepositoryUrl, repo.TokenUsername, repo.Token);

            await RunGitOrThrowAsync(repo, new[] { "push", "--force", authedUrl, $"{branchName}:{branchName}" }, cancellationToken, PushTimeoutSeconds).ConfigureAwait(false);

            return branchName;
        }

        /// <summary>Commit everything staged under a fixed CodeSpace identity; returns false (no commit) when there was nothing to commit. The identity AND <c>commit.gpgsign=false</c> are set inline via <c>-c</c> so the clone's git config is never mutated — and the automated capture commit can never inherit a host/global <c>commit.gpgsign=true</c> that would make it block on a signing key the unattended agent does not have (which would fail the branch push → the produced branch is silently lost). An internal automation commit under a synthetic identity has no meaningful signature, so signing is always disabled here.</summary>
        private async Task<bool> CommitOrDetectEmptyAsync(MaterializedRepo repo, string branchName, CancellationToken cancellationToken)
        {
            var result = await RunGitAsync(repo, new[] { "-c", "commit.gpgsign=false", "-c", "user.name=CodeSpace", "-c", "user.email=agent@codespace.local", "commit", "-m", $"Agent run {branchName}" }, cancellationToken, PushTimeoutSeconds).ConfigureAwait(false);

            if (result.Status == SandboxStatus.Success) return true;

            var output = $"{result.Stdout}\n{result.Stderr}";

            if (output.Contains("nothing to commit", StringComparison.OrdinalIgnoreCase) || output.Contains("working tree clean", StringComparison.OrdinalIgnoreCase))
                return false;

            throw new WorkspaceException($"git commit failed (exit {result.ExitCode}): {Redact(Summarize(result.Stderr), repo.Token)}");
        }

        /// <summary>True when the branch tip differs from the cloned base — covers a harness that COMMITTED its own
        /// work (clean tree, so nothing new to commit, but the branch still carries the change). Diffs against the
        /// same base SHA <see cref="CaptureChangesAsync"/> uses. <c>git diff --quiet</c> exits 0 when identical,
        /// non-zero when different; a non-zero exit is the signal, not a failure, so this never throws (and on a
        /// genuine git error it fails toward pushing rather than silently dropping the agent's work).</summary>
        private async Task<bool> HeadDiffersFromBaseAsync(MaterializedRepo repo, CancellationToken cancellationToken)
        {
            var result = await RunGitAsync(repo, new[] { "diff", "--quiet", repo.BaseSha, "HEAD" }, cancellationToken, CaptureTimeoutSeconds).ConfigureAwait(false);
            return result.ExitCode != 0;
        }

        private async Task<string> RunGitOrThrowAsync(MaterializedRepo repo, IReadOnlyList<string> args, CancellationToken cancellationToken, int timeoutSeconds = CaptureTimeoutSeconds)
        {
            var result = await RunGitAsync(repo, args, cancellationToken, timeoutSeconds).ConfigureAwait(false);

            if (result.Status != SandboxStatus.Success)
                // Redact any echoed token (the push argv embeds the authed URL) so it never reaches a log / exception.
                throw new WorkspaceException($"git {string.Join(' ', RedactArgs(args, repo.Token))} failed (exit {result.ExitCode}): {Redact(result.Stderr.Trim(), repo.Token)}");

            return result.Stdout;
        }

        /// <summary>Run a git command in a SPECIFIC repo's clone (its directory as cwd) through the same unconfined batch path — host network, not bubblewrapped. Returns the raw result so a caller can classify it (e.g. detect "nothing to commit") rather than always throw.</summary>
        private async Task<SandboxResult> RunGitAsync(MaterializedRepo repo, IReadOnlyList<string> args, CancellationToken cancellationToken, int timeoutSeconds)
        {
            try
            {
                return await _runner.RunAsync(
                    new SandboxSpec { Command = "git", Args = args, WorkingDirectory = repo.Directory, TimeoutSeconds = timeoutSeconds }, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Honour the IWorkspaceHandle contract: a git failure — including an INFRASTRUCTURE failure the
                // runner throws (git not on PATH, the working directory removed mid-run) — surfaces as a
                // WorkspaceException, never a raw Win32Exception/IOException leaking to the caller.
                throw new WorkspaceException($"git {string.Join(' ', RedactArgs(args, repo.Token))} could not run: {Redact(ex.Message, repo.Token)}", ex);
            }
        }

        /// <summary>Redact the token from the echoed argv (the push command carries the authed URL) before it lands in an exception message.</summary>
        private static IEnumerable<string> RedactArgs(IReadOnlyList<string> args, string? token) => args.Select(a => Redact(a, token));

        public ValueTask DisposeAsync()
        {
            try
            {
                // Remove the WHOLE workspace tree (every cloned repo), not just the cwd — for a multi-repo workspace
                // the cwd may be the root while each repo is a subdir; for single-repo the root IS the clone.
                if (System.IO.Directory.Exists(_workspaceRoot)) System.IO.Directory.Delete(_workspaceRoot, recursive: true);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to remove agent workspace {Directory}", _workspaceRoot);
            }

            return ValueTask.CompletedTask;
        }
    }
}
