using Autofac;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Agents.Sandbox;
using CodeSpace.Core.Services.Agents.Sandbox.Runners;
using CodeSpace.Core.Services.Agents.Workspace;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.Messages.Agents;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace CodeSpace.IntegrationTests.Agents;

/// <summary>
/// The local closed loop for URL pack import via REAL git (no network): create a local git repo with agents +
/// skills, clone it through <see cref="PackCloneFetcher"/> (the real "local" sandbox runner runs git clone), walk
/// the checkout with the real <see cref="IPackSourceWalker"/>, and prove DISPOSAL deletes the transient clone — the
/// disk-hygiene guarantee that transient clones never accumulate into an out-of-disk. Also proves the egress guard
/// refuses a non-allowlisted host before any clone. The gated real-github clone is a follow-up E2E.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class PackCloneFetcherFlowTests
{
    private readonly PostgresFixture _fixture;

    public PackCloneFetcherFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task Clones_a_repo_walks_it_and_disposal_deletes_the_clone()
    {
        if (OperatingSystem.IsWindows()) return;   // git invoked via /bin-style tooling on the local runner

        using var scope = _fixture.BeginScope();
        var runners = scope.Resolve<ISandboxRunnerRegistry>();
        if (!await GitReadyAsync(runners)) return;   // skip on a runner without git rather than fail spuriously

        var src = await CreateSourceRepoAsync(runners);
        try
        {
            var fetcher = new PackCloneFetcher(new AllowAll(), runners, NullLogger<PackCloneFetcher>.Instance);

            string clonedDir;
            using (var checkout = await fetcher.FetchAsync(src, reference: null, CancellationToken.None))
            {
                clonedDir = checkout.Directory;
                Directory.Exists(clonedDir).ShouldBeTrue();
                File.Exists(Path.Combine(clonedDir, "agents", "reviewer.md")).ShouldBeTrue("the clone carries the source files");

                // The closed loop: clone → discover, through the real walker.
                var pack = await scope.Resolve<IPackSourceWalker>().WalkAsync(clonedDir, CancellationToken.None);
                pack.Agents.Select(a => a.SourcePath).ShouldContain("agents/reviewer.md");
                pack.Skills.Select(s => s.SourcePath).ShouldContain("skills/tdd/SKILL.md");
            }

            // Disposal removed the transient clone — the happy-path disk-hygiene guarantee (no accumulation → no OOM).
            Directory.Exists(clonedDir).ShouldBeFalse("disposing the checkout deletes the transient clone");
        }
        finally
        {
            try { Directory.Delete(src, recursive: true); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public async Task A_non_allowlisted_host_is_refused_before_any_clone()
    {
        using var scope = _fixture.BeginScope();
        var runners = scope.Resolve<ISandboxRunnerRegistry>();
        var fetcher = new PackCloneFetcher(new PackHostAllowlist(rawAllowedHostsOverride: null), runners, NullLogger<PackCloneFetcher>.Instance);

        await Should.ThrowAsync<PackImportException>(() => fetcher.FetchAsync("https://internal.corp/secret-pack", null, CancellationToken.None));
    }

    [Fact]
    public void Is_DI_bound_as_the_fetcher_and_as_a_workspace_janitor()
    {
        using var scope = _fixture.BeginScope();

        // The fetcher resolves to the real impl…
        scope.Resolve<IPackSourceFetcher>().ShouldBeOfType<PackCloneFetcher>();

        // …and is collected into the janitor SET the recurring sweep fans over, so orphaned clones are actually
        // reclaimed (the crash-safety backstop is wired, not just implemented).
        scope.Resolve<IEnumerable<IWorkspaceJanitor>>().ShouldContain(j => j.Kind == "pack-source");
    }

    [Fact]
    public async Task A_clone_failure_throws_and_leaves_no_partial_clone_behind()
    {
        if (OperatingSystem.IsWindows()) return;

        using var scope = _fixture.BeginScope();
        var runners = scope.Resolve<ISandboxRunnerRegistry>();
        if (!await GitReadyAsync(runners)) return;

        var before = Snapshot();

        // AllowAll lets the (local) path through the egress guard; git then fails because the path is no repo.
        var fetcher = new PackCloneFetcher(new AllowAll(), runners, NullLogger<PackCloneFetcher>.Instance);
        var nonexistent = Path.Combine(Path.GetTempPath(), "cs-no-repo-" + Guid.NewGuid().ToString("N"));

        await Should.ThrowAsync<PackImportException>(() => fetcher.FetchAsync(nonexistent, null, CancellationToken.None));

        // The partial clone dir was reclaimed before the throw — a failed clone never leaks (disk hygiene).
        Snapshot().Except(before).ShouldBeEmpty("a failed clone must leave no directory behind under PackClonesRoot");
    }

    [Fact]
    public async Task A_branch_reference_is_honored()
    {
        if (OperatingSystem.IsWindows()) return;

        using var scope = _fixture.BeginScope();
        var runners = scope.Resolve<ISandboxRunnerRegistry>();
        if (!await GitReadyAsync(runners)) return;

        var src = await CreateSourceRepoAsync(runners);
        try
        {
            var fetcher = new PackCloneFetcher(new AllowAll(), runners, NullLogger<PackCloneFetcher>.Instance);

            using var checkout = await fetcher.FetchAsync(src, reference: "feature", CancellationToken.None);

            File.Exists(Path.Combine(checkout.Directory, "feature-only.md"))
                .ShouldBeTrue("the --branch ref was honored: the feature-only file (absent on the default branch) is present");
        }
        finally
        {
            try { Directory.Delete(src, recursive: true); } catch { /* best-effort */ }
        }
    }

    private static HashSet<string> Snapshot() =>
        Directory.Exists(PackCloneFetcher.PackClonesRoot)
            ? Directory.GetDirectories(PackCloneFetcher.PackClonesRoot).ToHashSet()
            : new HashSet<string>();

    private sealed class AllowAll : IPackHostAllowlist
    {
        public bool IsAllowed(string url) => true;
        public void EnsureAllowed(string url) { }
    }

    private static async Task<bool> GitReadyAsync(ISandboxRunnerRegistry runners)
    {
        try
        {
            var r = await runners.Resolve(LocalProcessRunner.LocalKind).RunAsync(new SandboxSpec { Command = "git", Args = new[] { "--version" }, TimeoutSeconds = 15 }, default);
            return r.Status == SandboxStatus.Success;
        }
        catch { return false; }
    }

    private async Task<string> CreateSourceRepoAsync(ISandboxRunnerRegistry runners)
    {
        var src = Path.Combine(Path.GetTempPath(), "cs-packsrc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(src);

        await RunGitAsync(runners, new[] { "init", "-b", "main", src });   // deterministic default branch name
        WriteFile(src, "agents/reviewer.md", "---\nname: reviewer\ndescription: Use to review PRs.\n---\nYou are a reviewer.");
        WriteFile(src, "skills/tdd/SKILL.md", "---\nname: tdd\ndescription: Use when implementing.\n---\n# TDD\n\nWrite the test first.");
        await CommitAllAsync(runners, src, "init");

        // A feature branch carrying a file ABSENT on main, so a --branch fetch is distinguishable from the default.
        await RunGitAsync(runners, new[] { "-C", src, "checkout", "-b", "feature" });
        WriteFile(src, "feature-only.md", "---\nname: feature-extra\ndescription: only on the feature branch.\n---\nbody");
        await CommitAllAsync(runners, src, "feature");
        await RunGitAsync(runners, new[] { "-C", src, "checkout", "main" });   // leave HEAD on the default branch

        return src;
    }

    private static Task CommitAllAsync(ISandboxRunnerRegistry runners, string src, string message) =>
        RunGitChainAsync(runners,
            new[] { "-C", src, "-c", "user.email=t@codespace.test", "-c", "user.name=Test", "add", "-A" },
            new[] { "-C", src, "-c", "user.email=t@codespace.test", "-c", "user.name=Test", "commit", "-m", message });

    private static async Task RunGitChainAsync(ISandboxRunnerRegistry runners, params string[][] argsList)
    {
        foreach (var args in argsList) await RunGitAsync(runners, args);
    }

    private static async Task RunGitAsync(ISandboxRunnerRegistry runners, string[] args)
    {
        var r = await runners.Resolve(LocalProcessRunner.LocalKind).RunAsync(new SandboxSpec { Command = "git", Args = args, TimeoutSeconds = 30 }, default);
        r.Status.ShouldBe(SandboxStatus.Success, $"git {string.Join(' ', args)} failed (exit {r.ExitCode}): {r.Stderr}");
    }

    private static void WriteFile(string root, string relPath, string content)
    {
        var full = Path.Combine(root, relPath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }
}
