using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Services.Agents.Eval.Benchmark;
using CodeSpace.Core.Services.Agents.Eval.Benchmark.Graders;
using CodeSpace.Core.Services.Agents.Publish;
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
    /// <summary>
    /// The acceptance-evaluation machinery's version, stamped onto every receipt this funnel mints (Q-freeze
    /// item: a verdict from a superseded evaluator is re-qualification input, not truth). BUMP this constant in
    /// the SAME PR as any change to grading semantics — oracle dispatch, restore/tamper behavior, evidence
    /// capture, fail-closed arms. Pinned by test; the literal is the wire value on durable receipts.
    /// </summary>
    public const string EvaluatorVersion = "supervisor-acceptance/v4";   // v4: an argv oracle's own program file is protected from candidate tampering even when no ProtectedPaths were authored

    /// <summary>The grading clone + oracle commands run on the worker host's own local runner. NOT the deployment
    /// default (<c>AgentDefaultRunnerSetting</c>): this funnel never reads a caller-supplied runner kind, and the
    /// host-side git it drives goes through the same local runner <c>RemoteTipResolver</c> resolves.</summary>
    private const string GradingRunnerKind = SandboxKinds.Local;
    private const int CloneTimeoutSeconds = 300;

    private readonly IAgentWorkspaceResolver _workspaceResolver;
    private readonly IWorkspaceProviderRegistry _providers;
    private readonly ISandboxRunnerRegistry _runners;
    private readonly IBenchmarkGraderRegistry _graders;
    private readonly IArtifactOffloader _offloader;
    private readonly Workflows.Artifacts.IArtifactStore _artifacts;
    private readonly IArtifactManifestStore _artifactManifests;
    private readonly ILogger<SupervisorAcceptanceGrader> _logger;

    public SupervisorAcceptanceGrader(IAgentWorkspaceResolver workspaceResolver, IWorkspaceProviderRegistry providers, ISandboxRunnerRegistry runners, IBenchmarkGraderRegistry graders, IArtifactOffloader offloader, Workflows.Artifacts.IArtifactStore artifacts, IArtifactManifestStore artifactManifests, ILogger<SupervisorAcceptanceGrader> logger)
    {
        _artifactManifests = artifactManifests;
        _workspaceResolver = workspaceResolver;
        _providers = providers;
        _runners = runners;
        _graders = graders;
        _offloader = offloader;
        _artifacts = artifacts;
        _logger = logger;
    }

    public Task<BenchmarkGrade> GradeAsync(Guid repositoryId, Guid teamId, string branch, SupervisorAcceptanceSpec spec, int timeoutSeconds, CancellationToken cancellationToken) =>
        GradeAsync(repositoryId, teamId, branch, spec, timeoutSeconds, oracleBaseSha: null, cancellationToken);

    public async Task<BenchmarkGrade> GradeAsync(Guid repositoryId, Guid teamId, string branch, SupervisorAcceptanceSpec spec, int timeoutSeconds, string? oracleBaseSha, CancellationToken cancellationToken)
    {
        try
        {
            var clone = await _workspaceResolver.ResolveByRepositoryIdAsync(repositoryId, teamId, cancellationToken, @ref: branch).ConfigureAwait(false)
                ?? throw new WorkspaceException($"Repository {repositoryId} resolved to no clone request for acceptance grading.");

            // The oracle restore reads the BASE commit's bytes, and a workspace request defaults to the agents'
            // Depth=1 clone, which holds only the candidate tip. `git checkout <base> -- <paths>` then dies with
            // "reference is not a tree" and the grade fails closed as oracle-restore-failed (Environment) — so a
            // contract that named protected paths did not merely go unprotected, it stopped being gradeable at all,
            // and read as infrastructure noise while doing it. IntegrationRequest.Depth records the same lesson for
            // its 3-way apply: an operation that reaches back to the base needs the base history.
            if (MayProtect(spec) && !string.IsNullOrEmpty(oracleBaseSha))
                clone = clone with { Depth = 0 };

            await using var workspace = await _providers.Resolve(GradingRunnerKind).PrepareAsync(WorkspaceProvisionRequest.FromSingle(clone), cancellationToken).ConfigureAwait(false);

            var protection = await RestoreOracleAsync(workspace.Directory, spec, oracleBaseSha, timeoutSeconds, cancellationToken).ConfigureAwait(false);

            if (protection.Failure is not null) return protection.Failure;

            return await GradeWorkspaceAsync(workspace.Directory, spec, teamId, timeoutSeconds, cancellationToken, protection).ConfigureAwait(false);
        }
        catch (WorkspaceException ex)
        {
            // A repo/branch we cannot clone cannot be verified → fail closed to "not accepted" (never a silent pass).
            _logger.LogWarning(ex, "Acceptance grading could not clone {RepositoryId} at {Branch}; failing closed to not-accepted", repositoryId, branch);
            return Failed($"clone-failed: {ex.Message}", GradeFailureClass.Environment);
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

    public async Task<BenchmarkGrade> GradeDirectoryAsync(string directory, SupervisorAcceptanceSpec spec, Guid teamId, int timeoutSeconds, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(directory))
            return new BenchmarkGrade { Passed = false, Detail = "grade-error: the workspace directory no longer exists", Class = Messages.Agents.Benchmark.GradeFailureClass.Environment };

        return await GradeWorkspaceAsync(directory, spec, teamId, timeoutSeconds, cancellationToken).ConfigureAwait(false);
    }

    public async Task<BenchmarkGrade> GradeCapturedAsync(Guid agentRunId, Guid teamId, SupervisorAcceptanceSpec spec, int timeoutSeconds, CancellationToken cancellationToken)
    {
        var directory = Path.Combine(LocalGitWorkspaceProvider.WorkspacesRoot, "captured-" + Guid.NewGuid().ToString("N"));

        try
        {
            if (await MaterializeCapturedAsync(agentRunId, teamId, directory, cancellationToken).ConfigureAwait(false) == 0)
            {
                _logger.LogWarning("Agent run {AgentRunId}: no captured deliverable to grade the repo-less acceptance against — failing closed as {Detail}", agentRunId, ISupervisorAcceptanceGrader.NoDeliverablesCaptured);

                return Failed(ISupervisorAcceptanceGrader.NoDeliverablesCaptured, GradeFailureClass.Genuine);
            }

            return await GradeWorkspaceAsync(directory, spec, teamId, timeoutSeconds, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The world-rebuild itself failed (a CAS read fault, a disk fault) — the check never RAN, so this is the
            // grader's own fault class, never a verdict on the work. Fail closed rather than strand the fold.
            _logger.LogWarning(ex, "Agent run {AgentRunId}: could not rebuild the captured deliverables to grade against; failing closed to not-accepted", agentRunId);

            return Failed($"grade-error: {ex.Message}", GradeFailureClass.GraderFault);
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); } catch { /* best-effort: a leaked temp dir on the worker's ephemeral disk is harmless */ }
        }
    }

    /// <summary>
    /// Write every CURRENT manifest row's bytes to <paramref name="directory"/> under its own logical path, and
    /// return how many landed. A row whose logical path would escape the rebuilt root, or whose CAS bytes no longer
    /// resolve team-scoped, is SKIPPED with a named warning — the oracle then judges the world as it actually is (an
    /// absent deliverable is its business), the same posture the capture side takes.
    /// </summary>
    private async Task<int> MaterializeCapturedAsync(Guid agentRunId, Guid teamId, string directory, CancellationToken cancellationToken)
    {
        var rows = await _artifactManifests.ListForAgentRunAsync(agentRunId, teamId, cancellationToken).ConfigureAwait(false);

        Directory.CreateDirectory(directory);

        var written = 0;

        foreach (var row in LatestAttemptRows(rows))
        {
            var target = Path.GetFullPath(Path.Combine(directory, row.LogicalPath));

            if (!WorkspaceArtifactGuard.IsStrictlyWithin(directory, target))
            {
                _logger.LogWarning("Agent run {AgentRunId}: captured deliverable '{Path}' would escape the grading directory — not materialized", agentRunId, row.LogicalPath);
                continue;
            }

            var bytes = await _artifacts.GetBytesAsync(teamId, row.ContentArtifactId, cancellationToken).ConfigureAwait(false);

            if (bytes is null)
            {
                _logger.LogWarning("Agent run {AgentRunId}: captured deliverable '{Path}' has no resolvable bytes ({ArtifactId}) — not materialized", agentRunId, row.LogicalPath, row.ContentArtifactId);
                continue;
            }

            // A logical path can collide with one already materialized as a directory (or vice versa) — 'docs' and
            // 'docs/report.md' both being captured names. That is a rebuild-shaped problem, never a verdict on the
            // work, so it costs ONE row rather than failing the whole grade into a GraderFault.
            try { Directory.CreateDirectory(Path.GetDirectoryName(target)!); }
            catch (IOException ex)
            {
                _logger.LogWarning(ex, "Agent run {AgentRunId}: captured deliverable '{Path}' collides with another captured path — not materialized", agentRunId, row.LogicalPath);
                continue;
            }

            try { await File.WriteAllBytesAsync(target, bytes.Bytes, cancellationToken).ConfigureAwait(false); }
            catch (UnauthorizedAccessException ex)
            {
                _logger.LogWarning(ex, "Agent run {AgentRunId}: captured deliverable '{Path}' could not be written into the grading directory — not materialized", agentRunId, row.LogicalPath);
                continue;
            }
            catch (IOException ex)
            {
                _logger.LogWarning(ex, "Agent run {AgentRunId}: captured deliverable '{Path}' could not be written into the grading directory — not materialized", agentRunId, row.LogicalPath);
                continue;
            }

            written++;
        }

        return written;
    }

    /// <summary>
    /// The LATEST attempt's rows only — current (unsuperseded) rows at the run's highest <c>FenceEpoch</c>.
    ///
    /// <para>Supersession is keyed per <c>(AgentRunId, FenceEpoch, LogicalPath)</c>, so a retry-resume that bumps the
    /// epoch leaves the PRIOR attempt's rows current too. Materializing those rebuilds a world that never existed:
    /// a deliverable the latest attempt deliberately deleted still satisfies <c>ArtifactPresent</c>, and two epochs'
    /// copies of the same path race to overwrite each other in whatever order the store returned them — so the graded
    /// bytes are decided by row order, not by the attempt. Filtering per-path would fix only the second; a deleted
    /// deliverable would still be resurrected by its older row. The attempt is the unit of truth, so the whole world
    /// comes from one epoch.</para>
    /// </summary>
    private static IEnumerable<Persistence.Entities.ArtifactManifest> LatestAttemptRows(IReadOnlyList<Persistence.Entities.ArtifactManifest> rows)
    {
        var current = rows.Where(r => r.SupersededByManifestId is null).ToList();

        if (current.Count == 0) return current;

        var latest = current.Max(r => r.FenceEpoch);

        return current.Where(r => r.FenceEpoch == latest);
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

            // This is the live pre-completion two-carrier seam: executor capture may supply both a bounded inline copy
            // and the full artifact. The ref is authoritative; missing/corrupt/foreign bytes fail closed, never inline.
            var patch = await _offloader.ResolvePatchRequiredAsync(teamId, inlinePatch, patchArtifactId, cancellationToken).ConfigureAwait(false);

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

            // The patch is the candidate's work — its edits to protected oracle bytes are as void here as a
            // branch's are (the base sha is this path's own anchor, no manifest resolution needed).
            var protection = await RestoreOracleAsync(directory, spec, baseSha, timeoutSeconds, cancellationToken).ConfigureAwait(false);

            if (protection.Failure is not null) return protection.Failure;

            return await GradeWorkspaceAsync(directory, spec, teamId, timeoutSeconds, cancellationToken, protection).ConfigureAwait(false);
        }
        catch (WorkspaceException ex)
        {
            _logger.LogWarning(ex, "Acceptance grading could not clone {RepositoryId} at base {BaseSha}; failing closed to not-accepted", repositoryId, baseSha);
            return Failed($"clone-failed: {ex.Message}", GradeFailureClass.Environment);
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
            return Failed($"clone-failed: {ex.Message}", GradeFailureClass.Environment);
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

        var cloneResult = await _runners.Resolve(GradingRunnerKind).RunAsync(
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
            await LocalGitWorkspaceProvider.StripTokenFromRemoteAsync(_runners.Resolve(GradingRunnerKind), CloneTimeoutSeconds, _logger, clone.RepositoryUrl, directory, cancellationToken).ConfigureAwait(false);

        var checkoutResult = await _runners.Resolve(GradingRunnerKind).RunAsync(
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
            var result = await _runners.Resolve(GradingRunnerKind).RunAsync(
                new SandboxSpec { Command = "git", Args = new[] { "-C", directory, "apply", "--3way", patchFile }, WorkingDirectory = directory, TimeoutSeconds = 60 }, cancellationToken).ConfigureAwait(false);

            return result.Status == SandboxStatus.Success ? null : result.Stderr;
        }
        finally
        {
            try { File.Delete(patchFile); } catch { /* best-effort — the whole clone is discarded regardless */ }
        }
    }

    /// <summary>
    /// P3a-3 (B+V0+): the ORACLE's bytes are not the candidate's to edit. When the base sha is known and the
    /// contract yields protected paths — AUTHORED, or C3-DERIVED from the acceptance command's own program:
    /// (1) any candidate change under those paths is recorded as a TAMPER note, in the evidence AND on the grade
    /// itself (visibility — the restore makes it void, the note makes it seen); (2) the paths are restored from
    /// the base, so the check runs the BASE's judge against the CANDIDATE's code. A restore that cannot complete
    /// fails CLOSED (Environment): an unprotectable oracle cannot verify anything. A judge that could have been
    /// protected but was not (no base, or a base we could not probe) grades UNPROTECTED and SAYS so.
    /// </summary>
    private async Task<OracleProtectionOutcome> RestoreOracleAsync(string directory, SupervisorAcceptanceSpec spec, string? oracleBaseSha, int timeoutSeconds, CancellationToken cancellationToken)
    {
        // C3: a protectable judge that goes UNPROTECTED says so, on the grade itself. Silence here was readable as
        // protection — the one reading it (a decider weighing a pass, an operator reading a receipt) cannot tell
        // "the oracle was restored and untouched" from "nobody ever anchored it" unless the second case speaks.
        if (string.IsNullOrEmpty(oracleBaseSha))
            return CommandOracleCandidates(spec).Count == 0 ? OracleProtectionOutcome.None : OracleProtectionOutcome.Unprotected("no base recorded");

        var runner = _runners.Resolve(GradingRunnerKind);

        var (paths, probeFailed) = await ResolveProtectedPathsAsync(runner, directory, spec, oracleBaseSha, timeoutSeconds, cancellationToken).ConfigureAwait(false);

        if (probeFailed) return OracleProtectionOutcome.Unprotected("base probe failed");

        // No protected path is the QUIET case on purpose: either the contract names no judge file at all, or the
        // program it names is absent at base — which makes it the CANDIDATE's own creation ("add a check" work),
        // not an operator oracle that went unguarded.
        if (paths.Count == 0) return OracleProtectionOutcome.None;

        // Working-tree diff (no HEAD) so BOTH grade shapes see the candidate's changes: the branch clone's tree IS
        // the candidate commit, and the patch path's tree is base + an UNCOMMITTED apply (where base..HEAD is empty).
        var diff = await runner.RunAsync(GitSpec(directory, timeoutSeconds, new[] { "diff", "--name-only", oracleBaseSha, "--" }.Concat(paths)), cancellationToken).ConfigureAwait(false);
        var tampered = diff.Status == SandboxStatus.Success ? diff.Stdout.Trim() : null;

        var restore = await runner.RunAsync(GitSpec(directory, timeoutSeconds, new[] { "checkout", oracleBaseSha, "--" }.Concat(paths)), cancellationToken).ConfigureAwait(false);

        if (restore.Status != SandboxStatus.Success || restore.ExitCode != 0)
        {
            _logger.LogWarning("Oracle restore from {BaseSha} failed in {Directory}: {Stderr}", oracleBaseSha, directory, Summarize(restore.Stderr));
            return OracleProtectionOutcome.Fail(Failed($"oracle-restore-failed: {Summarize(restore.Stderr)}", GradeFailureClass.Environment));
        }

        // `git checkout` restores what EXISTS at base; it cannot remove what the candidate ADDED. Leaving additions
        // in place is a hole, not a rounding error: a protected directory is exactly where an auto-discovered hook
        // lives (pytest loads any conftest.py it finds), so a candidate could leave every oracle byte pristine, drop
        // one file beside them, and bend the verdict while the evidence announced the tamper VOIDED. Protected means
        // byte-identical to base, which includes absence.
        var removeFailure = await RemoveAddedPathsAsync(runner, directory, oracleBaseSha, paths, timeoutSeconds, cancellationToken).ConfigureAwait(false);

        if (removeFailure is not null) return OracleProtectionOutcome.Fail(removeFailure);

        if (string.IsNullOrEmpty(tampered))
            return OracleProtectionOutcome.Clean($"oracle: {paths.Count} protected path(s) restored from {oracleBaseSha[..Math.Min(12, oracleBaseSha.Length)]} (no candidate changes)");

        return OracleProtectionOutcome.Tampered($"ORACLE TAMPER VOIDED \u2014 candidate changed protected path(s), restored from base:\n{tampered}", tampered!);
    }

    /// <summary>
    /// What the protection step concluded: an optional fail-closed verdict, the EVIDENCE line (the full, possibly
    /// multi-line account), and the INTEGRITY note — a short single line that rides the grade itself, so a voided
    /// tamper or an unprotected judge reaches the journal / decider prompt / receipt instead of living only inside
    /// oracle output a talkative check can push out of the bounded tail.
    /// </summary>
    private readonly record struct OracleProtectionOutcome(BenchmarkGrade? Failure, string? EvidenceNote, string? IntegrityNote)
    {
        public static readonly OracleProtectionOutcome None = new(null, null, null);

        public static OracleProtectionOutcome Fail(BenchmarkGrade failure) => new(failure, null, null);

        /// <summary>The oracle was protected and the candidate left it alone — legible in the evidence, and deliberately silent on the grade (the quiet, dominant case).</summary>
        public static OracleProtectionOutcome Clean(string evidenceNote) => new(null, evidenceNote, null);

        public static OracleProtectionOutcome Tampered(string evidenceNote, string paths) =>
            new(null, evidenceNote, $"ORACLE TAMPER VOIDED \u2014 candidate changed protected path(s), restored from base: {Flatten(paths)}");

        /// <summary>
        /// A judge the contract names, graded with NOTHING anchoring it — said out loud ON THE GRADE so absence of
        /// a note can never be read as protection.
        ///
        /// <para>Deliberately NOT an evidence note. An evidence note is what mints the CAS artifact a receipt's
        /// <c>EvidenceRef</c> binds to, and admission caps an UNEVIDENCED verdict on a required obligation at
        /// InfraUnknown — so writing "there was no oracle integrity to record" into the evidence would hand a
        /// previously evidence-less grade an evidence binding, loosening admission on the strength of an absence.</para>
        /// </summary>
        public static OracleProtectionOutcome Unprotected(string why) => new(null, null, $"oracle: graded UNPROTECTED ({why})");

        /// <summary>The tamper list as ONE bounded line — the integrity note rides a detail string, never a paragraph.</summary>
        private static string Flatten(string paths)
        {
            var joined = string.Join(", ", paths.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

            return joined.Length <= IntegrityNoteMaxChars ? joined : joined[..IntegrityNoteMaxChars] + "\u2026";
        }

        private const int IntegrityNoteMaxChars = 200;
    }

    /// <summary>
    /// C3 — the paths this grade protects: the AUTHORED set when the contract named one (explicit wins outright —
    /// a model that scoped its own oracle is not second-guessed), otherwise the set DERIVED from the acceptance
    /// argv's own program file. The derivation is what gives the OPERATOR's floor a protected oracle: nothing in
    /// Core or the UI ever authored <c>ProtectedPaths</c>, so before it the most trusted check in the system ran
    /// whatever bytes the candidate left behind under that name.
    ///
    /// <para>Existence is answered off the base tree of THIS clone (one <c>ls-tree</c> over the candidate paths, no
    /// second fetch), because <c>git checkout &lt;base&gt; -- &lt;path&gt;</c> fails outright on a pathspec that
    /// does not exist at base — a derived guess must never turn a gradeable candidate into an Environment failure.
    /// The probe is best-effort for the same reason: a base we cannot read protects nothing rather than failing a
    /// grade that would otherwise stand.</para>
    /// </summary>
    private async Task<(IReadOnlyList<string> Paths, bool ProbeFailed)> ResolveProtectedPathsAsync(ISandboxRunner runner, string directory, SupervisorAcceptanceSpec spec, string oracleBaseSha, int timeoutSeconds, CancellationToken cancellationToken)
    {
        if (spec.ProtectedPaths is { Count: > 0 } authored) return (authored, false);

        var candidates = CommandOracleCandidates(spec);

        if (candidates.Count == 0) return (Array.Empty<string>(), false);

        var listing = await runner.RunAsync(GitSpec(directory, timeoutSeconds, new[] { "ls-tree", "-r", "--name-only", oracleBaseSha, "--" }.Concat(candidates)), cancellationToken).ConfigureAwait(false);

        if (listing.Status != SandboxStatus.Success || listing.ExitCode != 0)
        {
            _logger.LogWarning("Oracle path probe at {BaseSha} failed in {Directory}; grading with no derived protection: {Stderr}", oracleBaseSha, directory, Summarize(listing.Stderr));
            return (Array.Empty<string>(), true);
        }

        var present = listing.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.Ordinal);

        return (AcceptanceOracleProtection.DeriveProtectedPaths(spec.Command, present.Contains), false);
    }

    /// <summary>Whether a protected grade is possible at all — decided BEFORE the clone, because the restore and its probe both reach back to the base and so need its history (the agents' default shallow clone holds only the candidate tip).</summary>
    private static bool MayProtect(SupervisorAcceptanceSpec spec) => spec.ProtectedPaths is { Count: > 0 } || CommandOracleCandidates(spec).Count > 0;

    /// <summary>
    /// The argv's program candidates — empty for any oracle whose <c>Command</c> is NOT an argv. An
    /// <c>ArtifactPresent</c> contract's command is the list of deliverables the candidate must PRODUCE; restoring
    /// one of those from base would void the very work being verified, which is the opposite of protecting a judge.
    /// </summary>
    private static IReadOnlyList<string> CommandOracleCandidates(SupervisorAcceptanceSpec spec) =>
        spec.Kind is null or BenchmarkGradingKind.TestsPass ? AcceptanceOracleProtection.ProgramCandidates(spec.Command) : Array.Empty<string>();

    /// <summary>
    /// Deletes everything under the protected paths that the candidate ADDED relative to base — the half
    /// <c>git checkout</c> cannot do. Tracked additions come from the diff; untracked ones (the patch path applies
    /// into the working tree without committing) come from <c>git clean</c>, which is scoped to the same pathspecs
    /// so it can never touch the candidate's real work.
    /// </summary>
    private async Task<BenchmarkGrade?> RemoveAddedPathsAsync(ISandboxRunner runner, string directory, string oracleBaseSha, IReadOnlyList<string> paths, int timeoutSeconds, CancellationToken cancellationToken)
    {
        var added = await runner.RunAsync(GitSpec(directory, timeoutSeconds, new[] { "diff", "--name-only", "--diff-filter=A", oracleBaseSha, "--" }.Concat(paths)), cancellationToken).ConfigureAwait(false);

        if (added.Status != SandboxStatus.Success || added.ExitCode != 0)
        {
            _logger.LogWarning("Oracle addition scan from {BaseSha} failed in {Directory}: {Stderr}", oracleBaseSha, directory, Summarize(added.Stderr));
            return Failed($"oracle-restore-failed: {Summarize(added.Stderr)}", GradeFailureClass.Environment);
        }

        var files = added.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (files.Length > 0)
        {
            // -f because the paths are tracked in the candidate's tree; --ignore-unmatch keeps a race (a path already
            // gone) from failing a grade that is otherwise fine.
            var rm = await runner.RunAsync(GitSpec(directory, timeoutSeconds, new[] { "rm", "-rf", "--quiet", "--ignore-unmatch", "--" }.Concat(files)), cancellationToken).ConfigureAwait(false);

            if (rm.Status != SandboxStatus.Success || rm.ExitCode != 0)
            {
                _logger.LogWarning("Removing {Count} candidate-added protected path(s) failed in {Directory}: {Stderr}", files.Length, directory, Summarize(rm.Stderr));
                return Failed($"oracle-restore-failed: {Summarize(rm.Stderr)}", GradeFailureClass.Environment);
            }
        }

        // Untracked additions under the protected paths — the patch path's uncommitted apply. Best-effort: a clean
        // that cannot run is not worth failing an otherwise gradeable candidate over, and the tracked sweep above has
        // already closed the committed case.
        await runner.RunAsync(GitSpec(directory, timeoutSeconds, new[] { "clean", "-fdq", "--" }.Concat(paths)), cancellationToken).ConfigureAwait(false);

        return null;
    }

    private static SandboxSpec GitSpec(string directory, int timeoutSeconds, IEnumerable<string> args) => new()
    {
        Command = "git",
        Args = args.ToList(),
        WorkingDirectory = directory,
        TimeoutSeconds = timeoutSeconds,
    };

    private async Task<BenchmarkGrade> GradeWorkspaceAsync(string directory, SupervisorAcceptanceSpec spec, Guid teamId, int timeoutSeconds, CancellationToken cancellationToken, OracleProtectionOutcome protection = default)
    {
        if (spec.SetupCommand is { Count: > 0 } setupCommand)
        {
            var setupFailure = await RunSetupCommandAsync(setupCommand, directory, timeoutSeconds, cancellationToken).ConfigureAwait(false);
            if (setupFailure is not null) return setupFailure;
        }

        var context = BenchmarkGradingContext.ForAcceptance(spec, teamId, timeoutSeconds, directory, _runners.Resolve(GradingRunnerKind));

        var grade = await _graders.Resolve(spec.Kind ?? BenchmarkGradingKind.TestsPass).GradeAsync(context, cancellationToken).ConfigureAwait(false);

        if (protection.EvidenceNote is not null)
            grade = grade with { EvidenceText = $"{protection.EvidenceNote}\n{grade.EvidenceText}" };

        // The integrity note rides the GRADE, not just the evidence: the run-level stop fold carries pass + detail
        // only, and the bounded evidence tail keeps the END of oracle output — a talkative check pushes a prepended
        // note straight out of it. Without this the floor's own tamper never reached the journal or the decider.
        if (protection.IntegrityNote is not null)
            grade = grade with { OracleNote = protection.IntegrityNote };

        return await CaptureEvidenceAsync(grade, teamId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// P3a-1: the oracle run's output becomes a durable CAS artifact — the id a receipt's <c>EvidenceRef</c> binds
    /// to. ALWAYS stored (never inline-thresholded: an id is the contract, not a display optimization); the
    /// transient text is dropped either way. Best-effort with a loud log — a store fault degrades the receipt to
    /// evidence-less (admission batch 2 will read that as at-most-InfraUnknown), it never fails the grade itself.
    /// P5-2: the clipped <c>EvidenceTail</c> is folded BEFORE the store attempt, so the repair loop's diagnosis
    /// survives a store fault that the receipt's evidence binding does not.
    /// </summary>
    private async Task<BenchmarkGrade> CaptureEvidenceAsync(BenchmarkGrade grade, Guid teamId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(grade.EvidenceText)) return grade;

        grade = WithClippedEvidenceTail(grade);

        try
        {
            var id = await _artifacts.PutAsync(teamId, System.Text.Encoding.UTF8.GetBytes(grade.EvidenceText!), "text/plain", cancellationToken).ConfigureAwait(false);

            return grade with { EvidenceArtifactId = id, EvidenceText = null };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Storing oracle evidence failed; the grade stands evidence-less (a required contract reads at most InfraUnknown once admission tightens)");
            return grade with { EvidenceText = null };
        }
    }

    /// <summary>The inline diagnosis budget (P5-2): the trailing slice of the oracle's output kept on the grade for prompt/repair consumers. Small enough to ride the tape and the decider prompt per failed unit; the FULL text is always behind the CAS id.</summary>
    public const int EvidenceTailMaxChars = 2_048;

    /// <summary>Pure fold (P5-2): stamp the bounded TRAILING slice of <see cref="BenchmarkGrade.EvidenceText"/> onto <see cref="BenchmarkGrade.EvidenceTail"/> — the failure lives at the end of oracle output (the same convention the grader's own stdout/stderr tails use). No text → unchanged.</summary>
    internal static BenchmarkGrade WithClippedEvidenceTail(BenchmarkGrade grade) =>
        string.IsNullOrEmpty(grade.EvidenceText)
            ? grade
            : grade with { EvidenceTail = grade.EvidenceText!.Length <= EvidenceTailMaxChars ? grade.EvidenceText : grade.EvidenceText[^EvidenceTailMaxChars..] };

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

        var result = await _runners.Resolve(GradingRunnerKind).RunAsync(spec, cancellationToken).ConfigureAwait(false);

        if (result.Status == SandboxStatus.Success) return null;

        _logger.LogWarning("Acceptance grading's setup command failed in {Directory}: {Status} (exit {ExitCode}) {Stderr}", directory, result.Status, result.ExitCode, Summarize(result.Stderr));

        return result.Status == SandboxStatus.TimedOut
            ? Failed("setup-timed-out", GradeFailureClass.Environment)
            : Failed($"setup-failed: {Summarize(result.Stderr)}", GradeFailureClass.Environment);
    }

    private static BenchmarkGrade Failed(string detail, GradeFailureClass? failureClass = null) => new() { Passed = false, Detail = detail, Class = failureClass };

    private static string Summarize(string stderr) => string.IsNullOrWhiteSpace(stderr) ? "(no stderr)" : stderr.Trim().Replace("\n", " ");

    private static void TryDeleteDirectory(string directory)
    {
        try { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); } catch { /* best-effort — the workspace janitor reclaims an orphaned clone */ }
    }
}
