using System.Text.Json;
using Autofac;
using CodeSpace.Core.Services.Agents.Tools;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.Messages.Agents;
using Shouldly;

namespace CodeSpace.IntegrationTests.Agents;

/// <summary>
/// Proves the agent-tool registry builds from the REAL DI graph over the real node set — the wiring unit tests
/// (which construct it from a hand-list) can't exercise: that <c>IEnumerable&lt;INodeRuntime&gt;</c> resolves and
/// the eligible builtin nodes actually project onto the fabric.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class AgentToolRegistryFlowTests
{
    private readonly PostgresFixture _fixture;

    public AgentToolRegistryFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public void The_real_container_projects_eligible_nodes_onto_the_tool_fabric()
    {
        using var scope = _fixture.BeginScope();
        var registry = scope.Resolve<IAgentToolRegistry>();

        var runCommand = registry.Resolve("agent.run_command");
        runCommand.ShouldNotBeNull("the eligible agent.run_command node must project onto the tool fabric via DI");
        runCommand!.IsDestructive.ShouldBeTrue("running a command is side-effecting → a destructive, gated tool");

        registry.All.ShouldAllBe(t => !string.IsNullOrWhiteSpace(t.Kind));
    }

    [Theory]
    [InlineData("git.fetch_pr_diff")]
    [InlineData("git.fetch_pr_checks")]
    [InlineData("git.list_prs")]
    public void Read_only_git_nodes_project_as_non_destructive_tools(string kind)
    {
        using var scope = _fixture.BeginScope();
        var registry = scope.Resolve<IAgentToolRegistry>();

        var tool = registry.Resolve(kind);

        tool.ShouldNotBeNull($"the eligible read-only {kind} node must project onto the tool fabric via DI");
        tool!.IsReadOnly.ShouldBeTrue($"{kind} only reads provider data → a read-only tool");
        tool.IsDestructive.ShouldBeFalse($"{kind} has no side effects → not a destructive, gated tool");
    }

    [Theory]
    [InlineData("git.open_pr")]
    [InlineData("git.post_pr_comment")]
    [InlineData("git.pr_review")]
    public void Reversible_git_write_nodes_project_as_destructive_gated_tools(string kind)
    {
        using var scope = _fixture.BeginScope();
        var tool = scope.Resolve<IAgentToolRegistry>().Resolve(kind);

        tool.ShouldNotBeNull($"the eligible git-write {kind} node must project onto the tool fabric via DI");
        tool!.IsReadOnly.ShouldBeFalse($"{kind} mutates provider state → not read-only");
        tool.IsDestructive.ShouldBeTrue($"{kind} is side-effecting → a destructive tool");
        tool.RequiresApproval.ShouldBeTrue($"{kind} is destructive → approval-gated through AgentToolGate, exactly like agent.run_command");
    }

    [Fact]
    public void Git_merge_pr_is_NOT_projected_by_the_real_container()
    {
        // PIN over the REAL DI graph: git.merge_pr (irreversible) is registered as a node but DELIBERATELY left
        // off the agent-tool surface. A future accidental flip of its IsAgentToolEligible would start projecting
        // it here → this fails. It ships last behind a per-tool force-approval policy.
        using var scope = _fixture.BeginScope();

        scope.Resolve<IAgentToolRegistry>().Resolve("git.merge_pr")
            .ShouldBeNull("git.merge_pr stays off the agent-tool surface until a per-tool force-approval policy ships");
    }

    // Forward-looking guard: every currently-eligible repo-resolving tool MUST refuse a repositoryId when the
    // call carries no team (no sys.team_id). If a future eligible node forgets the NodeScopeReader.TryReadTeamId
    // check, this fails — catching a silently-reintroduced cross-tenant hole. The eligible repo-resolving builtins
    // today: agent.run_command + the three git read tools (fetch_pr_diff / fetch_pr_checks / list_prs) + the three
    // git write tools (open_pr / post_pr_comment / pr_review). Keep this list in sync with the IsAgentToolEligible
    // repo nodes — for the write tools this is the ONLY tool-fabric tenancy coverage (post_pr_comment has no
    // node-level teamless unit test). A teamless call must NOT succeed (it errors fail-closed).
    [Theory]
    [InlineData("agent.run_command")]
    [InlineData("git.fetch_pr_diff")]
    [InlineData("git.fetch_pr_checks")]
    [InlineData("git.list_prs")]
    [InlineData("git.open_pr")]
    [InlineData("git.post_pr_comment")]
    [InlineData("git.pr_review")]
    public async Task A_repo_resolving_tool_with_no_team_context_fails_closed(string kind)
    {
        using var scope = _fixture.BeginScope();
        var tool = scope.Resolve<IAgentToolRegistry>().Resolve(kind);

        tool.ShouldNotBeNull($"{kind} must project onto the tool fabric via DI");

        // A repositoryId is named but the call has no TeamId → no sys.team_id → the node can't resolve a tenant.
        // The extra fields satisfy each node's OTHER required inputs (command / number / title+branches / body /
        // verdict) so it reaches — and fails at — the team-scope check rather than tripping an unrelated
        // "missing input" guard first (every write node validates its required inputs BEFORE TryReadTeamId).
        var input = JsonSerializer.SerializeToElement(new
        {
            repositoryId = Guid.NewGuid().ToString(),
            command = "true",
            number = 1,
            title = "t",
            sourceBranch = "feature",
            targetBranch = "main",
            body = "b",
            verdict = "comment",
        });
        var result = await tool!.CallAsync(new AgentToolCall { Input = input }, CancellationToken.None);

        result.IsError.ShouldBeTrue($"{kind} must fail closed when invoked with a repositoryId but no team — never resolve a repo cross-tenant");
    }
}
