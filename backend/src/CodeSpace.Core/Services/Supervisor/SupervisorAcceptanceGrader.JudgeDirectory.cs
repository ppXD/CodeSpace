using CodeSpace.Core.Services.Agents.Eval.Benchmark;
using CodeSpace.Core.Services.Agents.Eval.Benchmark.Graders;
using CodeSpace.Core.Services.Agents.Sandbox;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Agents.Benchmark;
using Microsoft.Extensions.Logging;

namespace CodeSpace.Core.Services.Supervisor;

/// <summary>
/// The judge's DIRECTORY on the repo lane, once its program file has been put back from base.
///
/// <para>A derived judge's verdict is rarely decided by its file alone: the module it imports, the case it runs and the
/// hook its runner loads all live beside it. Restoring that whole directory from base, though, reverts honest work —
/// a co-located test's subject (<c>src/test_calc.py</c> beside <c>src/calc.py</c>), the expected output the goal asked to
/// update, a regression test the goal asked to add — and no retry could ever pass. So the directory is GUARDED instead:</para>
/// <list type="bullet">
///   <item>the program file is the base's (restored by the steps before this);</item>
///   <item>a file a runtime loads by its mere presence (<see cref="OracleGuard.IsAutoLoaded"/>) is put back to base
///         — an edited one is a voided TAMPER, an added one is discarded;</item>
///   <item>every other difference is the candidate's work and is KEPT, and the grade is labelled UNVERIFIED with the
///         paths, because the judge may read them. Only a directory with no kept difference is pinned for the runtime
///         (<see cref="BenchmarkGradingContext.PinnedOraclePaths"/>), so a python judge puts it first on its path only
///         when every byte in it is the base's.</item>
/// </list>
///
/// <para>The comparison never asks git which files are ignored — the candidate writes the <c>.gitignore</c>. Before the
/// setup step it lists every tracked difference and every untracked file; after the setup step and after the check it
/// compares the scope's CONTENT with a fingerprint taken just before (<see cref="OracleTree.Fingerprint"/>), so what the
/// setup changes is put back or reported, never silently deleted, and a check that rewrites platform-owned judge bytes
/// while it runs loses its pass (<see cref="OracleGuard.AfterCheck"/>). An operator-declared (authored) path stays the
/// platform's whole, exactly as declared.</para>
/// </summary>
public sealed partial class SupervisorAcceptanceGrader
{
    private const string ChangedBySetupPrefix = OracleGuard.TamperNoteMarker + " — the setup step changed platform-owned judge bytes, restored from base:";

    private const string HooksDiscardedPrefix = "judge directory: files a runtime loads by their presence were discarded:";

    private const string HooksDiscardedBySetupPrefix = "judge directory: files the setup step added that a runtime loads by their presence were discarded:";

    private const string KeptReason = "the judge's directory holds the candidate's own bytes, kept as written";

    private const string KeptBySetupReason = "the setup step left files beside the judge, kept as written";

    private const string UncomparedAfterSetupReason = "the judge's directory is too large to compare after the setup step";

    /// <summary>What the git restore steps leave for the directory guard: the clone, the contract, its anchor, the paths just restored, and the candidate's changes to them (null when none).</summary>
    private sealed record JudgeRestore(string Directory, SupervisorAcceptanceSpec Spec, OracleAnchor Anchor, IReadOnlyList<string> Paths, string? Tampered);

    /// <summary>
    /// The restore's outcome once every judge directory is guarded: Clean or Tampered on the program files and any hook
    /// put back, plus the neutral account of discarded hooks and the UNVERIFIED account of kept differences.
    /// </summary>
    private async Task<OracleProtectionOutcome> GuardJudgeDirectoriesAsync(JudgeRestore restore, int timeoutSeconds, CancellationToken cancellationToken)
    {
        var scopes = GuardedScopes(restore);

        var account = await ReconcileNeighbourhoodsAsync(restore, scopes, timeoutSeconds, cancellationToken).ConfigureAwait(false);

        if (account.Failure is not null) return OracleProtectionOutcome.Fail(account.Failure);

        var restored = new RestoredOracle(restore.Anchor.BaseSha!, restore.Paths, scopes, account.Kept.ToHashSet(StringComparer.Ordinal));
        var tampered = string.Join('\n', new[] { restore.Tampered }.Concat(account.Tampered).Where(line => !string.IsNullOrEmpty(line)));
        var shortSha = restored.BaseSha[..Math.Min(12, restored.BaseSha.Length)];

        var outcome = tampered.Length == 0
            ? OracleProtectionOutcome.Clean($"oracle: {restore.Paths.Count} protected path(s) restored from {shortSha} (no candidate changes)", restored)
            : OracleProtectionOutcome.Tampered($"{OracleGuard.TamperNoteMarker} — candidate changed protected path(s), restored from base:\n{tampered}", tampered, restored);

        return outcome.WithAccount(HooksDiscardedPrefix, account.Discarded).WithKept(KeptReason, account.Kept).WithSubject(Subject(restore.Spec, restore.Anchor, PlatformOwnedPrograms(scopes)));
    }

    /// <summary>What the subject account counts as protected: a scope the platform owns whole, and only the judge files of a guarded directory — a subject the command runs from inside that directory is still the candidate's own bytes.</summary>
    private static IReadOnlyList<string> PlatformOwnedPrograms(IReadOnlyList<GuardedScope> scopes) =>
        scopes.SelectMany(scope => scope.Whole ? new[] { scope.Path } : scope.Programs).ToList();

    /// <summary>
    /// The scopes this grade guards. An AUTHORED path is the platform's whole, as declared. A derived judge at the
    /// repository root owns only its file (its directory is the candidate's whole tree). A derived judge in a
    /// subdirectory owns its file, and its directory is guarded around it.
    /// </summary>
    private static IReadOnlyList<GuardedScope> GuardedScopes(JudgeRestore restore)
    {
        var programs = AcceptanceOracleProtection.CommandProgramCandidates(restore.Spec);

        if (AcceptanceOracleProtection.AuthoredOracleCandidates(restore.Spec, restore.Anchor.FloorPrograms).Count > 0)
            return restore.Paths.Select(path => AsScopePath(restore.Directory, path)).Distinct(StringComparer.Ordinal).Select(path => new GuardedScope(path, Whole: true, programs.Where(p => AcceptanceOracleProtection.Covers(new[] { path }, p)).ToList())).ToList();

        return restore.Paths.GroupBy(AcceptanceOracleProtection.JudgeScope, StringComparer.Ordinal).Select(group => new GuardedScope(group.Key, Whole: !group.Key.EndsWith('/'), group.ToList())).ToList();
    }

    /// <summary>A restored path as a scope: a directory always ends with <c>/</c>, however the contract spelled it.</summary>
    private static string AsScopePath(string directory, string path) => path.EndsWith('/') || !Directory.Exists(Path.Combine(directory, path)) ? path : path + "/";

    /// <summary>Put each guarded directory's hooks back to base and account for everything else that differs from base there.</summary>
    private async Task<JudgeDirectoryAccount> ReconcileNeighbourhoodsAsync(JudgeRestore restore, IReadOnlyList<GuardedScope> scopes, int timeoutSeconds, CancellationToken cancellationToken)
    {
        var neighbourhoods = scopes.Where(scope => !scope.Whole).ToList();

        if (neighbourhoods.Count == 0) return JudgeDirectoryAccount.Empty;

        var listing = await ListJudgeDirectoryChangesAsync(restore.Directory, restore.Anchor.BaseSha!, neighbourhoods.Select(scope => scope.Path).ToList(), timeoutSeconds, cancellationToken).ConfigureAwait(false);

        if (listing.Failure is not null) return JudgeDirectoryAccount.Failed(listing.Failure);

        var programs = scopes.SelectMany(scope => scope.Programs).ToHashSet(StringComparer.Ordinal);
        var changes = listing.Changes.Where(change => !programs.Contains(change.Path)).ToList();

        var changedHooks = changes.Where(change => OracleGuard.IsAutoLoaded(change.Path) && !change.Added).Select(change => change.Path).ToList();
        var addedHooks = changes.Where(change => OracleGuard.IsAutoLoaded(change.Path) && change.Added).Select(change => change.Path).ToList();

        var failure = await RestoreFromBaseAsync(restore.Directory, restore.Anchor.BaseSha!, changedHooks, timeoutSeconds, cancellationToken).ConfigureAwait(false);

        if (failure is not null) return JudgeDirectoryAccount.Failed(failure);

        foreach (var hook in addedHooks) OracleTree.RemovePath(restore.Directory, hook);

        return new JudgeDirectoryAccount(changedHooks, addedHooks, changes.Where(change => !OracleGuard.IsAutoLoaded(change.Path)).Select(change => change.Path).OrderBy(path => path, StringComparer.Ordinal).ToList(), null);
    }

    /// <summary>
    /// Every path under <paramref name="directories"/> that differs from <paramref name="baseSha"/>: the tracked differences
    /// (<c>git diff --name-status</c>, which also covers what a patch staged) and EVERY untracked file — <c>git ls-files -o</c>
    /// with no exclude rule, so a file the candidate's own <c>.gitignore</c> hides is listed like any other.
    /// </summary>
    private async Task<(IReadOnlyList<JudgeDirectoryChange> Changes, BenchmarkGrade? Failure)> ListJudgeDirectoryChangesAsync(string directory, string baseSha, IReadOnlyList<string> directories, int timeoutSeconds, CancellationToken cancellationToken)
    {
        var runner = _runners.Resolve(GradingRunnerKind);

        var tracked = await runner.RunAsync(GitSpec(directory, timeoutSeconds, new[] { "diff", "--name-status", "--no-renames", "-z", baseSha, "--" }.Concat(directories)), cancellationToken).ConfigureAwait(false);
        var untracked = await runner.RunAsync(GitSpec(directory, timeoutSeconds, new[] { "ls-files", "-o", "-z", "--" }.Concat(directories)), cancellationToken).ConfigureAwait(false);

        if (tracked.Status != SandboxStatus.Success || tracked.ExitCode != 0 || untracked.Status != SandboxStatus.Success || untracked.ExitCode != 0)
        {
            var stderr = Summarize(tracked.Stderr + " " + untracked.Stderr);
            _logger.LogWarning("Comparing the judge's directory with {BaseSha} failed in {Directory}: {Stderr}", baseSha, directory, stderr);
            return (Array.Empty<JudgeDirectoryChange>(), Failed($"oracle-restore-failed: {stderr}", GradeFailureClass.Environment));
        }

        return (ParseNameStatus(tracked.Stdout).Concat(Fields(untracked.Stdout).Select(path => new JudgeDirectoryChange(path, Added: true))).DistinctBy(change => change.Path, StringComparer.Ordinal).ToList(), null);
    }

    /// <summary><c>git diff --name-status -z</c> output: a status field then a path field, repeated. <c>A</c> means the base never had the path.</summary>
    private static IEnumerable<JudgeDirectoryChange> ParseNameStatus(string output)
    {
        var fields = Fields(output);

        for (var i = 0; i + 1 < fields.Count; i += 2)
            yield return new JudgeDirectoryChange(fields[i + 1], Added: fields[i].StartsWith('A'));
    }

    private static IReadOnlyList<string> Fields(string output) => output.Split('\0', StringSplitOptions.RemoveEmptyEntries).ToList();

    /// <summary><c>git checkout &lt;base&gt; -- &lt;paths&gt;</c> after making every directory on the way to each path a real one, so the checkout can never write through a link the candidate left there. Null on success (or nothing to do), else the fail-closed grade.</summary>
    private async Task<BenchmarkGrade?> RestoreFromBaseAsync(string directory, string baseSha, IReadOnlyList<string> paths, int timeoutSeconds, CancellationToken cancellationToken)
    {
        if (paths.Count == 0) return null;

        foreach (var path in paths) OracleTree.EnsureRealParents(directory, path);

        var restore = await _runners.Resolve(GradingRunnerKind).RunAsync(GitSpec(directory, timeoutSeconds, new[] { "checkout", baseSha, "--" }.Concat(paths)), cancellationToken).ConfigureAwait(false);

        if (restore.Status == SandboxStatus.Success && restore.ExitCode == 0) return null;

        _logger.LogWarning("Restoring {Count} judge path(s) from {BaseSha} failed in {Directory}: {Stderr}", paths.Count, baseSha, directory, Summarize(restore.Stderr));

        return Failed($"oracle-restore-failed: {Summarize(restore.Stderr)}", GradeFailureClass.Environment);
    }

    /// <summary>
    /// After the setup step: compare the guarded scopes with their fingerprint from just before it. Platform-owned bytes
    /// it changed are put back from base and reported as a voided TAMPER; hooks it added are discarded; anything else it
    /// added (a generated case, a build output) is KEPT and reported, never deleted — an honest setup that builds beside
    /// the judge must not fail silently. A restore that cannot complete fails closed, exactly like the first one.
    /// </summary>
    private async Task<OracleProtectionOutcome> ReassertOracleAsync(string directory, OracleProtectionOutcome protection, OracleTree.ScopeFingerprint? beforeSetup, int timeoutSeconds, CancellationToken cancellationToken)
    {
        if (protection.Restored is not { } restored || beforeSetup is null) return protection;

        var after = restored.Fingerprint(directory);

        if (!beforeSetup.Complete || !after.Complete) return await ReassertUncomparedAsync(directory, protection, restored, timeoutSeconds, cancellationToken).ConfigureAwait(false);

        var comparison = beforeSetup.Compare(after);
        var reverted = comparison.Changed.Where(restored.IsPlatformOwned).ToList();
        var hooks = comparison.Added.Where(OracleGuard.IsAutoLoaded).ToList();
        var outputs = comparison.Added.Where(path => !OracleGuard.IsAutoLoaded(path)).ToList();

        var failure = await RestoreFromBaseAsync(directory, restored.BaseSha, reverted, timeoutSeconds, cancellationToken).ConfigureAwait(false);

        if (failure is not null) return OracleProtectionOutcome.Fail(failure);

        foreach (var hook in hooks) OracleTree.RemovePath(directory, hook);

        return protection.WithAccount(ChangedBySetupPrefix, reverted).WithAccount(HooksDiscardedBySetupPrefix, hooks).WithKept(KeptBySetupReason, outputs) with { Restored = restored.Keeping(outputs) };
    }

    /// <summary>A scope too large to fingerprint cannot be compared: put its restored paths back from base regardless, and label the grade — every scope now counts as holding bytes the platform did not check.</summary>
    private async Task<OracleProtectionOutcome> ReassertUncomparedAsync(string directory, OracleProtectionOutcome protection, RestoredOracle restored, int timeoutSeconds, CancellationToken cancellationToken)
    {
        var failure = await RestoreFromBaseAsync(directory, restored.BaseSha, restored.Pathspecs, timeoutSeconds, cancellationToken).ConfigureAwait(false);

        if (failure is not null) return OracleProtectionOutcome.Fail(failure);

        return protection with { IntegrityNote = OracleRuntime.CombineNotes(protection.IntegrityNote, OracleRuntime.UnverifiedNote(UncomparedAfterSetupReason)), Restored = restored.Keeping(restored.ScopePaths) };
    }

    /// <summary>One guarded scope: its path (a file, or a directory ending in <c>/</c>), whether the platform owns it WHOLE (authored, or a root-level judge file) or only guards it around its judge, and the judge program files inside it.</summary>
    private sealed record GuardedScope(string Path, bool Whole, IReadOnlyList<string> Programs);

    /// <summary>One path in a guarded directory that differs from base, and whether the base never had it.</summary>
    private sealed record JudgeDirectoryChange(string Path, bool Added);

    /// <summary>What reconciling the guarded directories did: hooks put back (a tamper), hooks discarded, differences kept — or the fail-closed grade.</summary>
    private sealed record JudgeDirectoryAccount(IReadOnlyList<string> Tampered, IReadOnlyList<string> Discarded, IReadOnlyList<string> Kept, BenchmarkGrade? Failure)
    {
        public static readonly JudgeDirectoryAccount Empty = new(Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(), null);

        public static JudgeDirectoryAccount Failed(BenchmarkGrade failure) => Empty with { Failure = failure };
    }

    /// <summary>
    /// What a completed restore leaves for the rest of the grade: the base it restored from, the pathspecs the git steps
    /// restored (to restore again when nothing else can be compared), the guarded scopes, and the candidate's bytes kept
    /// inside them. Every other byte in a scope is platform-owned.
    /// </summary>
    private sealed record RestoredOracle(string BaseSha, IReadOnlyList<string> Pathspecs, IReadOnlyList<GuardedScope> Scopes, IReadOnlySet<string> Kept)
    {
        public IReadOnlyList<string> ScopePaths => Scopes.Select(scope => scope.Path).ToList();

        /// <summary>The paths holding only platform-owned bytes, for the seal and the check's runtime: a scope with nothing kept in it whole, otherwise just its judge files.</summary>
        public IReadOnlyList<string> Pins => Scopes.SelectMany(scope => IsPristine(scope) ? new[] { scope.Path } : scope.Programs).Distinct(StringComparer.Ordinal).ToList();

        public bool IsPlatformOwned(string path) => !Kept.Contains(path) && AcceptanceOracleProtection.Covers(ScopePaths, path);

        public OracleTree.ScopeFingerprint Fingerprint(string directory) => OracleTree.Fingerprint(directory, ScopePaths);

        /// <summary>This restore with <paramref name="paths"/> kept as well.</summary>
        public RestoredOracle Keeping(IEnumerable<string> paths) => this with { Kept = Kept.Concat(paths).ToHashSet(StringComparer.Ordinal) };

        private bool IsPristine(GuardedScope scope) => !Kept.Any(path => AcceptanceOracleProtection.Covers(new[] { scope.Path }, path));
    }
}
