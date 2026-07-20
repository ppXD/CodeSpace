using CodeSpace.Core.Services.Agents.Sandbox;
using CodeSpace.Core.Services.Agents.Sandbox.Runners;
using CodeSpace.Core.Services.Agents.Workspace;
using CodeSpace.Core.Services.Agents.Workspace.Providers;
using CodeSpace.Messages.Agents;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows;

/// <summary>
/// End-to-end contract for <see cref="IWorkspacePushHandle.PushChangesAsync"/> against REAL git: clones a
/// BARE local repo (the "remote") through <see cref="LocalGitWorkspaceProvider"/> at the production default
/// shallow depth, writes changes, pushes, and inspects the bare remote's refs. The unconfined batch git
/// path is the same one the diff-capture uses (the handle's local runner, host network).
///
/// <para>Covers: happy-path push lands the branch + file on the remote; idempotent re-push (force overwrite,
/// no second branch); no-op when nothing changed (null, no branch); auth failure surfaces a redacted
/// WorkspaceException (the MANDATORY token-leak guard); an anonymous (no-token) handle short-circuits null
/// without invoking git push. Skips on Windows / when git is absent so a cross-host <c>dotnet test</c> stays
/// clean; every dir is GUID-suffixed under an IDisposable that best-effort cleans the bare remote + clone.</para>
/// </summary>
[Trait("Category", "Integration")]
public sealed class AgentWorkspacePushFlowTests
{
    [Fact]
    public async Task Happy_path_pushes_the_branch_with_the_file_to_the_remote()
    {
        if (OperatingSystem.IsWindows()) return;
        if (!await GitAvailableAsync()) return;

        using var ctx = new PushTestContext();
        await ctx.SeedBareRemoteWithOneCommitAsync();

        await using var handle = await ctx.CloneWithTokenAsync();

        await File.WriteAllTextAsync(Path.Combine(handle.Directory, "agent-change.txt"), "produced by the agent");

        var branch = await Push(handle).PushChangesAsync(ctx.BranchName, CancellationToken.None);

        branch.ShouldBe(ctx.BranchName);
        (await ctx.RemoteHasBranchAsync(ctx.BranchName)).ShouldBeTrue("the bare remote now carries the pushed branch");
        (await ctx.RemoteBranchContainsFileAsync(ctx.BranchName, "agent-change.txt")).ShouldBeTrue("the pushed branch tip contains the agent's file");

        // P3b-2 provider readback: arrival is an OBSERVED remote fact — the handle's confirmed sha IS the
        // remote's actual branch tip, re-read from the remote after the push, never a self-report.
        Push(handle).LastPushedCommitSha().ShouldBe(await ctx.RemoteTipAsync(ctx.BranchName));
    }

    [Fact]
    public async Task A_no_change_run_confirms_no_sha()
    {
        if (OperatingSystem.IsWindows()) return;
        if (!await GitAvailableAsync()) return;

        using var ctx = new PushTestContext();
        await ctx.SeedBareRemoteWithOneCommitAsync();

        await using var handle = await ctx.CloneWithTokenAsync();

        (await Push(handle).PushChangesAsync(ctx.BranchName, CancellationToken.None)).ShouldBeNull("nothing changed, nothing pushed");
        Push(handle).LastPushedCommitSha().ShouldBeNull("no push ⇒ no confirmed arrival — absence stays honest");
    }

    [Fact]
    public async Task Re_push_is_idempotent_and_force_overwrites_without_a_second_branch()
    {
        if (OperatingSystem.IsWindows()) return;
        if (!await GitAvailableAsync()) return;

        using var ctx = new PushTestContext();
        await ctx.SeedBareRemoteWithOneCommitAsync();

        await using var handle = await ctx.CloneWithTokenAsync();
        await File.WriteAllTextAsync(Path.Combine(handle.Directory, "agent-change.txt"), "v1");

        (await Push(handle).PushChangesAsync(ctx.BranchName, CancellationToken.None)).ShouldBe(ctx.BranchName);

        // Re-push the SAME clone state: --force re-checkout-b would conflict, so this proves the operation is
        // safe to repeat on a remote that already has the branch (the run-unique branch overwrites itself).
        await using var handle2 = await ctx.CloneWithTokenAsync();
        await File.WriteAllTextAsync(Path.Combine(handle2.Directory, "agent-change.txt"), "v2");

        (await Push(handle2).PushChangesAsync(ctx.BranchName, CancellationToken.None)).ShouldBe(ctx.BranchName);

        (await ctx.CountRemoteBranchesAsync(ctx.BranchName)).ShouldBe(1, "force-push overwrites — never a divergent / second branch");
        (await ctx.RemoteBranchContainsTextAsync(ctx.BranchName, "agent-change.txt", "v2")).ShouldBeTrue("the second push's content is the branch tip");
    }

    [Fact]
    public async Task No_changes_returns_null_and_the_remote_gets_no_new_branch()
    {
        if (OperatingSystem.IsWindows()) return;
        if (!await GitAvailableAsync()) return;

        using var ctx = new PushTestContext();
        await ctx.SeedBareRemoteWithOneCommitAsync();

        await using var handle = await ctx.CloneWithTokenAsync();   // change NOTHING

        var branch = await Push(handle).PushChangesAsync(ctx.BranchName, CancellationToken.None);

        branch.ShouldBeNull("nothing to commit → no push");
        (await ctx.RemoteHasBranchAsync(ctx.BranchName)).ShouldBeFalse("the remote gained no branch");
    }

    [Fact]
    public async Task A_harness_that_committed_its_own_work_is_still_pushed()
    {
        // The agent may COMMIT its change itself (clean working tree afterward) instead of leaving it staged.
        // The push must detect that the branch tip differs from the cloned base and push it — not see "nothing
        // new to commit" and silently produce no branch.
        if (OperatingSystem.IsWindows()) return;
        if (!await GitAvailableAsync()) return;

        using var ctx = new PushTestContext();
        await ctx.SeedBareRemoteWithOneCommitAsync();

        await using var handle = await ctx.CloneWithTokenAsync();
        await ctx.CommitInCloneAsync(handle.Directory, "agent-change.txt", "committed by the agent");

        var branch = await Push(handle).PushChangesAsync(ctx.BranchName, CancellationToken.None);

        branch.ShouldBe(ctx.BranchName, "a harness-committed change is still pushed");
        (await ctx.RemoteHasBranchAsync(ctx.BranchName)).ShouldBeTrue("the bare remote carries the committed work");
        (await ctx.RemoteBranchContainsFileAsync(ctx.BranchName, "agent-change.txt")).ShouldBeTrue();
    }

    [Fact]
    public async Task Auth_failure_surfaces_a_redacted_workspace_exception()
    {
        // MANDATORY token-leak guard: a push at an unreachable/garbage remote must throw a WorkspaceException
        // whose message has the token literal ABSENT and "***" present.
        if (OperatingSystem.IsWindows()) return;
        if (!await GitAvailableAsync()) return;

        using var ctx = new PushTestContext();
        await ctx.SeedBareRemoteWithOneCommitAsync();

        // Clone the real remote with a token (so the local commit succeeds), THEN destroy the bare remote so
        // the push fails ("does not appear to be a git repository") — the handle re-injects the token into the
        // failing push URL, so the surfaced WorkspaceException must redact it.
        await using var handle = await ctx.CloneWithTokenAsync();
        await File.WriteAllTextAsync(Path.Combine(handle.Directory, "agent-change.txt"), "x");
        ctx.DestroyBareRemote();

        var ex = await Should.ThrowAsync<WorkspaceException>(async () =>
            await Push(handle).PushChangesAsync(ctx.BranchName, CancellationToken.None));

        ex.Message.ShouldNotContain(PushTestContext.Token, Case.Insensitive, "the token literal must never leak into the surfaced error");
        ex.Message.ShouldContain("***", customMessage: "the token is replaced with the redaction marker");
    }

    [Fact]
    public async Task An_anonymous_handle_short_circuits_null_without_invoking_git_push()
    {
        if (OperatingSystem.IsWindows()) return;
        if (!await GitAvailableAsync()) return;

        using var ctx = new PushTestContext();
        await ctx.SeedBareRemoteWithOneCommitAsync();

        await using var handle = await ctx.CloneAnonymousAsync();   // Token == null
        await File.WriteAllTextAsync(Path.Combine(handle.Directory, "agent-change.txt"), "x");

        var branch = await Push(handle).PushChangesAsync(ctx.BranchName, CancellationToken.None);

        branch.ShouldBeNull("an anonymous clone carries no push credential → short-circuit null");
        (await ctx.RemoteHasBranchAsync(ctx.BranchName)).ShouldBeFalse("git push was never invoked — no branch appeared");
    }

    [Fact]
    public async Task Multi_repo_pushes_each_writable_repo_to_its_OWN_remote()
    {
        // CROWN JEWEL for multi-repo PR3: a two-repo workspace pushes EACH repo by alias to ITS OWN bare remote
        // under the same run-derived branch name — the change set. The thing a fake can't prove: web's branch lands
        // on web's remote and api's on api's, never crossed.
        if (OperatingSystem.IsWindows()) return;
        if (!await GitAvailableAsync()) return;

        using var ctx = new MultiRepoPushTestContext();
        await ctx.SeedBareRemotesAsync();

        await using var handle = await ctx.CloneBothWithTokensAsync();

        // Each handle carries the resolved clone ref as its BaseBranch — the PR target threaded into the change set.
        handle.Repositories.Single(r => r.Alias == "web").BaseBranch.ShouldBe("main", "the cloned ref surfaces as the per-repo base branch");
        handle.Repositories.Single(r => r.Alias == "api").BaseBranch.ShouldBe("main");

        var webDir = handle.Repositories.Single(r => r.Alias == "web").Directory;
        var apiDir = handle.Repositories.Single(r => r.Alias == "api").Directory;
        await File.WriteAllTextAsync(Path.Combine(webDir, "web-change.txt"), "web work");
        await File.WriteAllTextAsync(Path.Combine(apiDir, "api-change.txt"), "api work");

        var push = (IWorkspacePushHandle)handle;
        (await push.PushChangesAsync("web", ctx.BranchName, CancellationToken.None)).ShouldBe(ctx.BranchName);
        (await push.PushChangesAsync("api", ctx.BranchName, CancellationToken.None)).ShouldBe(ctx.BranchName);

        (await ctx.RemoteHasBranchAsync("web", ctx.BranchName)).ShouldBeTrue("web's branch is on web's remote");
        (await ctx.RemoteBranchContainsFileAsync("web", ctx.BranchName, "web-change.txt")).ShouldBeTrue();
        (await ctx.RemoteHasBranchAsync("api", ctx.BranchName)).ShouldBeTrue("api's branch is on api's remote");
        (await ctx.RemoteBranchContainsFileAsync("api", ctx.BranchName, "api-change.txt")).ShouldBeTrue();

        (await ctx.RemoteBranchContainsFileAsync("api", ctx.BranchName, "web-change.txt")).ShouldBeFalse("web's file never crossed onto api's remote");
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────

    private static IWorkspacePushHandle Push(IWorkspaceHandle handle) => (IWorkspacePushHandle)handle;

    private static async Task<bool> GitAvailableAsync()
    {
        try { return (await new LocalProcessRunner().RunAsync(new SandboxSpec { Command = "git", Args = new[] { "--version" }, TimeoutSeconds = 10 }, CancellationToken.None)).Status == SandboxStatus.Success; }
        catch { return false; }
    }

    private sealed class PushTestContext : IDisposable
    {
        public const string Token = "super-secret-push-token";

        private readonly string _root = Path.Combine(Path.GetTempPath(), "cs-push-" + Guid.NewGuid().ToString("N"));
        private readonly string _bareRemote;

        public string BranchName { get; } = "codespace/agent/" + Guid.NewGuid().ToString("N");

        public PushTestContext()
        {
            Directory.CreateDirectory(_root);
            _bareRemote = Path.Combine(_root, "remote.git");
        }

        private string RemoteUrl => new Uri(_bareRemote).AbsoluteUri;

        /// <summary>A bare repo is the "remote"; seed it via a throwaway working clone so it has a default branch + one commit.</summary>
        public async Task SeedBareRemoteWithOneCommitAsync()
        {
            await RunGitAsync(_root, "init", "--bare", "-b", "main", _bareRemote);

            var seed = Path.Combine(_root, "seed");
            Directory.CreateDirectory(seed);
            await RunGitAsync(seed, "clone", _bareRemote, seed);
            await RunGitAsync(seed, "config", "user.email", "test@codespace.dev");
            await RunGitAsync(seed, "config", "user.name", "Test");
            await RunGitAsync(seed, "config", "commit.gpgsign", "false");
            await File.WriteAllTextAsync(Path.Combine(seed, "README.md"), "base");
            await RunGitAsync(seed, "add", ".");
            await RunGitAsync(seed, "commit", "-m", "seed");
            await RunGitAsync(seed, "push", "origin", "main");
        }

        public Task<IWorkspaceHandle> CloneWithTokenAsync() =>
            // A file:// remote ignores the token; the point is that the handle CARRIES a token, so PushChangesAsync
            // takes the authenticated path (re-injecting it into the push argv) rather than short-circuiting.
            NewProvider().PrepareAsync(WorkspaceProvisionRequest.FromSingle(new WorkspaceRequest { RepositoryUrl = RemoteUrl, Token = Token, TokenUsername = "x-access-token" }), CancellationToken.None);

        public Task<IWorkspaceHandle> CloneAnonymousAsync() =>
            NewProvider().PrepareAsync(WorkspaceProvisionRequest.FromSingle(new WorkspaceRequest { RepositoryUrl = RemoteUrl }), CancellationToken.None);

        /// <summary>Delete the bare remote AFTER the clone so a subsequent push to it fails — the failing push URL embeds the token, exercising the redaction path.</summary>
        public void DestroyBareRemote() => Directory.Delete(_bareRemote, recursive: true);

        /// <summary>Simulate a harness that COMMITS its own work in the clone (clean tree afterward), so the push must detect committed changes via the base-SHA diff, not only freshly-staged ones.</summary>
        public async Task CommitInCloneAsync(string cloneDir, string file, string content)
        {
            await File.WriteAllTextAsync(Path.Combine(cloneDir, file), content);
            await RunGitAsync(cloneDir, "add", ".");
            await RunGitAsync(cloneDir, "-c", "user.email=agent@codespace.dev", "-c", "user.name=Agent", "-c", "commit.gpgsign=false", "commit", "-m", "agent commit");
        }

        public async Task<string> RemoteTipAsync(string branch) =>
            (await RunGitAsync(_root, "--git-dir", _bareRemote, "rev-parse", branch)).Trim();

        public async Task<bool> RemoteHasBranchAsync(string branch) =>
            (await RunGitAsync(_root, "--git-dir", _bareRemote, "branch", "--list", branch)).Trim().Length > 0;

        public async Task<int> CountRemoteBranchesAsync(string branch) =>
            (await RunGitAsync(_root, "--git-dir", _bareRemote, "branch", "--list", branch))
                .Split('\n', StringSplitOptions.RemoveEmptyEntries).Length;

        public async Task<bool> RemoteBranchContainsFileAsync(string branch, string file) =>
            (await RunGitAsync(_root, "--git-dir", _bareRemote, "ls-tree", "-r", "--name-only", branch)).Split('\n').Any(l => l.Trim() == file);

        public async Task<bool> RemoteBranchContainsTextAsync(string branch, string file, string text) =>
            (await RunGitAsync(_root, "--git-dir", _bareRemote, "show", $"{branch}:{file}")).Contains(text);

        private static LocalGitWorkspaceProvider NewProvider() =>
            new(new SandboxRunnerRegistry(new ISandboxRunner[] { new LocalProcessRunner() }), NullLogger<LocalGitWorkspaceProvider>.Instance);

        private static async Task<string> RunGitAsync(string workdir, params string[] args)
        {
            var result = await new LocalProcessRunner().RunAsync(
                new SandboxSpec { Command = "git", Args = args, WorkingDirectory = workdir, TimeoutSeconds = 60 }, CancellationToken.None);

            if (result.Status != SandboxStatus.Success)
                throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {result.Stderr}");

            return result.Stdout;
        }

        public void Dispose()
        {
            try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
        }
    }

    /// <summary>Two bare remotes ("web" + "api"), each seeded with one commit; clones BOTH into a single multi-repo workspace (both writable, web primary) carrying tokens so per-repo push takes the authenticated path. GUID-suffixed under one IDisposable root.</summary>
    private sealed class MultiRepoPushTestContext : IDisposable
    {
        private const string Token = "super-secret-push-token";

        private readonly string _root = Path.Combine(Path.GetTempPath(), "cs-push-multi-" + Guid.NewGuid().ToString("N"));
        private readonly Dictionary<string, string> _bareByAlias = new();

        public string BranchName { get; } = "codespace/agent/" + Guid.NewGuid().ToString("N");

        public MultiRepoPushTestContext() => Directory.CreateDirectory(_root);

        public async Task SeedBareRemotesAsync()
        {
            await SeedOneAsync("web");
            await SeedOneAsync("api");
        }

        private async Task SeedOneAsync(string alias)
        {
            var bare = Path.Combine(_root, $"{alias}.git");
            _bareByAlias[alias] = bare;
            await RunGitAsync(_root, "init", "--bare", "-b", "main", bare);

            var seed = Path.Combine(_root, $"seed-{alias}");
            Directory.CreateDirectory(seed);
            await RunGitAsync(seed, "clone", bare, seed);
            await RunGitAsync(seed, "config", "user.email", "test@codespace.dev");
            await RunGitAsync(seed, "config", "user.name", "Test");
            await RunGitAsync(seed, "config", "commit.gpgsign", "false");
            await File.WriteAllTextAsync(Path.Combine(seed, $"{alias}.txt"), "base");
            await RunGitAsync(seed, "add", ".");
            await RunGitAsync(seed, "commit", "-m", "seed");
            await RunGitAsync(seed, "push", "origin", "main");
        }

        public Task<IWorkspaceHandle> CloneBothWithTokensAsync() => NewProvider().PrepareAsync(new WorkspaceProvisionRequest
        {
            PrimaryAlias = "web",
            Repositories = new[]
            {
                new WorkspaceRepositoryProvision { Alias = "web", IsPrimary = true, Access = WorkspaceAccess.Write, CloneRequest = new WorkspaceRequest { RepositoryUrl = RemoteUrl("web"), Ref = "main", Token = Token, TokenUsername = "x-access-token" } },
                new WorkspaceRepositoryProvision { Alias = "api", Access = WorkspaceAccess.Write, CloneRequest = new WorkspaceRequest { RepositoryUrl = RemoteUrl("api"), Ref = "main", Token = Token, TokenUsername = "x-access-token" } },
            },
        }, CancellationToken.None);

        public async Task<bool> RemoteHasBranchAsync(string alias, string branch) =>
            (await RunGitAsync(_root, "--git-dir", _bareByAlias[alias], "branch", "--list", branch)).Trim().Length > 0;

        public async Task<bool> RemoteBranchContainsFileAsync(string alias, string branch, string file) =>
            (await RunGitAsync(_root, "--git-dir", _bareByAlias[alias], "ls-tree", "-r", "--name-only", branch)).Split('\n').Any(l => l.Trim() == file);

        private string RemoteUrl(string alias) => new Uri(_bareByAlias[alias]).AbsoluteUri;

        private static LocalGitWorkspaceProvider NewProvider() =>
            new(new SandboxRunnerRegistry(new ISandboxRunner[] { new LocalProcessRunner() }), NullLogger<LocalGitWorkspaceProvider>.Instance);

        private static async Task<string> RunGitAsync(string workdir, params string[] args)
        {
            var result = await new LocalProcessRunner().RunAsync(
                new SandboxSpec { Command = "git", Args = args, WorkingDirectory = workdir, TimeoutSeconds = 60 }, CancellationToken.None);

            if (result.Status != SandboxStatus.Success)
                throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {result.Stderr}");

            return result.Stdout;
        }

        public void Dispose()
        {
            try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
        }
    }
}
