using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Services.Agents.Sandbox;
using CodeSpace.Core.Services.Agents.Sandbox.Runners;
using CodeSpace.Core.Services.Agents.Workspace.Providers;
using CodeSpace.Core.Services.Workflows.Artifacts;
using CodeSpace.Messages.Agents;
using Microsoft.Extensions.Logging;

namespace CodeSpace.Core.Services.Agents.Workspace.Integrators;

/// <summary>
/// v0 branch integrator: integrates K agent contributions into ONE branch via real <c>git</c> on the worker's
/// filesystem, run THROUGH the "local" <see cref="ISandboxRunner"/> (same process / timeout / cancellation handling
/// as the workspace provider). Pairs with <c>LocalGitWorkspaceProvider</c> — both <see cref="Kind"/> "local".
///
/// <para><b>Base-anchored, all-or-nothing, fail-safe.</b> Resolves each contribution's patch (team-scoped offloader),
/// refuses any whose recorded base disagrees with the request base (the moved-base integrity guard) / is truncated /
/// spans another repo / has no patch-and-no-branch, then clones full, checks out the shared base SHA (detached), and
/// <c>git apply --index --3way</c>s each patch IN ORDER. If EVERY contribution applies clean it commits once and
/// pushes a run-id-derived reviewable branch; on ANY conflict or refusal it resets the clone to base (no half-merge
/// survives), pushes nothing, and returns a <see cref="IntegrationResult"/> naming what could not be applied — the
/// original K agent branches/patches remain intact for human review.</para>
///
/// <para><b>Secret hygiene</b> is co-located with the provider: the clone embeds the token in the URL for the clone
/// command only and every surfaced git output is redacted (<see cref="LocalGitWorkspaceProvider.Redact"/>), and the
/// transient clone is always removed in a <c>finally</c>.</para>
/// </summary>
public sealed class LocalGitBranchIntegrator : IBranchIntegrator, IScopedDependency
{
    private const int GitTimeoutSeconds = 300;
    private const string TruncationMarker = "... diff truncated";

    private readonly ISandboxRunnerRegistry _runners;
    private readonly IArtifactOffloader _offloader;
    private readonly ILogger<LocalGitBranchIntegrator> _logger;

    public LocalGitBranchIntegrator(ISandboxRunnerRegistry runners, IArtifactOffloader offloader, ILogger<LocalGitBranchIntegrator> logger)
    {
        _runners = runners;
        _offloader = offloader;
        _logger = logger;
    }

    public string Kind => LocalProcessRunner.LocalKind;

    public async Task<IntegrationResult> IntegrateAsync(IntegrationRequest request, CancellationToken cancellationToken)
    {
        if (request.Contributions.Count == 0)
            return IntegrationResult.Build(IntegrationStatus.Empty, null, Array.Empty<ContributionOutcome>(), "no contributions to integrate");

        var resolved = await ResolveContributionsAsync(request, cancellationToken).ConfigureAwait(false);

        var preflightBlock = Preflight(request, resolved);

        if (preflightBlock is not null)
            return Aborted(resolved, preflightBlock);

        return await CloneApplyAndPushAsync(request, resolved, cancellationToken).ConfigureAwait(false);
    }

    // ── Resolve + preflight (pure, no clone) ─────────────────────────────────────────

    /// <summary>Resolve each contribution's patch text (inline or team-scoped offloaded) up front — needed by both the preflight checks and the apply.</summary>
    private async Task<IReadOnlyList<ResolvedContribution>> ResolveContributionsAsync(IntegrationRequest request, CancellationToken cancellationToken)
    {
        var resolved = new List<ResolvedContribution>(request.Contributions.Count);

        foreach (var c in request.Contributions)
        {
            var patch = await _offloader.ResolveAsync(request.TeamId, c.Patch, c.PatchArtifactId, cancellationToken).ConfigureAwait(false);
            resolved.Add(new ResolvedContribution(c, patch));
        }

        return resolved;
    }

    /// <summary>The whole-set + per-contribution pure refusal checks. Returns a set-level abort reason when integration cannot proceed, else null. Records each blocked contribution's own reason on the <see cref="ResolvedContribution"/>.</summary>
    private static string? Preflight(IntegrationRequest request, IReadOnlyList<ResolvedContribution> resolved)
    {
        if (SpansMultipleRepositories(resolved))
        {
            foreach (var r in resolved) r.Block("contributions span multiple repositories");
            return "contributions span multiple repositories";
        }

        var anyBlocked = false;

        foreach (var r in resolved)
        {
            var reason = BlockReason(request, r);

            if (reason is null) continue;

            r.Block(reason);
            anyBlocked = true;
        }

        return anyBlocked ? "a contribution could not be applied" : null;
    }

    private static bool SpansMultipleRepositories(IReadOnlyList<ResolvedContribution> resolved) =>
        resolved.Select(r => r.Contribution.SourceRepositoryId).Where(id => id != Guid.Empty).Distinct().Count() > 1;

    /// <summary>The per-contribution refusal cause (null = would apply). The base-SHA equality check is the integrity guard against grafting stale-base work onto a moved tree.</summary>
    private static string? BlockReason(IntegrationRequest request, ResolvedContribution r)
    {
        var c = r.Contribution;

        if (string.IsNullOrEmpty(c.BaseSha))
            return "no recorded base revision (re-attached run — its work was not captured)";

        if (!string.Equals(c.BaseSha, request.BaseSha, StringComparison.Ordinal))
            return $"base SHA mismatch (expected {Short(request.BaseSha)}, got {Short(c.BaseSha)})";

        // An OFFLOADED patch that resolves to nothing is a resolution failure (a missing or cross-team artifact),
        // NEVER a no-op — treating it as an empty diff would silently drop the agent's real work. A genuinely-empty
        // INLINE patch (no artifact id) is a true no-op and falls through to the no-patch-and-no-branch check.
        if (c.PatchArtifactId is not null && string.IsNullOrEmpty(r.Patch))
            return "offloaded patch could not be resolved (missing or cross-team artifact)";

        if (r.Patch.Contains(TruncationMarker, StringComparison.Ordinal))
            return "diff exceeded the inline cap and was truncated — cannot apply a truncated patch";

        // An empty patch can NEVER be applied. With NO branch it is a no-op / lost-work contribution; WITH a branch
        // the agent's real work lives only on that branch (the integrator is patch-based, it never fetches a branch)
        // — refusing it (rather than skipping as a vacuous no-op) is what stops a branch-only agent being silently
        // dropped yet reported Applied. Block() records it Conflicted-with-fallback when a branch exists, else Unintegrable.
        if (string.IsNullOrWhiteSpace(r.Patch))
            return string.IsNullOrEmpty(c.ProducedBranch)
                ? "no patch and no branch (the agent's work was not captured)"
                : "no patch captured — the agent's work lives only on its branch, which cannot be integrated by patch";

        return null;
    }

    // ── Clone + apply + push (the git work) ──────────────────────────────────────────

    private async Task<IntegrationResult> CloneApplyAndPushAsync(IntegrationRequest request, IReadOnlyList<ResolvedContribution> resolved, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(LocalGitWorkspaceProvider.WorkspacesRoot);

        var directory = Path.Combine(LocalGitWorkspaceProvider.WorkspacesRoot, "integrate-" + Guid.NewGuid().ToString("N"));

        try
        {
            await CloneAsync(request, directory, cancellationToken).ConfigureAwait(false);

            if (!await CheckoutBaseAsync(directory, request, cancellationToken).ConfigureAwait(false))
                return Aborted(resolved, $"base revision {Short(request.BaseSha)} not found in the repository");

            var applyBlock = await ApplyAllAsync(directory, request, resolved, cancellationToken).ConfigureAwait(false);

            if (applyBlock is not null)
            {
                await ResetToBaseAsync(directory, request.BaseSha, cancellationToken).ConfigureAwait(false);
                return Aborted(resolved, applyBlock);
            }

            return await CommitAndPushAsync(directory, request, resolved, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not WorkspaceException)
        {
            // Totalize the IBranchIntegrator contract: a non-git environmental fault (disk full / EACCES on the patch
            // write or the clone dir) becomes a redacted WorkspaceException — the SAME exception the callers
            // (supervisor merge, git.integrate node) already catch — instead of escaping and stranding the turn.
            throw new WorkspaceException($"branch integration failed: {LocalGitWorkspaceProvider.Redact(ex.Message, request.Token)}", ex);
        }
        finally
        {
            TryDeleteDirectory(directory);
        }
    }

    private async Task CloneAsync(IntegrationRequest request, string directory, CancellationToken cancellationToken)
    {
        // A FULL clone (no --depth): a 3-way apply needs the base history the agents' shallow clones lacked, and a
        // full clone guarantees the recorded base SHA is present. (A --filter=blob:none partial clone is a deferred
        // optimisation — it needs remote allow-filter support a bare file:// remote can't give a test.)
        var url = LocalGitWorkspaceProvider.BuildAuthenticatedUrl(request.RepositoryUrl, request.TokenUsername, request.Token);

        var result = await RunGitAsync(new[] { "clone", url, directory }, workingDirectory: null, cancellationToken).ConfigureAwait(false);

        if (result.Status != SandboxStatus.Success)
            throw new WorkspaceException($"git clone failed (exit {result.ExitCode}): {LocalGitWorkspaceProvider.Redact(Summarize(result.Stderr), request.Token)}");
    }

    /// <summary>Check out the EXACT shared base (detached) so every <c>apply --3way</c> resolves the pre-image against the commit the agents saw. False when the SHA is not in history (a bad base).</summary>
    private async Task<bool> CheckoutBaseAsync(string directory, IntegrationRequest request, CancellationToken cancellationToken)
    {
        var result = await RunGitAsync(new[] { "-C", directory, "checkout", "--detach", request.BaseSha }, directory, cancellationToken).ConfigureAwait(false);

        return result.Status == SandboxStatus.Success;
    }

    /// <summary>Apply each clean (preflight-passed) contribution in order. Returns a set-level abort reason on the FIRST textual conflict (marking the rest not-attempted), else null when all applied.</summary>
    private async Task<string?> ApplyAllAsync(string directory, IntegrationRequest request, IReadOnlyList<ResolvedContribution> resolved, CancellationToken cancellationToken)
    {
        for (var i = 0; i < resolved.Count; i++)
        {
            var r = resolved[i];

            if (string.IsNullOrWhiteSpace(r.Patch)) continue; // a true no-op (base matched, empty diff) — nothing to apply

            if (await TryApplyAsync(directory, r, cancellationToken).ConfigureAwait(false)) continue;

            var conflictedFiles = await ReadConflictedFilesAsync(directory, r, cancellationToken).ConfigureAwait(false);

            r.Conflict("textual conflict applying the patch", conflictedFiles);

            for (var j = i + 1; j < resolved.Count; j++) resolved[j].Block("not integrated — an earlier contribution conflicted");

            return "a contribution conflicted while integrating";
        }

        return null;
    }

    private async Task<bool> TryApplyAsync(string directory, ResolvedContribution r, CancellationToken cancellationToken)
    {
        var patchFile = Path.Combine(directory, ".codespace-integrate.patch");
        await File.WriteAllTextAsync(patchFile, r.Patch, cancellationToken).ConfigureAwait(false);

        try
        {
            var result = await RunGitAsync(new[] { "-C", directory, "apply", "--index", "--3way", patchFile }, directory, cancellationToken).ConfigureAwait(false);
            return result.Status == SandboxStatus.Success;
        }
        finally
        {
            TryDeleteFile(patchFile);
        }
    }

    /// <summary>Best-effort: the unmerged paths after a failed 3-way apply; falls back to the patch's target paths so a conflict always names at least the files involved.</summary>
    private async Task<IReadOnlyList<string>> ReadConflictedFilesAsync(string directory, ResolvedContribution r, CancellationToken cancellationToken)
    {
        var result = await RunGitAsync(new[] { "-C", directory, "diff", "--name-only", "--diff-filter=U" }, directory, cancellationToken).ConfigureAwait(false);

        var unmerged = result.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return unmerged.Length > 0 ? unmerged : ParseTargetPaths(r.Patch);
    }

    private async Task<IntegrationResult> CommitAndPushAsync(string directory, IntegrationRequest request, IReadOnlyList<ResolvedContribution> resolved, CancellationToken cancellationToken)
    {
        if (!await HasStagedChangesAsync(directory, cancellationToken).ConfigureAwait(false))
            return IntegrationResult.Build(IntegrationStatus.Empty, null, resolved.Select(r => r.Applied()).ToList(), "every contribution was a no-op — nothing to integrate");

        await CommitAsync(directory, resolved.Count, cancellationToken).ConfigureAwait(false);

        var pushBlock = await PublishAsync(directory, request, cancellationToken).ConfigureAwait(false);

        return pushBlock is null
            ? IntegrationResult.Build(IntegrationStatus.Clean, request.IntegrationBranch, resolved.Select(r => r.Applied()).ToList())
            : Aborted(resolved, pushBlock);
    }

    private async Task CommitAsync(string directory, int count, CancellationToken cancellationToken)
    {
        // commit.gpgsign=false is set inline so the automated integration commit never inherits a host/global
        // commit.gpgsign=true that would make it block on a signing key the unattended agent does not have — unlike the
        // capture commit (whose failure is swallowed), THIS failure propagates to an integration "Failed" status, so a
        // signing-on host would degrade the integration → no integrated head → a false acceptance miss. An internal
        // automation commit under a synthetic identity has no meaningful signature; identity is inline too so the
        // staging clone's git config is never mutated.
        var args = new[] { "-C", directory, "-c", "commit.gpgsign=false", "-c", "user.name=CodeSpace", "-c", "user.email=agent@codespace.local", "commit", "-m", $"Integrate {count} agent contribution(s)" };

        var result = await RunGitAsync(args, directory, cancellationToken).ConfigureAwait(false);

        if (result.Status != SandboxStatus.Success)
            throw new WorkspaceException($"git commit failed (exit {result.ExitCode}): {Summarize(result.Stderr)}");
    }

    /// <summary>
    /// Publish the integrated commit to the run-id-derived branch — fail-SAFE against clobbering foreign work. If the
    /// branch already existed at clone, integrate ONLY when its tree byte-equals ours (our own prior idempotent push);
    /// a differing tree is a reviewer fixup / concurrent rerun → refuse. Else a plain push creates it (git's own
    /// non-fast-forward rejection catches a concurrent post-clone creation). Returns a block reason, or null on success.
    /// </summary>
    private async Task<string?> PublishAsync(string directory, IntegrationRequest request, CancellationToken cancellationToken)
    {
        if (request.Token is null)
            return "integration was clean but NOT pushed — no write-capable credential was supplied";

        var existing = await TryRevParseAsync(directory, $"refs/remotes/origin/{request.IntegrationBranch}", cancellationToken).ConfigureAwait(false);

        if (existing is not null)
            return await ReconcileExistingBranchAsync(directory, existing, cancellationToken).ConfigureAwait(false);

        return await PushNewBranchAsync(directory, request, cancellationToken).ConfigureAwait(false);
    }

    private async Task<string?> ReconcileExistingBranchAsync(string directory, string existingSha, CancellationToken cancellationToken)
    {
        var ourTree = await RevParseTreeAsync(directory, "HEAD", cancellationToken).ConfigureAwait(false);
        var theirTree = await RevParseTreeAsync(directory, existingSha, cancellationToken).ConfigureAwait(false);

        return string.Equals(ourTree, theirTree, StringComparison.Ordinal)
            ? null // our own prior idempotent push (identical tree) — already published, no clobber
            : "remote integration branch advanced — refusing to clobber it";
    }

    private async Task<string?> PushNewBranchAsync(string directory, IntegrationRequest request, CancellationToken cancellationToken)
    {
        var refspec = $"HEAD:refs/heads/{request.IntegrationBranch}";

        var result = await RunGitAsync(new[] { "-C", directory, "push", "origin", refspec }, directory, cancellationToken).ConfigureAwait(false);

        if (result.Status == SandboxStatus.Success) return null;

        var output = $"{result.Stdout}\n{result.Stderr}";

        if (IsNonFastForward(output))
            return "remote integration branch advanced — refusing to clobber it";

        // A genuine infrastructure failure (auth / network / repo gone) — surface it (token redacted).
        throw new WorkspaceException($"git push failed (exit {result.ExitCode}): {LocalGitWorkspaceProvider.Redact(Summarize(result.Stderr), request.Token)}");
    }

    private async Task ResetToBaseAsync(string directory, string baseSha, CancellationToken cancellationToken)
    {
        // Restore the clone to a pristine base tree so NO half-merged / conflict-marked state survives the abort.
        await RunGitAsync(new[] { "-C", directory, "reset", "--hard", baseSha }, directory, cancellationToken).ConfigureAwait(false);
        await RunGitAsync(new[] { "-C", directory, "clean", "-fd" }, directory, cancellationToken).ConfigureAwait(false);
    }

    // ── Small git helpers ────────────────────────────────────────────────────────────

    private async Task<bool> HasStagedChangesAsync(string directory, CancellationToken cancellationToken)
    {
        var result = await RunGitAsync(new[] { "-C", directory, "diff", "--cached", "--quiet" }, directory, cancellationToken).ConfigureAwait(false);
        return result.ExitCode != 0; // --quiet exits non-zero when there ARE staged changes
    }

    private async Task<string?> TryRevParseAsync(string directory, string rev, CancellationToken cancellationToken)
    {
        var result = await RunGitAsync(new[] { "-C", directory, "rev-parse", "--verify", "--quiet", rev }, directory, cancellationToken).ConfigureAwait(false);
        return result.Status == SandboxStatus.Success ? result.Stdout.Trim() : null;
    }

    private async Task<string> RevParseTreeAsync(string directory, string rev, CancellationToken cancellationToken)
    {
        var result = await RunGitAsync(new[] { "-C", directory, "rev-parse", $"{rev}^{{tree}}" }, directory, cancellationToken).ConfigureAwait(false);
        return result.Stdout.Trim();
    }

    private async Task<SandboxResult> RunGitAsync(IReadOnlyList<string> args, string? workingDirectory, CancellationToken cancellationToken)
    {
        try
        {
            return await _runners.Resolve(Kind).RunAsync(
                new SandboxSpec { Command = "git", Args = args, WorkingDirectory = workingDirectory, TimeoutSeconds = GitTimeoutSeconds }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new WorkspaceException($"git could not run: {ex.Message}", ex);
        }
    }

    // ── Pure helpers ─────────────────────────────────────────────────────────────────

    /// <summary>The whole set aborted: nothing pushed, base restored. Each contribution reflects whether ITS work is preserved (a pushed branch) or lost.</summary>
    private static IntegrationResult Aborted(IReadOnlyList<ResolvedContribution> resolved, string reason) =>
        IntegrationResult.Build(IntegrationStatus.Conflicted, null, resolved.Select(r => r.ToOutcome()).ToList(), reason);

    private static bool IsNonFastForward(string output) =>
        output.Contains("non-fast-forward", StringComparison.OrdinalIgnoreCase)
        || output.Contains("fetch first", StringComparison.OrdinalIgnoreCase)
        || output.Contains("[rejected]", StringComparison.OrdinalIgnoreCase);

    /// <summary>Best-effort target paths from a unified diff's <c>+++ b/&lt;path&gt;</c> headers — the conflict-file fallback when the index has no unmerged entries.</summary>
    private static IReadOnlyList<string> ParseTargetPaths(string patch) =>
        patch.Split('\n')
            .Where(l => l.StartsWith("+++ b/", StringComparison.Ordinal))
            .Select(l => l["+++ b/".Length..].Trim())
            .Where(p => p.Length > 0 && p != "/dev/null")
            .Distinct()
            .ToList();

    private static string Short(string? sha) => string.IsNullOrEmpty(sha) ? "(none)" : sha.Length <= 8 ? sha : sha[..8];

    private static string Summarize(string stderr) => string.IsNullOrWhiteSpace(stderr) ? "(no stderr)" : stderr.Trim().Replace("\n", " ");

    private static void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best-effort */ }
    }

    private static void TryDeleteDirectory(string directory)
    {
        try { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); } catch { /* best-effort: a leaked temp clone is reclaimed by the workspace janitor */ }
    }

    /// <summary>A contribution paired with its resolved patch + the running disposition the integrator records as it proceeds. Mutable scratch (not a persisted noun) — projects to the immutable <see cref="ContributionOutcome"/>.</summary>
    private sealed class ResolvedContribution
    {
        public ResolvedContribution(BranchContribution contribution, string patch)
        {
            Contribution = contribution;
            Patch = patch;
        }

        public BranchContribution Contribution { get; }
        public string Patch { get; }

        private ContributionDisposition _disposition = ContributionDisposition.Applied;
        private string? _reason;
        private IReadOnlyList<string> _conflictedFiles = Array.Empty<string>();

        public void Block(string reason)
        {
            _disposition = string.IsNullOrEmpty(Contribution.ProducedBranch) ? ContributionDisposition.Unintegrable : ContributionDisposition.Conflicted;
            _reason = reason;
        }

        public void Conflict(string reason, IReadOnlyList<string> conflictedFiles)
        {
            Block(reason);
            _conflictedFiles = conflictedFiles;
        }

        public ContributionOutcome Applied() => new() { Label = Contribution.Label, Disposition = ContributionDisposition.Applied };

        public ContributionOutcome ToOutcome() => new()
        {
            Label = Contribution.Label,
            Disposition = _disposition,
            FallbackBranch = _disposition == ContributionDisposition.Applied ? null : Contribution.ProducedBranch,
            ConflictedFiles = _conflictedFiles,
            Reason = _reason,
        };
    }
}
