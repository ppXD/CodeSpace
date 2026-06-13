using Autofac;
using CodeSpace.Core.Services.Agents.Tools;
using CodeSpace.IntegrationTests.Infrastructure;
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
}
