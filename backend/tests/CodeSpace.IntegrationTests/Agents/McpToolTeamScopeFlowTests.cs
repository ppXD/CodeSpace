using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents.Mcp;
using CodeSpace.Core.Services.Agents.Sandbox.Runners;
using CodeSpace.Core.Services.Agents.Tools;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Enums;
using Shouldly;

namespace CodeSpace.IntegrationTests.Agents;

/// <summary>
/// Proves the run's team travels the full per-call path that the unit tests can only mock in halves:
/// <c>McpRequestHandler</c> (bound to a team + autonomy) → <c>AgentToolCall.TeamId</c> → <c>NodeAgentTool</c>
/// stamps <c>sys.team_id</c> → the real <c>agent.run_command</c> node resolves the repo within that tenant —
/// over the REAL DI registry + real Postgres + real git clone.
///
/// <para>Trimmed to the NET-NEW handler surface (RunCommandFlowTests already covers RunCommandService-level
/// tenancy directly). Covers: happy team-A clone, cross-team fail-closed, null-team fail-closed, and one gate
/// denial over the real registry. Skips on Windows / when git is absent so a cross-host <c>dotnet test</c>
/// stays clean.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class McpToolTeamScopeFlowTests
{
    private readonly PostgresFixture _fixture;

    public McpToolTeamScopeFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task Run_command_through_a_handler_bound_to_team_A_resolves_team_As_repo()
    {
        if (OperatingSystem.IsWindows()) return;
        if (!await GitAvailableAsync()) return;

        var (teamA, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        using var origin = new TempDir();
        await SeedLocalRepoAsync(origin.Path, "README.md", "hello-from-team-a");
        var repoId = await SeedRepositoryAsync(teamA, new Uri(origin.Path).AbsoluteUri, "main");

        using var scope = _fixture.BeginScope();
        var handler = new McpRequestHandler(scope.Resolve<IAgentToolRegistry>(), AgentAutonomyLevel.Unleashed, teamA);

        // `cat README.md` only reads the file if the handler's team reached the node, the node resolved team A's
        // repo, cloned it, and ran with the clone as cwd. Proves teamId travels handler → call → Sys → node.
        var result = await CallToolAsync(handler, "agent.run_command", new { repositoryId = repoId.ToString(), command = "cat", args = new[] { "README.md" } });

        result.GetProperty("isError").GetBoolean().ShouldBeFalse(customMessage: "the team-A repo must resolve and the command run inside its clone");
        Text(result).ShouldContain("hello-from-team-a", customMessage: "the command read the team-A repo file from the cloned workspace");
    }

    [Fact]
    public async Task A_handler_bound_to_team_A_naming_team_Bs_repo_fails_closed_without_leaking_existence()
    {
        if (OperatingSystem.IsWindows()) return;
        if (!await GitAvailableAsync()) return;

        var (teamA, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var (teamB, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        using var origin = new TempDir();
        await SeedLocalRepoAsync(origin.Path, "README.md", "secret-of-team-b");
        var repoId = await SeedRepositoryAsync(teamB, new Uri(origin.Path).AbsoluteUri, "main");

        using var scope = _fixture.BeginScope();
        var handler = new McpRequestHandler(scope.Resolve<IAgentToolRegistry>(), AgentAutonomyLevel.Unleashed, teamA);

        // The repo belongs to team B; a handler bound to team A names it → the tenant filter resolves nothing.
        var result = await CallToolAsync(handler, "agent.run_command", new { repositoryId = repoId.ToString(), command = "cat", args = new[] { "README.md" } });

        result.GetProperty("isError").GetBoolean().ShouldBeTrue(customMessage: "a cross-team repo id must fail closed, never clone");
        Text(result).ShouldContain("not found", customMessage: "a cross-team repo is indistinguishable from a missing one (no existence leak)");
        Text(result).ShouldNotContain("secret-of-team-b");
    }

    [Fact]
    public async Task A_repo_touching_tool_through_a_handler_with_no_team_fails_closed()
    {
        if (OperatingSystem.IsWindows()) return;
        if (!await GitAvailableAsync()) return;

        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        using var origin = new TempDir();
        await SeedLocalRepoAsync(origin.Path, "README.md", "x");
        var repoId = await SeedRepositoryAsync(teamId, new Uri(origin.Path).AbsoluteUri, "main");

        using var scope = _fixture.BeginScope();
        var handler = new McpRequestHandler(scope.Resolve<IAgentToolRegistry>(), AgentAutonomyLevel.Unleashed);   // no teamId → null

        // No team on the handler → no sys.team_id → the node can't resolve a repo (today's fail-closed default).
        var result = await CallToolAsync(handler, "agent.run_command", new { repositoryId = repoId.ToString(), command = "true" });

        result.GetProperty("isError").GetBoolean().ShouldBeTrue();
        Text(result).ShouldContain("team context", customMessage: "a repo-scoped tool with no team context must be refused");
    }

    [Fact]
    public async Task Run_command_at_Confined_is_denied_before_execution_over_the_real_registry()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);

        using var scope = _fixture.BeginScope();
        var handler = new McpRequestHandler(scope.Resolve<IAgentToolRegistry>(), AgentAutonomyLevel.Confined, teamId);

        // agent.run_command is side-effecting → destructive → gated. At Confined the autonomy gate denies it
        // BEFORE the tool runs, so no clone is even attempted (no git/skip-guard needed).
        var result = await CallToolAsync(handler, "agent.run_command", new { command = "true" });

        result.GetProperty("isError").GetBoolean().ShouldBeTrue();
        Text(result).ShouldContain("not permitted", customMessage: "a destructive tool at Confined is denied before execution");
    }

    // ── Helpers (mirror RunCommandFlowTests' temp-repo + seed pattern) ──────────

    private static async Task<JsonElement> CallToolAsync(McpRequestHandler handler, string name, object arguments)
    {
        var request = JsonSerializer.SerializeToElement(new
        {
            jsonrpc = "2.0",
            id = 1,
            method = "tools/call",
            @params = new { name, arguments },
        });

        var response = await handler.HandleAsync(request, CancellationToken.None);
        return response!.Value.GetProperty("result");
    }

    private static string Text(JsonElement toolResult) => toolResult.GetProperty("content")[0].GetProperty("text").GetString() ?? "";

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
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "cs-mcp-origin-" + Guid.NewGuid().ToString("N"));
        public TempDir() => Directory.CreateDirectory(Path);
        public void Dispose() { try { Directory.Delete(Path, recursive: true); } catch { /* best-effort */ } }
    }
}
