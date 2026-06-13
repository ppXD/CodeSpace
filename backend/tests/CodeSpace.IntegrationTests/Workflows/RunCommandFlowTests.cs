using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents.Commands;
using CodeSpace.Core.Services.Agents.Sandbox.Runners;
using CodeSpace.Core.Services.Agents.Workspace;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Enums;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows;

/// <summary>
/// End-to-end contract for <see cref="IRunCommandService"/> against real Postgres + the real
/// <see cref="LocalProcessRunner"/> + <see cref="CodeSpace.Core.Services.Agents.Workspace.Providers.LocalGitWorkspaceProvider"/>:
/// it clones a REAL local git repo into a fresh workspace, runs a REAL command in it, captures the result,
/// and cleans up — the meaty tier that proves the orchestration (resolve runner+workspace by kind → clone →
/// run → dispose) actually works, not just that the node maps a stub.
///
/// <para>Covers: repo-scoped run reads the cloned file; ephemeral run (no repo) runs with no checkout; a
/// non-zero exit is a normal <see cref="SandboxStatus.Failed"/> result; a bad clone URL throws
/// <see cref="WorkspaceException"/>; a blank command throws. Skips on Windows / when git is absent so a
/// cross-host <c>dotnet test</c> stays clean.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class RunCommandFlowTests
{
    private readonly PostgresFixture _fixture;

    public RunCommandFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task Repo_scoped_run_clones_the_repo_and_runs_in_it()
    {
        if (OperatingSystem.IsWindows()) return;
        if (!await GitAvailableAsync()) return;

        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        using var origin = new TempDir();
        await SeedLocalRepoAsync(origin.Path, "README.md", "hello-from-repo");
        var repoId = await SeedRepositoryAsync(teamId, new Uri(origin.Path).AbsoluteUri, "main");

        // `cat README.md` only sees the file if the service cloned the repo AND ran with the clone as cwd.
        var result = await RunAsync(new RunCommandRequest { RepositoryId = repoId, TeamId = teamId, Command = "cat", Args = new[] { "README.md" } });

        result.Status.ShouldBe(SandboxStatus.Success);
        result.ExitCode.ShouldBe(0);
        result.Stdout.ShouldContain("hello-from-repo", customMessage: "the command ran inside the cloned workspace and read the repo file");
    }

    [Fact]
    public async Task Ephemeral_run_with_no_repository_runs_with_no_checkout()
    {
        if (OperatingSystem.IsWindows()) return;

        var result = await RunAsync(new RunCommandRequest { Command = "true" });

        result.Status.ShouldBe(SandboxStatus.Success);
        result.ExitCode.ShouldBe(0);
    }

    [Fact]
    public async Task A_non_zero_exit_is_a_normal_failed_result_not_an_exception()
    {
        if (OperatingSystem.IsWindows()) return;

        var result = await RunAsync(new RunCommandRequest { Command = "false" });

        result.Status.ShouldBe(SandboxStatus.Failed, "a non-zero exit is a normal SandboxResult, never an exception");
        result.ExitCode.ShouldNotBe(0);
    }

    [Fact]
    public async Task A_bad_clone_url_throws_a_workspace_exception()
    {
        if (OperatingSystem.IsWindows()) return;
        if (!await GitAvailableAsync()) return;

        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var missing = new Uri(Path.Combine(Path.GetTempPath(), "cs-missing-" + Guid.NewGuid().ToString("N"))).AbsoluteUri;
        var repoId = await SeedRepositoryAsync(teamId, missing, "main");

        await Should.ThrowAsync<WorkspaceException>(() => RunAsync(new RunCommandRequest { RepositoryId = repoId, TeamId = teamId, Command = "true" }));
    }

    [Fact]
    public async Task A_repository_in_another_team_is_refused_fail_closed()
    {
        if (OperatingSystem.IsWindows()) return;

        var (ownerTeam, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var (otherTeam, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        using var origin = new TempDir();
        await SeedLocalRepoAsync(origin.Path, "README.md", "secret-of-owner-team");
        var repoId = await SeedRepositoryAsync(ownerTeam, new Uri(origin.Path).AbsoluteUri, "main");

        // The repo belongs to ownerTeam; a run bound to otherTeam must NOT be able to clone it — the tenant
        // filter resolves nothing and the service refuses (no clone, no command, no cross-tenant read).
        var ex = await Should.ThrowAsync<WorkspaceException>(() => RunAsync(new RunCommandRequest { RepositoryId = repoId, TeamId = otherTeam, Command = "cat", Args = new[] { "README.md" } }));
        ex.Message.ShouldContain("not found", customMessage: "a cross-team repo is indistinguishable from a missing one (no existence leak)");
    }

    [Fact]
    public async Task A_repo_scoped_run_without_a_team_context_is_refused()
    {
        if (OperatingSystem.IsWindows()) return;

        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        using var origin = new TempDir();
        await SeedLocalRepoAsync(origin.Path, "README.md", "x");
        var repoId = await SeedRepositoryAsync(teamId, new Uri(origin.Path).AbsoluteUri, "main");

        // No TeamId on a repo-scoped request → fail-closed (e.g. a synthetic agent-tool context with no scope).
        var ex = await Should.ThrowAsync<WorkspaceException>(() => RunAsync(new RunCommandRequest { RepositoryId = repoId, Command = "true" }));
        ex.Message.ShouldContain("team context");
    }

    [Fact]
    public async Task A_blank_command_is_rejected()
    {
        var ex = await Should.ThrowAsync<InvalidOperationException>(() => RunAsync(new RunCommandRequest { Command = "   " }));
        ex.Message.ShouldContain("command");
    }

    // ── Helpers (mirror AgentRunExecutorTests' temp-repo + seed pattern) ────────

    private async Task<SandboxResult> RunAsync(RunCommandRequest request)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<IRunCommandService>().RunAsync(request, CancellationToken.None);
    }

    private async Task<Guid> SeedRepositoryAsync(Guid teamId, string cloneUrlHttps, string defaultBranch)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var instanceId = Guid.NewGuid();
        db.ProviderInstance.Add(new ProviderInstance { Id = instanceId, TeamId = teamId, Provider = ProviderKind.Git, DisplayName = "local", BaseUrl = "https://local" });

        var repoId = Guid.NewGuid();
        db.Repository.Add(new Repository
        {
            Id = repoId, TeamId = teamId, ProviderInstanceId = instanceId, CredentialId = null,
            ExternalId = repoId.ToString(), NamespacePath = "org", Name = "repo", FullPath = "org/repo",
            DefaultBranch = defaultBranch, CloneUrlHttps = cloneUrlHttps, WebUrl = "https://local/org/repo",
        });

        await db.SaveChangesAsync();
        return repoId;
    }

    private static async Task<bool> GitAvailableAsync()
    {
        try { return (await new LocalProcessRunner().RunAsync(new SandboxSpec { Command = "git", Args = new[] { "--version" }, TimeoutSeconds = 10 }, CancellationToken.None)).Status == SandboxStatus.Success; }
        catch { return false; }
    }

    private static async Task SeedLocalRepoAsync(string dir, string file, string content)
    {
        await RunGitInAsync(dir, "init", "-b", "main");
        await RunGitInAsync(dir, "config", "user.email", "test@codespace.dev");
        await RunGitInAsync(dir, "config", "user.name", "Test");
        await RunGitInAsync(dir, "config", "commit.gpgsign", "false");
        await File.WriteAllTextAsync(Path.Combine(dir, file), content);
        await RunGitInAsync(dir, "add", ".");
        await RunGitInAsync(dir, "commit", "-m", "seed");
    }

    private static async Task RunGitInAsync(string workdir, params string[] args)
    {
        var result = await new LocalProcessRunner().RunAsync(
            new SandboxSpec { Command = "git", Args = args, WorkingDirectory = workdir, TimeoutSeconds = 60 }, CancellationToken.None);
        if (result.Status != SandboxStatus.Success) throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {result.Stderr}");
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "cs-runcmd-origin-" + Guid.NewGuid().ToString("N"));
        public TempDir() => Directory.CreateDirectory(Path);
        public void Dispose() { try { Directory.Delete(Path, recursive: true); } catch { /* best-effort */ } }
    }
}
