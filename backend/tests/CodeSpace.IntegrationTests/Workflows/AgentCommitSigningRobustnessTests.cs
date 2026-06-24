using CodeSpace.Core.Services.Agents.Sandbox;
using CodeSpace.Core.Services.Agents.Sandbox.Runners;
using CodeSpace.Messages.Agents;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows;

/// <summary>
/// 🟡 MEDIUM-MIRROR (Rule 12.5) — pins that the automated agent commits (the workspace CAPTURE in
/// <c>LocalGitWorkspaceProvider</c> and the branch INTEGRATE in <c>LocalGitBranchIntegrator</c>) survive a host/global
/// <c>commit.gpgsign=true</c>. <see cref="LocalProcessRunner.EnvAllowlist"/> preserves <c>HOME</c>, so the agent's git
/// inherits the runner's <c>~/.gitconfig</c>: a runner image that defaults signing on makes a plain <c>git commit</c>
/// block on a signing key the unattended agent does not have. For the CAPTURE commit that loses the produced branch
/// (the failure is swallowed); for the INTEGRATE commit it degrades the integration to "Failed" → no integrated head →
/// a false acceptance miss on the live-brain whole-loop gate. A DEFENSIVE correctness fix: an internal automation
/// commit under a synthetic identity has no meaningful signature, so signing is always disabled inline.
///
/// <para>This drives the REAL <see cref="LocalProcessRunner"/> + real <c>git</c> with a real global
/// <c>commit.gpgsign=true</c> injected via <c>GIT_CONFIG_GLOBAL</c> (a per-run <see cref="SandboxSpec.Environment"/>
/// entry — wins over the inherited HOME, so NO process-global mutation), replaying the production commit ARG pattern
/// to show the inline <c>-c commit.gpgsign=false</c> is load-bearing (without it the commit fails; with it it
/// succeeds). The <see cref="Production_commit_sites_keep_the_inline_gpgsign_false_override_drift_detector"/> below is
/// the MANDATORY drift detector: it fails if either production commit drops the flag, so this mirror can never go
/// silently stale.</para>
/// </summary>
[Trait("Surface", "Engine")]
public sealed class AgentCommitSigningRobustnessTests
{
    /// <summary>The exact override the production commits carry (mirrored here; the drift detector pins production keeps it).</summary>
    private static readonly string[] GpgSignOff = { "-c", "commit.gpgsign=false" };

    [Fact]
    public async Task An_automated_commit_under_a_global_gpgsign_true_fails_without_the_inline_override_and_succeeds_with_it()
    {
        if (OperatingSystem.IsWindows()) return;             // the agent CLI + capture path is POSIX
        if (!await GitAvailableAsync()) return;              // real git is required

        using var repo = new SigningRepo();
        await repo.InitWithStagedChangeAsync();

        // WITHOUT the inline override: the global commit.gpgsign=true + a failing gpg program makes the commit FAIL —
        // the precondition a signing-on host would impose. Assert it fails for the RIGHT reason (signing), so the RED
        // case can never pass vacuously on an unrelated failure (e.g. a missing identity or "nothing to commit").
        var unsigned = await repo.CommitAsync(extraArgsBeforeCommit: Array.Empty<string>());
        unsigned.Status.ShouldNotBe(SandboxStatus.Success,
            $"a global commit.gpgsign=true with a failing gpg MUST break a plain commit (else the test proves nothing) — stdout/stderr: {unsigned.Stdout}{unsigned.Stderr}");
        $"{unsigned.Stdout}\n{unsigned.Stderr}".ToLowerInvariant().ShouldContain("gpg",
            customMessage: "the RED commit must fail specifically because of GPG SIGNING — otherwise the mirror is not pinning the gpgsign mechanism the production override targets");

        // WITH the inline override (the production arg pattern): the commit SUCCEEDS — signing is skipped for this one
        // automated commit, never inheriting the host requirement.
        var overridden = await repo.CommitAsync(extraArgsBeforeCommit: GpgSignOff);
        overridden.Status.ShouldBe(SandboxStatus.Success,
            $"`-c commit.gpgsign=false` must override the global signing requirement — stderr: {overridden.Stderr}");
    }

    [Fact]
    public void Production_commit_sites_keep_the_inline_gpgsign_false_override_drift_detector()
    {
        // Rule 12.5 — if either production commit drops the inline flag, the mirror above goes stale; fail HERE so the
        // regression is caught at the source, not by a silently-passing mirror.
        var capture = ReadRepoFile("backend/src/CodeSpace.Core/Services/Agents/Workspace/Providers/LocalGitWorkspaceProvider.cs");
        var integrate = ReadRepoFile("backend/src/CodeSpace.Core/Services/Agents/Workspace/Integrators/LocalGitBranchIntegrator.cs");

        capture.ShouldContain("\"commit.gpgsign=false\"", customMessage: "the agent CAPTURE commit must keep its inline gpgsign=false override (LocalGitWorkspaceProvider.CommitOrDetectEmptyAsync)");
        integrate.ShouldContain("\"commit.gpgsign=false\"", customMessage: "the branch INTEGRATE commit must keep its inline gpgsign=false override (LocalGitBranchIntegrator.CommitAsync)");
    }

    private static async Task<bool> GitAvailableAsync()
    {
        try { return (await new LocalProcessRunner().RunAsync(new SandboxSpec { Command = "git", Args = new[] { "--version" }, TimeoutSeconds = 10 }, CancellationToken.None)).Status == SandboxStatus.Success; }
        catch { return false; }
    }

    /// <summary>Read a repo-relative source file, locating the repo root by walking up from the test assembly until <c>backend/src</c> is found (present in CI's checkout + a local clone).</summary>
    private static string ReadRepoFile(string relativePath)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "backend", "src"))) dir = dir.Parent;

        dir.ShouldNotBeNull("could not locate the repo root (a directory containing backend/src) from the test assembly path");
        return File.ReadAllText(Path.Combine(dir!.FullName, relativePath));
    }

    /// <summary>A throwaway repo whose GLOBAL git config (via GIT_CONFIG_GLOBAL — isolated, no process mutation) forces <c>commit.gpgsign=true</c> with a gpg program that always fails, so only an inline override can commit.</summary>
    private sealed class SigningRepo : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "cs-gpgsign-" + Guid.NewGuid().ToString("N"));
        private readonly string _globalConfig;
        private readonly Dictionary<string, string> _env;

        public SigningRepo()
        {
            Directory.CreateDirectory(_root);
            _globalConfig = Path.Combine(_root, "global.gitconfig");
            File.WriteAllText(_globalConfig,
                "[commit]\n\tgpgsign = true\n[gpg]\n\tprogram = false\n[user]\n\tname = Host\n\temail = host@example.com\n");

            // GIT_CONFIG_GLOBAL points git at our config INSTEAD of HOME/.gitconfig — isolating the test from the real
            // host config and avoiding any process-global mutation. spec.Environment wins over the inherited HOME.
            _env = new Dictionary<string, string> { ["GIT_CONFIG_GLOBAL"] = _globalConfig, ["GIT_CONFIG_SYSTEM"] = "/dev/null" };
        }

        public async Task InitWithStagedChangeAsync()
        {
            await GitAsync("init", "-b", "main");
            await File.WriteAllTextAsync(Path.Combine(_root, "file.txt"), "change\n");
            await GitAsync("add", "-A");
        }

        /// <summary>Run the production-shaped commit (identity inline, optional gpgsign-off prefix) and return the raw result so the caller classifies success/failure.</summary>
        public Task<SandboxResult> CommitAsync(string[] extraArgsBeforeCommit)
        {
            var args = new List<string>(extraArgsBeforeCommit);
            args.AddRange(new[] { "-c", "user.name=CodeSpace", "-c", "user.email=agent@codespace.local", "commit", "-m", "Agent run test" });
            return Run(args.ToArray());
        }

        private Task<SandboxResult> GitAsync(params string[] args) => Run(args);

        private Task<SandboxResult> Run(string[] args) =>
            new LocalProcessRunner().RunAsync(new SandboxSpec { Command = "git", Args = args, WorkingDirectory = _root, Environment = _env, TimeoutSeconds = 30 }, CancellationToken.None);

        public void Dispose()
        {
            try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
        }
    }
}
