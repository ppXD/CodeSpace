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
public sealed class LocalGitWorkspaceProvider : IWorkspaceProvider, IWorkspaceJanitor, ISingletonDependency
{
    private const int CloneTimeoutSeconds = 300;
    private const int CaptureTimeoutSeconds = 120;
    private static readonly string WorkspacesRoot = Path.Combine(Path.GetTempPath(), "codespace-agent-workspaces");

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

    public async Task<IWorkspaceHandle> PrepareAsync(WorkspaceRequest request, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(WorkspacesRoot);

        var directory = Path.Combine(WorkspacesRoot, Guid.NewGuid().ToString("N"));

        try
        {
            await CloneAsync(request, directory, cancellationToken).ConfigureAwait(false);

            if (!string.IsNullOrEmpty(request.Token))
                await StripTokenFromRemoteAsync(request.RepositoryUrl, directory, cancellationToken).ConfigureAwait(false);

            var baseSha = await ReadBaseShaAsync(directory, cancellationToken).ConfigureAwait(false);

            return new LocalWorkspaceHandle(directory, baseSha, _runners.Resolve(Kind), _logger);
        }
        catch
        {
            TryDeleteDirectory(directory);
            throw;
        }
    }

    /// <summary>Record the cloned HEAD revision so <see cref="LocalWorkspaceHandle.CaptureChangesAsync"/> can diff the agent's work against it — robust whether the agent commits or leaves changes uncommitted.</summary>
    private async Task<string> ReadBaseShaAsync(string directory, CancellationToken cancellationToken)
    {
        var result = await _runners.Resolve(Kind).RunAsync(
            new SandboxSpec { Command = "git", Args = new[] { "-C", directory, "rev-parse", "HEAD" }, TimeoutSeconds = CloneTimeoutSeconds }, cancellationToken).ConfigureAwait(false);

        if (result.Status != SandboxStatus.Success)
            throw new WorkspaceException($"Could not read the workspace base revision (exit {result.ExitCode}): {Summarize(result.Stderr)}");

        return result.Stdout.Trim();
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

        var args = new List<string> { "clone" };

        if (request.Depth > 0) { args.Add("--depth"); args.Add(request.Depth.ToString()); }
        if (!string.IsNullOrWhiteSpace(request.Ref)) { args.Add("--branch"); args.Add(request.Ref); }

        args.Add(url);
        args.Add(directory);

        var result = await RunGitAsync(args, cancellationToken).ConfigureAwait(false);

        if (result.Status != SandboxStatus.Success)
            throw new WorkspaceException($"git clone failed (exit {result.ExitCode}): {Redact(Summarize(result.Stderr), request.Token)}");
    }

    /// <summary>
    /// Rewrite origin to the tokenless URL so the cloned <c>.git/config</c> never persists credentials.
    /// If the rewrite fails, REMOVE the origin remote outright — the persisted config carrying a token is
    /// the credential-leak we must close, and the run captures changes via the local diff (not origin), so
    /// dropping origin is safe. Only when both fail do we log an error; the workspace janitor is the final
    /// backstop. The clone already succeeded, so this never fails the run.
    /// </summary>
    private async Task StripTokenFromRemoteAsync(string cleanUrl, string directory, CancellationToken cancellationToken)
    {
        var rewrite = await RunGitAsync(new[] { "-C", directory, "remote", "set-url", "origin", cleanUrl }, cancellationToken).ConfigureAwait(false);

        if (rewrite.Status == SandboxStatus.Success) return;

        var remove = await RunGitAsync(new[] { "-C", directory, "remote", "remove", "origin" }, cancellationToken).ConfigureAwait(false);

        if (remove.Status == SandboxStatus.Success)
            _logger.LogWarning("Token strip via set-url failed (exit {ExitCode}); removed the origin remote so no credential persists in .git/config", rewrite.ExitCode);
        else
            _logger.LogError("Could not strip OR remove the tokened origin (set-url exit {SetExit}, remove exit {RemoveExit}); .git/config may retain credentials until the workspace janitor reclaims it", rewrite.ExitCode, remove.ExitCode);
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

    /// <summary>Strip any echoed token from surfaced output so it never reaches a log / exception message.</summary>
    private static string Redact(string text, string? token) =>
        string.IsNullOrEmpty(token) ? text : text.Replace(token, "***");

    private sealed class LocalWorkspaceHandle : IWorkspaceHandle
    {
        private readonly string _baseSha;
        private readonly ISandboxRunner _runner;
        private readonly ILogger _logger;

        public LocalWorkspaceHandle(string directory, string baseSha, ISandboxRunner runner, ILogger logger)
        {
            Directory = directory;
            _baseSha = baseSha;
            _runner = runner;
            _logger = logger;
        }

        public string Directory { get; }

        public async Task<WorkspaceChanges> CaptureChangesAsync(CancellationToken cancellationToken)
        {
            // Stage everything (new, modified, deleted) so the diff vs the cloned base is complete, then
            // read the patch + the changed-file names. `--cached <base>` captures committed AND uncommitted
            // work, so it's robust whether the agent committed or just edited the working tree.
            await RunGitOrThrowAsync(new[] { "add", "-A" }, cancellationToken).ConfigureAwait(false);

            var patch = await RunGitOrThrowAsync(new[] { "diff", "--cached", "--no-color", _baseSha }, cancellationToken).ConfigureAwait(false);
            var names = await RunGitOrThrowAsync(new[] { "diff", "--cached", "--name-only", _baseSha }, cancellationToken).ConfigureAwait(false);

            return new WorkspaceChanges
            {
                Patch = patch,
                ChangedFiles = names.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            };
        }

        private async Task<string> RunGitOrThrowAsync(IReadOnlyList<string> args, CancellationToken cancellationToken)
        {
            SandboxResult result;
            try
            {
                result = await _runner.RunAsync(
                    new SandboxSpec { Command = "git", Args = args, WorkingDirectory = Directory, TimeoutSeconds = CaptureTimeoutSeconds }, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Honour the IWorkspaceHandle contract: a git failure — including an INFRASTRUCTURE failure the
                // runner throws (git not on PATH, the working directory removed mid-run) — surfaces as a
                // WorkspaceException, never a raw Win32Exception/IOException leaking to the caller.
                throw new WorkspaceException($"git {string.Join(' ', args)} could not run: {ex.Message}", ex);
            }

            if (result.Status != SandboxStatus.Success)
                throw new WorkspaceException($"git {string.Join(' ', args)} failed (exit {result.ExitCode}): {result.Stderr.Trim()}");

            return result.Stdout;
        }

        public ValueTask DisposeAsync()
        {
            try
            {
                if (System.IO.Directory.Exists(Directory)) System.IO.Directory.Delete(Directory, recursive: true);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to remove agent workspace {Directory}", Directory);
            }

            return ValueTask.CompletedTask;
        }
    }
}
