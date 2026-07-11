using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Services.Agents.Eval.Benchmark;
using CodeSpace.Core.Services.Agents.Sandbox;
using CodeSpace.Core.Services.Agents.Workspace;
using CodeSpace.Core.Services.Agents.Workspace.Providers;
using CodeSpace.Core.Services.Workflows.Artifacts;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Agents.Benchmark;
using Microsoft.Extensions.Logging;

namespace CodeSpace.Core.Services.Supervisor;

/// <summary>
/// Clones a repository at a produced branch and grades it with the shared <see cref="TestsPassGrader"/> oracle —
/// the supervisor's objective acceptance adapter (L4 arc A). It OWNS the clone (a fresh, agent-independent shallow
/// checkout from the remote) and DELEGATES the verdict to the registry-resolved grader, so it reuses both the
/// workspace底座 (<see cref="IAgentWorkspaceResolver"/> + <see cref="IWorkspaceProviderRegistry"/>) and the grading
/// oracle without duplicating either. Scoped because the workspace resolver injects the DbContext; the registries
/// it resolves are singletons. Dormant until A3 folds its verdict at the supervisor's accept boundary.
/// </summary>
public sealed class SupervisorAcceptanceGrader : ISupervisorAcceptanceGrader, IScopedDependency
{
    private const string DefaultRunnerKind = "local";
    private const int CloneTimeoutSeconds = 300;

    private readonly IAgentWorkspaceResolver _workspaceResolver;
    private readonly IWorkspaceProviderRegistry _providers;
    private readonly ISandboxRunnerRegistry _runners;
    private readonly IBenchmarkGraderRegistry _graders;
    private readonly IArtifactOffloader _offloader;
    private readonly ILogger<SupervisorAcceptanceGrader> _logger;

    public SupervisorAcceptanceGrader(IAgentWorkspaceResolver workspaceResolver, IWorkspaceProviderRegistry providers, ISandboxRunnerRegistry runners, IBenchmarkGraderRegistry graders, IArtifactOffloader offloader, ILogger<SupervisorAcceptanceGrader> logger)
    {
        _workspaceResolver = workspaceResolver;
        _providers = providers;
        _runners = runners;
        _graders = graders;
        _offloader = offloader;
        _logger = logger;
    }

    public async Task<BenchmarkGrade> GradeAsync(Guid repositoryId, Guid teamId, string branch, SupervisorAcceptanceSpec spec, int timeoutSeconds, CancellationToken cancellationToken)
    {
        try
        {
            var clone = await _workspaceResolver.ResolveByRepositoryIdAsync(repositoryId, teamId, cancellationToken, @ref: branch).ConfigureAwait(false)
                ?? throw new WorkspaceException($"Repository {repositoryId} resolved to no clone request for acceptance grading.");

            await using var workspace = await _providers.Resolve(DefaultRunnerKind).PrepareAsync(WorkspaceProvisionRequest.FromSingle(clone), cancellationToken).ConfigureAwait(false);

            return await GradeWorkspaceAsync(workspace.Directory, spec, teamId, timeoutSeconds, cancellationToken).ConfigureAwait(false);
        }
        catch (WorkspaceException ex)
        {
            // A repo/branch we cannot clone cannot be verified → fail closed to "not accepted" (never a silent pass).
            _logger.LogWarning(ex, "Acceptance grading could not clone {RepositoryId} at {Branch}; failing closed to not-accepted", repositoryId, branch);
            return Failed($"clone-failed: {ex.Message}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The check itself could not be RUN (e.g. a model-authored command names a binary not on PATH) — acceptance
            // still cannot be verified, so fail closed to "not accepted" rather than crashing the supervisor turn. Only a
            // genuine cancellation propagates (the caller asked to stop).
            _logger.LogWarning(ex, "Acceptance grading could not run the check for {RepositoryId} at {Branch}; failing closed to not-accepted", repositoryId, branch);
            return Failed($"grade-error: {ex.Message}");
        }
    }

    public async Task<BenchmarkGrade> GradePatchAsync(Guid repositoryId, Guid teamId, string baseSha, string inlinePatch, Guid? patchArtifactId, SupervisorAcceptanceSpec spec, int timeoutSeconds, CancellationToken cancellationToken)
    {
        var directory = Path.Combine(LocalGitWorkspaceProvider.WorkspacesRoot, "grade-" + Guid.NewGuid().ToString("N"));

        try
        {
            // A base SHA is not a branch/tag name, so it cannot go through IWorkspaceProviderRegistry — the shared
            // provider clones via `git clone --branch <ref>`, which git refuses for a raw commit SHA. This clones
            // full + checks the base out detached instead, mirroring LocalGitBranchIntegrator's own base-anchored
            // clone (the other caller that needs an arbitrary base SHA rather than a named ref).
            var clone = await _workspaceResolver.ResolveByRepositoryIdAsync(repositoryId, teamId, cancellationToken).ConfigureAwait(false)
                ?? throw new WorkspaceException($"Repository {repositoryId} resolved to no clone request for acceptance grading.");

            await CloneAtBaseAsync(clone, baseSha, directory, cancellationToken).ConfigureAwait(false);

            var patch = await _offloader.ResolveAsync(teamId, inlinePatch, patchArtifactId, cancellationToken).ConfigureAwait(false);

            if (string.IsNullOrEmpty(patch))
            {
                _logger.LogWarning("Acceptance grading found no resolvable patch for {RepositoryId} at base {BaseSha}; failing closed to not-accepted", repositoryId, baseSha);
                return Failed("no-branch-or-repo");
            }

            var applyError = await ApplyPatchAsync(directory, patch, cancellationToken).ConfigureAwait(false);

            if (applyError is not null)
            {
                _logger.LogWarning("Acceptance grading could not apply the recorded patch for {RepositoryId} onto base {BaseSha}: {Error}", repositoryId, baseSha, applyError);
                return Failed($"patch-apply-failed: {applyError}");
            }

            return await GradeWorkspaceAsync(directory, spec, teamId, timeoutSeconds, cancellationToken).ConfigureAwait(false);
        }
        catch (WorkspaceException ex)
        {
            _logger.LogWarning(ex, "Acceptance grading could not clone {RepositoryId} at base {BaseSha}; failing closed to not-accepted", repositoryId, baseSha);
            return Failed($"clone-failed: {ex.Message}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Acceptance grading could not run the check for {RepositoryId} at base {BaseSha}; failing closed to not-accepted", repositoryId, baseSha);
            return Failed($"grade-error: {ex.Message}");
        }
        finally
        {
            TryDeleteDirectory(directory);
        }
    }

    public async Task<BenchmarkGrade> GradeBaseAsync(Guid repositoryId, Guid teamId, string baseSha, SupervisorAcceptanceSpec spec, int timeoutSeconds, CancellationToken cancellationToken)
    {
        var directory = Path.Combine(LocalGitWorkspaceProvider.WorkspacesRoot, "grade-base-" + Guid.NewGuid().ToString("N"));

        try
        {
            var clone = await _workspaceResolver.ResolveByRepositoryIdAsync(repositoryId, teamId, cancellationToken).ConfigureAwait(false)
                ?? throw new WorkspaceException($"Repository {repositoryId} resolved to no clone request for baseline grading.");

            await CloneAtBaseAsync(clone, baseSha, directory, cancellationToken).ConfigureAwait(false);

            return await GradeWorkspaceAsync(directory, spec, teamId, timeoutSeconds, cancellationToken).ConfigureAwait(false);
        }
        catch (WorkspaceException ex)
        {
            _logger.LogWarning(ex, "Baseline grading could not clone {RepositoryId} at base {BaseSha}; recording clone-failed", repositoryId, baseSha);
            return Failed($"clone-failed: {ex.Message}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Baseline grading could not run the check for {RepositoryId} at base {BaseSha}; recording grade-error", repositoryId, baseSha);
            return Failed($"grade-error: {ex.Message}");
        }
        finally
        {
            TryDeleteDirectory(directory);
        }
    }

    /// <summary>Full clone (no <c>--branch</c> — a base SHA is not a ref name the shared provider's clone can accept) then a detached checkout of the exact base. Throws <see cref="WorkspaceException"/> (redacted) on either git failure.</summary>
    private async Task CloneAtBaseAsync(WorkspaceRequest clone, string baseSha, string directory, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(LocalGitWorkspaceProvider.WorkspacesRoot);

        var url = LocalGitWorkspaceProvider.BuildAuthenticatedUrl(clone.RepositoryUrl, clone.TokenUsername, clone.Token);

        var cloneResult = await _runners.Resolve(DefaultRunnerKind).RunAsync(
            new SandboxSpec { Command = "git", Args = new[] { "clone", url, directory }, TimeoutSeconds = CloneTimeoutSeconds }, cancellationToken).ConfigureAwait(false);

        if (cloneResult.Status != SandboxStatus.Success)
            throw new WorkspaceException($"git clone failed (exit {cloneResult.ExitCode}): {LocalGitWorkspaceProvider.Redact(Summarize(cloneResult.Stderr), clone.Token)}");

        // Model-authored setup/acceptance commands run INSIDE this clone next — strip the tokened origin via the
        // SAME shared helper LocalGitWorkspaceProvider's own branch-grading path uses (LocalGitWorkspaceProvider.
        // StripTokenFromRemoteAsync — one implementation, not a second copy that could drift), so no credential
        // persists in .git/config for those commands to read. Best-effort: the clone already succeeded, so this
        // never fails the grade. Guarded on a present token, mirroring MaterializeAsync's own call site exactly —
        // a public repo with no credential has nothing to strip.
        if (!string.IsNullOrEmpty(clone.Token))
            await LocalGitWorkspaceProvider.StripTokenFromRemoteAsync(_runners.Resolve(DefaultRunnerKind), CloneTimeoutSeconds, _logger, clone.RepositoryUrl, directory, cancellationToken).ConfigureAwait(false);

        var checkoutResult = await _runners.Resolve(DefaultRunnerKind).RunAsync(
            new SandboxSpec { Command = "git", Args = new[] { "-C", directory, "checkout", "--detach", baseSha }, WorkingDirectory = directory, TimeoutSeconds = CloneTimeoutSeconds }, cancellationToken).ConfigureAwait(false);

        if (checkoutResult.Status != SandboxStatus.Success)
            throw new WorkspaceException($"base revision {baseSha} not found in the repository: {LocalGitWorkspaceProvider.Redact(Summarize(checkoutResult.Stderr), clone.Token)}");
    }

    /// <summary>Apply <paramref name="patch"/> onto the already-checked-out <paramref name="directory"/> — NO stage, NO commit, NO push (this grade is read-only by construction; the clone is discarded after grading either way). Mirrors <c>LocalGitBranchIntegrator</c>'s own apply step (<c>git apply --3way</c>) minus <c>--index</c>, since nothing here is ever committed. Returns null on success, else <c>git</c>'s stderr.</summary>
    private async Task<string?> ApplyPatchAsync(string directory, string patch, CancellationToken cancellationToken)
    {
        var patchFile = Path.Combine(directory, $".codespace-acceptance-{Guid.NewGuid():N}.patch");
        await File.WriteAllTextAsync(patchFile, patch, cancellationToken).ConfigureAwait(false);

        try
        {
            var result = await _runners.Resolve(DefaultRunnerKind).RunAsync(
                new SandboxSpec { Command = "git", Args = new[] { "-C", directory, "apply", "--3way", patchFile }, WorkingDirectory = directory, TimeoutSeconds = 60 }, cancellationToken).ConfigureAwait(false);

            return result.Status == SandboxStatus.Success ? null : result.Stderr;
        }
        finally
        {
            try { File.Delete(patchFile); } catch { /* best-effort — the whole clone is discarded regardless */ }
        }
    }

    private async Task<BenchmarkGrade> GradeWorkspaceAsync(string directory, SupervisorAcceptanceSpec spec, Guid teamId, int timeoutSeconds, CancellationToken cancellationToken)
    {
        if (spec.SetupCommand is { Count: > 0 } setupCommand)
        {
            var setupFailure = await RunSetupCommandAsync(setupCommand, directory, timeoutSeconds, cancellationToken).ConfigureAwait(false);
            if (setupFailure is not null) return setupFailure;
        }

        var context = BenchmarkGradingContext.ForAcceptance(spec, teamId, timeoutSeconds, directory, _runners.Resolve(DefaultRunnerKind));

        return await _graders.Resolve(spec.Kind ?? BenchmarkGradingKind.TestsPass).GradeAsync(context, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// P3.1 part 2: run the contract's OPTIONAL setup step in the SAME workspace before the check — a failure here
    /// means the check itself never got a chance to run, so it is classified alongside <c>grade-error:</c>/
    /// <c>clone-failed:</c> (infra, not a code verdict) rather than as a genuine failing check. Returns null on
    /// success (proceed to grading); a non-null grade short-circuits <see cref="GradeWorkspaceAsync"/>.
    /// </summary>
    private async Task<BenchmarkGrade?> RunSetupCommandAsync(IReadOnlyList<string> setupCommand, string directory, int timeoutSeconds, CancellationToken cancellationToken)
    {
        var spec = new SandboxSpec
        {
            Command = setupCommand[0],
            Args = setupCommand.Skip(1).ToList(),
            WorkingDirectory = directory,
            TimeoutSeconds = timeoutSeconds,
        };

        var result = await _runners.Resolve(DefaultRunnerKind).RunAsync(spec, cancellationToken).ConfigureAwait(false);

        if (result.Status == SandboxStatus.Success) return null;

        _logger.LogWarning("Acceptance grading's setup command failed in {Directory}: {Status} (exit {ExitCode}) {Stderr}", directory, result.Status, result.ExitCode, Summarize(result.Stderr));

        return result.Status == SandboxStatus.TimedOut
            ? Failed("setup-timed-out")
            : Failed($"setup-failed: {Summarize(result.Stderr)}");
    }

    private static BenchmarkGrade Failed(string detail) => new() { Passed = false, Detail = detail };

    private static string Summarize(string stderr) => string.IsNullOrWhiteSpace(stderr) ? "(no stderr)" : stderr.Trim().Replace("\n", " ");

    private static void TryDeleteDirectory(string directory)
    {
        try { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); } catch { /* best-effort — the workspace janitor reclaims an orphaned clone */ }
    }
}
