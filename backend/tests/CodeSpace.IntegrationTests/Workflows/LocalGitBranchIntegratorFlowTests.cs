using CodeSpace.Core.Services.Agents.Sandbox;
using CodeSpace.Core.Services.Agents.Sandbox.Runners;
using CodeSpace.Core.Services.Agents.Workspace;
using CodeSpace.Core.Services.Agents.Workspace.Integrators;
using CodeSpace.Core.Services.Agents.Workspace.Providers;
using CodeSpace.Core.Services.Workflows.Artifacts;
using CodeSpace.Messages.Agents;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows;

/// <summary>
/// 🟢 High-fidelity Integration: the model-free 極致 core of SOTA #3 — <see cref="LocalGitBranchIntegrator"/> against
/// REAL git on a bare local "remote", proving K agent contributions integrate into ONE branch or fail SAFE. NO model,
/// NO Postgres: the patches are real unified diffs captured the same way an agent produces them; the offloader is a
/// controlled fake (its team-gate is covered by the D2 offloader's own tests — here the integrator's CONTRACT with it
/// is what's under test: it threads <c>request.TeamId</c> and treats an empty resolve as unintegrable, never a silent
/// no-op).
///
/// <para>Crown jewels: K disjoint contributions integrate with ALL changes; a moved-base patch is anchored to the
/// RECORDED base (not the moved tip) so it is never the corrupt upstream+agent mix; a base-mismatched contribution is
/// REFUSED; a conflicting set fails SAFE (no branch pushed, the agents' work preserved); an idempotent re-run produces
/// the same single branch; a diverged remote branch is never clobbered; a cross-team artifact resolves empty and is
/// loudly unintegrable. Skips on Windows / when git is absent so a cross-host <c>dotnet test</c> stays clean.</para>
/// </summary>
[Trait("Category", "Integration")]
public sealed class LocalGitBranchIntegratorFlowTests
{
    // ── Crown jewel: K clean branches integrate into ONE branch with ALL changes ─────

    [Fact]
    public async Task K_clean_disjoint_contributions_integrate_into_one_branch_with_all_changes()
    {
        if (!await GitReadyAsync()) return;

        using var ctx = new IntegratorTestContext();
        var baseSha = await ctx.SeedBaseAsync(new() { ["a.txt"] = "a", ["b.txt"] = "b", ["c.txt"] = "c" });

        var a = await ctx.MakeContributionAsync("agent-a", baseSha, d => File.WriteAllText(Path.Combine(d, "a.txt"), "a-edited"));
        var b = await ctx.MakeContributionAsync("agent-b", baseSha, d => File.WriteAllText(Path.Combine(d, "b.txt"), "b-edited"));
        var c = await ctx.MakeContributionAsync("agent-c", baseSha, d => File.WriteAllText(Path.Combine(d, "d-new.txt"), "brand new"));

        var result = await ctx.NewIntegrator().IntegrateAsync(ctx.Request(baseSha, a, b, c), CancellationToken.None);

        result.Status.ShouldBe(IntegrationStatus.Clean);
        result.AppliedCount.ShouldBe(3);
        result.IntegratedBranch.ShouldBe(ctx.IntegrationBranch);

        (await ctx.RemoteFileAsync(ctx.IntegrationBranch, "a.txt")).Trim().ShouldBe("a-edited");
        (await ctx.RemoteFileAsync(ctx.IntegrationBranch, "b.txt")).Trim().ShouldBe("b-edited");
        (await ctx.RemoteFileAsync(ctx.IntegrationBranch, "d-new.txt")).Trim().ShouldBe("brand new", "every agent's change lands on the one integrated branch");
    }

    // ── Crown jewel: base-anchoring (the BLOCKER) — checkout the RECORDED base, not the moved tip ──

    [Fact]
    public async Task A_contribution_is_anchored_to_its_recorded_base_not_the_moved_remote_tip()
    {
        if (!await GitReadyAsync()) return;

        using var ctx = new IntegratorTestContext();
        var baseX = await ctx.SeedBaseAsync(new() { ["f.txt"] = "line1\nline2\nline3\n" });

        // The remote default branch ADVANCES past the base: line1 changes upstream (a NON-overlapping edit to the
        // agent's line3 change). The integrator must root the apply at the RECORDED base, not this moved tip.
        await ctx.AdvanceRemoteAsync(d => File.WriteAllText(Path.Combine(d, "f.txt"), "LINE1-upstream\nline2\nline3\n"));

        var agent = await ctx.MakeContributionAsync("agent-a", baseX, d => File.WriteAllText(Path.Combine(d, "f.txt"), "line1\nline2\nline3-agent\n"));

        var result = await ctx.NewIntegrator().IntegrateAsync(ctx.Request(baseX, agent), CancellationToken.None);

        result.Status.ShouldBe(IntegrationStatus.Clean);
        (await ctx.RemoteFileAsync(ctx.IntegrationBranch, "f.txt")).ShouldBe("line1\nline2\nline3-agent\n",
            "anchored to the RECORDED base: line1 is ORIGINAL — NOT the corrupt 'LINE1-upstream + line3-agent' mix a tip-rooted 3-way apply would graft");
    }

    [Fact]
    public async Task A_contribution_whose_recorded_base_disagrees_is_refused()
    {
        if (!await GitReadyAsync()) return;

        using var ctx = new IntegratorTestContext();
        var baseX = await ctx.SeedBaseAsync(new() { ["a.txt"] = "a", ["b.txt"] = "b" });

        var good = await ctx.MakeContributionAsync("agent-good", baseX, d => File.WriteAllText(Path.Combine(d, "a.txt"), "a-edited"));
        var stale = await ctx.MakeContributionAsync("agent-stale", baseX, d => File.WriteAllText(Path.Combine(d, "b.txt"), "b-edited"))
            with { BaseSha = "0000000000000000000000000000000000000000" };   // recorded a DIFFERENT base

        var result = await ctx.NewIntegrator().IntegrateAsync(ctx.Request(baseX, good, stale), CancellationToken.None);

        result.Status.ShouldBe(IntegrationStatus.Conflicted, "all-or-nothing: one base-mismatched contribution blocks the whole set");
        result.IntegratedBranch.ShouldBeNull();
        (await ctx.RemoteHasBranchAsync(ctx.IntegrationBranch)).ShouldBeFalse("nothing is pushed when the set cannot be cleanly integrated");
        ctx.Outcome(result, "agent-stale").Disposition.ShouldNotBe(ContributionDisposition.Applied);
        ctx.Outcome(result, "agent-stale").Reason.ShouldContain("base SHA mismatch");
    }

    // ── Crown jewel: a conflicting set fails SAFE ────────────────────────────────────

    [Fact]
    public async Task A_conflicting_set_fails_safe_no_branch_pushed_and_files_named()
    {
        if (!await GitReadyAsync()) return;

        using var ctx = new IntegratorTestContext();
        var baseSha = await ctx.SeedBaseAsync(new() { ["f.txt"] = "shared-line\n" });

        var a = await ctx.MakeContributionAsync("agent-a", baseSha, d => File.WriteAllText(Path.Combine(d, "f.txt"), "A-change\n"));
        var b = await ctx.MakeContributionAsync("agent-b", baseSha, d => File.WriteAllText(Path.Combine(d, "f.txt"), "B-change\n"));

        var result = await ctx.NewIntegrator().IntegrateAsync(ctx.Request(baseSha, a, b), CancellationToken.None);

        result.Status.ShouldBe(IntegrationStatus.Conflicted, "two edits to the same line cannot be auto-integrated");
        result.IntegratedBranch.ShouldBeNull();
        (await ctx.RemoteHasBranchAsync(ctx.IntegrationBranch)).ShouldBeFalse("a conflict NEVER produces a corrupt half-merged branch on the remote");
        ctx.Outcome(result, "agent-b").ConflictedFiles.ShouldContain("f.txt", "the conflicting file is named for human review");
    }

    // ── Crown jewel: idempotent re-run reproduces the SAME single branch ─────────────

    [Fact]
    public async Task An_idempotent_rerun_reproduces_the_same_branch_with_no_divergence()
    {
        if (!await GitReadyAsync()) return;

        using var ctx = new IntegratorTestContext();
        var baseSha = await ctx.SeedBaseAsync(new() { ["a.txt"] = "a" });
        var a = await ctx.MakeContributionAsync("agent-a", baseSha, d => File.WriteAllText(Path.Combine(d, "a.txt"), "a-edited"));

        var request = ctx.Request(baseSha, a);

        (await ctx.NewIntegrator().IntegrateAsync(request, CancellationToken.None)).Status.ShouldBe(IntegrationStatus.Clean);
        var second = await ctx.NewIntegrator().IntegrateAsync(request, CancellationToken.None);

        second.Status.ShouldBe(IntegrationStatus.Clean, "re-running the same frozen inputs reproduces the same clean integration (replay-safe)");
        (await ctx.CountRemoteBranchesAsync(ctx.IntegrationBranch)).ShouldBe(1, "the run-id-derived branch never forks — exactly one branch");
        (await ctx.RemoteFileAsync(ctx.IntegrationBranch, "a.txt")).Trim().ShouldBe("a-edited", "the integrated tree is identical");
    }

    // ── Crown jewel: a diverged remote branch is never clobbered ─────────────────────

    [Fact]
    public async Task A_diverged_remote_integration_branch_is_not_clobbered()
    {
        if (!await GitReadyAsync()) return;

        using var ctx = new IntegratorTestContext();
        var baseSha = await ctx.SeedBaseAsync(new() { ["a.txt"] = "a" });

        // A reviewer / concurrent rerun already advanced the integration branch with DIFFERENT content.
        await ctx.PreCreateRemoteBranchAsync(ctx.IntegrationBranch, baseSha, d => File.WriteAllText(Path.Combine(d, "reviewer.txt"), "human fixup"));

        var a = await ctx.MakeContributionAsync("agent-a", baseSha, d => File.WriteAllText(Path.Combine(d, "a.txt"), "a-edited"));

        var result = await ctx.NewIntegrator().IntegrateAsync(ctx.Request(baseSha, a), CancellationToken.None);

        result.Status.ShouldBe(IntegrationStatus.Conflicted, "a differing existing branch is foreign work — refuse, don't clobber");
        result.Reason.ShouldContain("advanced");
        (await ctx.RemoteFileAsync(ctx.IntegrationBranch, "reviewer.txt")).Trim().ShouldBe("human fixup", "the reviewer's branch content is untouched");
    }

    // ── Offloaded patch is resolved + integrated ─────────────────────────────────────

    [Fact]
    public async Task An_offloaded_patch_is_resolved_and_integrated()
    {
        if (!await GitReadyAsync()) return;

        using var ctx = new IntegratorTestContext();
        var baseSha = await ctx.SeedBaseAsync(new() { ["a.txt"] = "a" });

        var patch = await ctx.MakePatchAsync(baseSha, d => File.WriteAllText(Path.Combine(d, "a.txt"), "a-from-artifact"));
        var artifactId = Guid.NewGuid();
        var offloader = new FakeOffloader();
        offloader.Store(artifactId, ctx.TeamId, patch);

        // The contribution carries NO inline patch — only the artifact reference (the D2 large-diff offload path).
        var c = new BranchContribution { Label = "agent-a", BaseSha = baseSha, Patch = "", PatchArtifactId = artifactId };

        var result = await ctx.NewIntegrator(offloader).IntegrateAsync(ctx.Request(baseSha, c), CancellationToken.None);

        result.Status.ShouldBe(IntegrationStatus.Clean);
        (await ctx.RemoteFileAsync(ctx.IntegrationBranch, "a.txt")).Trim().ShouldBe("a-from-artifact", "the offloaded diff was resolved and applied");
        offloader.ResolvedTeams.ShouldAllBe(t => t == ctx.TeamId, "the integrator resolves every artifact under the request's team");
    }

    // ── Crown jewel: a cross-team artifact resolves empty and is loudly unintegrable ──

    [Fact]
    public async Task A_cross_team_artifact_resolves_empty_and_is_unintegrable_never_a_silent_noop()
    {
        if (!await GitReadyAsync()) return;

        using var ctx = new IntegratorTestContext();
        var baseSha = await ctx.SeedBaseAsync(new() { ["a.txt"] = "a" });

        var patch = await ctx.MakePatchAsync(baseSha, d => File.WriteAllText(Path.Combine(d, "a.txt"), "a-edited"));
        var artifactId = Guid.NewGuid();
        var offloader = new FakeOffloader();
        offloader.Store(artifactId, Guid.NewGuid(), patch);   // stored under a DIFFERENT team → the team-gate returns empty

        var c = new BranchContribution { Label = "agent-cross", BaseSha = baseSha, Patch = "", PatchArtifactId = artifactId, ProducedBranch = "codespace/agent/x" };

        var result = await ctx.NewIntegrator(offloader).IntegrateAsync(ctx.Request(baseSha, c), CancellationToken.None);

        result.Status.ShouldBe(IntegrationStatus.Conflicted, "an offloaded patch that resolves to nothing is NEVER silently treated as a no-op");
        (await ctx.RemoteHasBranchAsync(ctx.IntegrationBranch)).ShouldBeFalse("no cross-tenant patch ever reaches the clone or a pushed branch");
        ctx.Outcome(result, "agent-cross").Reason.ShouldContain("could not be resolved");
    }

    // ── Multi-repo set is refused ────────────────────────────────────────────────────

    [Fact]
    public async Task Contributions_spanning_multiple_repositories_are_refused()
    {
        if (!await GitReadyAsync()) return;

        using var ctx = new IntegratorTestContext();
        var baseSha = await ctx.SeedBaseAsync(new() { ["a.txt"] = "a", ["b.txt"] = "b" });

        var a = (await ctx.MakeContributionAsync("agent-a", baseSha, d => File.WriteAllText(Path.Combine(d, "a.txt"), "a-edited"))) with { SourceRepositoryId = Guid.NewGuid() };
        var b = (await ctx.MakeContributionAsync("agent-b", baseSha, d => File.WriteAllText(Path.Combine(d, "b.txt"), "b-edited"))) with { SourceRepositoryId = Guid.NewGuid() };

        var result = await ctx.NewIntegrator().IntegrateAsync(ctx.Request(baseSha, a, b), CancellationToken.None);

        result.Status.ShouldBe(IntegrationStatus.Conflicted);
        result.Reason.ShouldContain("multiple repositories");
        (await ctx.RemoteHasBranchAsync(ctx.IntegrationBranch)).ShouldBeFalse();
    }

    // ── Re-attached agent (no base, no branch) is loudly unintegrable ────────────────

    [Fact]
    public async Task A_reattached_contribution_with_no_base_and_no_branch_is_loudly_unintegrable()
    {
        if (!await GitReadyAsync()) return;

        using var ctx = new IntegratorTestContext();
        var baseSha = await ctx.SeedBaseAsync(new() { ["a.txt"] = "a" });

        var ok = await ctx.MakeContributionAsync("agent-ok", baseSha, d => File.WriteAllText(Path.Combine(d, "a.txt"), "a-edited"));
        var lost = new BranchContribution { Label = "agent-lost", BaseSha = null, Patch = "", ProducedBranch = null };   // crash-reattach: no surviving clone

        var result = await ctx.NewIntegrator().IntegrateAsync(ctx.Request(baseSha, ok, lost), CancellationToken.None);

        result.Status.ShouldBe(IntegrationStatus.Conflicted);
        ctx.Outcome(result, "agent-lost").Disposition.ShouldBe(ContributionDisposition.Unintegrable, "no patch + no branch → unrecoverable, never a silent skip");
        (await ctx.RemoteHasBranchAsync(ctx.IntegrationBranch)).ShouldBeFalse();
    }

    // ── An empty patch carrying a branch is refused, never a silent drop ─────────────

    [Fact]
    public async Task An_empty_patch_with_a_produced_branch_is_refused_not_silently_applied()
    {
        if (!await GitReadyAsync()) return;

        using var ctx = new IntegratorTestContext();
        var baseSha = await ctx.SeedBaseAsync(new() { ["a.txt"] = "a" });

        var real = await ctx.MakeContributionAsync("agent-real", baseSha, d => File.WriteAllText(Path.Combine(d, "a.txt"), "a-edited"));
        // The agent's work lives ONLY on its pushed branch — no patch was captured. The integrator is patch-based, so
        // it must REFUSE (Conflicted-with-fallback), never skip it as a no-op while reporting the set Clean.
        var branchOnly = new BranchContribution { Label = "agent-branch-only", BaseSha = baseSha, Patch = "", ProducedBranch = "codespace/agent/branch-only" };

        var result = await ctx.NewIntegrator().IntegrateAsync(ctx.Request(baseSha, real, branchOnly), CancellationToken.None);

        result.Status.ShouldBe(IntegrationStatus.Conflicted, "an un-patchable branch-only contribution cannot be silently dropped while the set reports Clean");
        ctx.Outcome(result, "agent-branch-only").Disposition.ShouldBe(ContributionDisposition.Conflicted, "preserved on its fallback branch for human review, NOT Applied");
        ctx.Outcome(result, "agent-branch-only").FallbackBranch.ShouldBe("codespace/agent/branch-only");
        (await ctx.RemoteHasBranchAsync(ctx.IntegrationBranch)).ShouldBeFalse();
    }

    // ── A truncated patch is refused, never applied ──────────────────────────────────

    [Fact]
    public async Task A_truncated_patch_is_refused()
    {
        if (!await GitReadyAsync()) return;

        using var ctx = new IntegratorTestContext();
        var baseSha = await ctx.SeedBaseAsync(new() { ["a.txt"] = "a" });

        var patch = await ctx.MakePatchAsync(baseSha, d => File.WriteAllText(Path.Combine(d, "a.txt"), "a-edited"));
        var truncated = new BranchContribution { Label = "agent-big", BaseSha = baseSha, Patch = patch + "\n... diff truncated (9999 chars; capped at 1000000) ...\n" };

        var result = await ctx.NewIntegrator().IntegrateAsync(ctx.Request(baseSha, truncated), CancellationToken.None);

        result.Status.ShouldBe(IntegrationStatus.Conflicted);
        ctx.Outcome(result, "agent-big").Reason.ShouldContain("truncated");
        (await ctx.RemoteHasBranchAsync(ctx.IntegrationBranch)).ShouldBeFalse();
    }

    // ── A malicious path-traversal patch is refused, nothing escapes the clone ───────

    [Fact]
    public async Task A_path_traversal_patch_is_refused_and_writes_nothing_outside_the_clone()
    {
        if (!await GitReadyAsync()) return;

        using var ctx = new IntegratorTestContext();
        var baseSha = await ctx.SeedBaseAsync(new() { ["a.txt"] = "a" });

        // A hand-crafted unified diff that tries to create a file OUTSIDE the repo via a ../ traversal path.
        var canary = Path.Combine(LocalGitWorkspaceProvider.WorkspacesRoot, "integrate-traversal-canary.txt");
        var malicious = "diff --git a/../integrate-traversal-canary.txt b/../integrate-traversal-canary.txt\n"
            + "new file mode 100644\n--- /dev/null\n+++ b/../integrate-traversal-canary.txt\n@@ -0,0 +1 @@\n+pwned\n";
        var c = new BranchContribution { Label = "agent-evil", BaseSha = baseSha, Patch = malicious };

        var result = await ctx.NewIntegrator().IntegrateAsync(ctx.Request(baseSha, c), CancellationToken.None);

        result.Status.ShouldBe(IntegrationStatus.Conflicted, "git apply rejects ../ traversal paths by default");
        File.Exists(canary).ShouldBeFalse("the traversal hunk wrote NOTHING outside the clone directory");
        (await ctx.RemoteHasBranchAsync(ctx.IntegrationBranch)).ShouldBeFalse();
    }

    // ── A clean set with NO write credential reports but does not push ───────────────

    [Fact]
    public async Task A_clean_set_with_no_write_credential_reports_but_pushes_nothing()
    {
        if (!await GitReadyAsync()) return;

        using var ctx = new IntegratorTestContext();
        var baseSha = await ctx.SeedBaseAsync(new() { ["a.txt"] = "a" });
        var a = await ctx.MakeContributionAsync("agent-a", baseSha, d => File.WriteAllText(Path.Combine(d, "a.txt"), "a-edited"));

        var result = await ctx.NewIntegrator().IntegrateAsync(ctx.Request(baseSha, a) with { Token = null }, CancellationToken.None);

        result.Status.ShouldBe(IntegrationStatus.Conflicted, "a clean integration with no write credential is reported, not silently lost");
        result.IntegratedBranch.ShouldBeNull();
        result.Reason.ShouldContain("write-capable credential");
        (await ctx.RemoteHasBranchAsync(ctx.IntegrationBranch)).ShouldBeFalse();
    }

    // ── The transient clone is removed on every path ─────────────────────────────────

    [Fact]
    public async Task The_integration_clone_is_removed_on_both_clean_and_conflict_paths()
    {
        if (!await GitReadyAsync()) return;

        using var ctx = new IntegratorTestContext();
        var baseSha = await ctx.SeedBaseAsync(new() { ["f.txt"] = "shared\n" });

        var before = CountIntegrationClones();

        var clean = await ctx.MakeContributionAsync("agent-a", baseSha, d => File.WriteAllText(Path.Combine(d, "f.txt"), "clean\n"));
        await ctx.NewIntegrator().IntegrateAsync(ctx.Request(baseSha, clean), CancellationToken.None);

        var conflictA = await ctx.MakeContributionAsync("agent-a", baseSha, d => File.WriteAllText(Path.Combine(d, "f.txt"), "x\n"));
        var conflictB = await ctx.MakeContributionAsync("agent-b", baseSha, d => File.WriteAllText(Path.Combine(d, "f.txt"), "y\n"));
        await ctx.NewIntegrator().IntegrateAsync(ctx.Request(baseSha, conflictA, conflictB), CancellationToken.None);

        CountIntegrationClones().ShouldBe(before, "no integrate-* clone lingers after a clean OR a conflict run");
    }

    // ── Empty request → Empty ────────────────────────────────────────────────────────

    [Fact]
    public async Task An_empty_request_is_empty()
    {
        if (!await GitReadyAsync()) return;

        using var ctx = new IntegratorTestContext();
        var baseSha = await ctx.SeedBaseAsync(new() { ["a.txt"] = "a" });

        var result = await ctx.NewIntegrator().IntegrateAsync(ctx.Request(baseSha), CancellationToken.None);

        result.Status.ShouldBe(IntegrationStatus.Empty);
        result.IntegratedBranch.ShouldBeNull();
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────────

    private static int CountIntegrationClones() =>
        Directory.Exists(LocalGitWorkspaceProvider.WorkspacesRoot)
            ? Directory.EnumerateDirectories(LocalGitWorkspaceProvider.WorkspacesRoot, "integrate-*").Count()
            : 0;

    private static async Task<bool> GitReadyAsync()
    {
        if (OperatingSystem.IsWindows()) return false;
        try { return (await new LocalProcessRunner().RunAsync(new SandboxSpec { Command = "git", Args = new[] { "--version" }, TimeoutSeconds = 10 }, CancellationToken.None)).Status == SandboxStatus.Success; }
        catch { return false; }
    }

    /// <summary>An IArtifactOffloader fake: inline text passes through; an artifact id resolves to its stored text ONLY when the calling team owns it (mirroring the real team-gate), else empty. Records every team it was asked to resolve under.</summary>
    private sealed class FakeOffloader : IArtifactOffloader
    {
        private readonly Dictionary<Guid, (Guid Team, string Text)> _store = new();
        public List<Guid> ResolvedTeams { get; } = new();

        public void Store(Guid artifactId, Guid team, string text) => _store[artifactId] = (team, text);

        public Task<string> ResolveAsync(Guid teamId, string? inline, Guid? artifactId, CancellationToken cancellationToken)
        {
            ResolvedTeams.Add(teamId);

            if (artifactId is null) return Task.FromResult(inline ?? "");

            return Task.FromResult(_store.TryGetValue(artifactId.Value, out var e) && e.Team == teamId ? e.Text : "");
        }

        public Task<OffloadedText> OffloadIfLargeAsync(Guid teamId, string? text, string contentType, CancellationToken cancellationToken) =>
            throw new NotSupportedException("the integrator never offloads");
    }

    private sealed class IntegratorTestContext : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "cs-integrate-" + Guid.NewGuid().ToString("N"));
        private readonly string _bare;

        public Guid TeamId { get; } = Guid.NewGuid();
        public string IntegrationBranch { get; } = "codespace/integration/" + Guid.NewGuid().ToString("N");

        public IntegratorTestContext()
        {
            Directory.CreateDirectory(_root);
            _bare = Path.Combine(_root, "remote.git");
        }

        private string RemoteUrl => new Uri(_bare).AbsoluteUri;

        public IBranchIntegrator NewIntegrator(IArtifactOffloader? offloader = null) =>
            new LocalGitBranchIntegrator(new SandboxRunnerRegistry(new ISandboxRunner[] { new LocalProcessRunner() }), offloader ?? new FakeOffloader(), NullLogger<LocalGitBranchIntegrator>.Instance);

        public IntegrationRequest Request(string baseSha, params BranchContribution[] contributions) => new()
        {
            TeamId = TeamId,
            RepositoryUrl = RemoteUrl,
            BaseRef = "main",
            BaseSha = baseSha,
            Token = "integration-token",
            TokenUsername = "x-access-token",
            IntegrationBranch = IntegrationBranch,
            Depth = 0,
            Contributions = contributions,
        };

        /// <summary>Seed the bare remote with one commit holding <paramref name="files"/>; returns the base commit SHA.</summary>
        public async Task<string> SeedBaseAsync(Dictionary<string, string> files)
        {
            await Git(_root, "init", "--bare", "-b", "main", _bare);

            var seed = Path.Combine(_root, "seed");
            Directory.CreateDirectory(seed);
            await Git(seed, "clone", _bare, seed);
            await ConfigAsync(seed);

            foreach (var (name, content) in files) await File.WriteAllTextAsync(Path.Combine(seed, name), content);

            await Git(seed, "add", "-A");
            await Git(seed, "commit", "-m", "seed");
            await Git(seed, "push", "origin", "main");

            return (await Git(seed, "rev-parse", "HEAD")).Trim();
        }

        /// <summary>Advance the remote default branch with a further commit (a non-overlapping upstream edit).</summary>
        public async Task AdvanceRemoteAsync(Action<string> mutate)
        {
            var work = Path.Combine(_root, "advance-" + Guid.NewGuid().ToString("N"));
            await Git(_root, "clone", _bare, work);
            await ConfigAsync(work);
            mutate(work);
            await Git(work, "add", "-A");
            await Git(work, "commit", "-m", "upstream advance");
            await Git(work, "push", "origin", "main");
        }

        /// <summary>Push a DIVERGENT commit to <paramref name="branch"/> on the remote (a reviewer fixup / concurrent rerun).</summary>
        public async Task PreCreateRemoteBranchAsync(string branch, string baseSha, Action<string> mutate)
        {
            var work = Path.Combine(_root, "diverge-" + Guid.NewGuid().ToString("N"));
            await Git(_root, "clone", _bare, work);
            await ConfigAsync(work);
            await Git(work, "checkout", "-b", branch, baseSha);
            mutate(work);
            await Git(work, "add", "-A");
            await Git(work, "commit", "-m", "reviewer fixup");
            await Git(work, "push", "origin", branch);
        }

        /// <summary>Produce a unified diff rooted at <paramref name="baseSha"/> the same way an agent does (git add -A + git diff --cached vs the base).</summary>
        public async Task<string> MakePatchAsync(string baseSha, Action<string> mutate)
        {
            var work = Path.Combine(_root, "patch-" + Guid.NewGuid().ToString("N"));
            await Git(_root, "clone", _bare, work);
            await ConfigAsync(work);
            await Git(work, "checkout", "--detach", baseSha);
            mutate(work);
            await Git(work, "add", "-A");
            var patch = await Git(work, "diff", "--cached", "--no-color", baseSha);
            Directory.Delete(work, recursive: true);
            return patch;
        }

        public async Task<BranchContribution> MakeContributionAsync(string label, string baseSha, Action<string> mutate) =>
            new() { Label = label, BaseSha = baseSha, Patch = await MakePatchAsync(baseSha, mutate) };

        public ContributionOutcome Outcome(IntegrationResult result, string label) =>
            result.Outcomes.Single(o => o.Label == label);

        public async Task<bool> RemoteHasBranchAsync(string branch) =>
            (await Git(_root, "--git-dir", _bare, "branch", "--list", branch)).Trim().Length > 0;

        public async Task<int> CountRemoteBranchesAsync(string branch) =>
            (await Git(_root, "--git-dir", _bare, "branch", "--list", branch)).Split('\n', StringSplitOptions.RemoveEmptyEntries).Length;

        public Task<string> RemoteFileAsync(string branch, string file) =>
            Git(_root, "--git-dir", _bare, "show", $"{branch}:{file}");

        private static async Task ConfigAsync(string dir)
        {
            await Git(dir, "config", "user.email", "test@codespace.dev");
            await Git(dir, "config", "user.name", "Test");
            await Git(dir, "config", "commit.gpgsign", "false");
        }

        private static async Task<string> Git(string workdir, params string[] args)
        {
            var result = await new LocalProcessRunner().RunAsync(
                new SandboxSpec { Command = "git", Args = args, WorkingDirectory = workdir, TimeoutSeconds = 60 }, CancellationToken.None);

            if (result.Status != SandboxStatus.Success)
                throw new InvalidOperationException($"git {string.Join(' ', args)} failed (exit {result.ExitCode}): {result.Stderr}");

            return result.Stdout;
        }

        public void Dispose()
        {
            try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
        }
    }
}
