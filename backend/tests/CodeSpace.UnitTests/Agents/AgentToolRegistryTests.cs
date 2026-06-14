using System.Text.Json;
using CodeSpace.Core.Services.Agents.Commands;
using CodeSpace.Core.Services.Agents.Tools;
using CodeSpace.Core.Services.Workflows.Nodes;
using CodeSpace.Core.Services.Workflows.Nodes.Builtin;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Enums;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace CodeSpace.UnitTests.Agents;

/// <summary>
/// Pins the agent-tool catalog: it projects ONLY tool-eligible nodes onto the fabric, resolves by kind, fails
/// loudly on a duplicate kind, and the real agent.run_command node is actually marked eligible.
/// </summary>
[Trait("Category", "Unit")]
public class AgentToolRegistryTests
{
    private sealed class FakeNode : INodeRuntime
    {
        public FakeNode(string typeKey, bool eligible)
        {
            TypeKey = typeKey;
            Manifest = new NodeManifest
            {
                DisplayName = typeKey, Category = "Test", Kind = NodeKind.Regular,
                IsAgentToolEligible = eligible,
                ConfigSchema = SchemaBuilder.EmptyObject(), InputSchema = SchemaBuilder.EmptyObject(), OutputSchema = SchemaBuilder.EmptyObject(),
            };
        }
        public string TypeKey { get; }
        public NodeManifest Manifest { get; }
        public Task<NodeResult> RunAsync(NodeRunContext context, CancellationToken ct) => Task.FromResult(NodeResult.Ok());
    }

    private sealed class StubRunCommandService : IRunCommandService
    {
        public Task<SandboxResult> RunAsync(RunCommandRequest request, CancellationToken ct) =>
            Task.FromResult(new SandboxResult { Status = SandboxStatus.Success, ExitCode = 0, Stdout = "", Stderr = "" });
    }

    private static AgentToolRegistry Build(params INodeRuntime[] nodes) => new(nodes, NullLoggerFactory.Instance);

    [Fact]
    public void Only_eligible_nodes_are_projected_as_tools()
    {
        var registry = Build(
            new FakeNode("git.read", eligible: true),
            new FakeNode("agent.code", eligible: false),    // suspends → not a tool
            new FakeNode("trigger.push", eligible: false),  // trigger → not a tool
            new FakeNode("run.cmd", eligible: true));

        registry.All.Select(t => t.Kind).ShouldBe(new[] { "git.read", "run.cmd" }, "sorted, eligible-only");
    }

    [Fact]
    public void Resolve_returns_the_tool_by_kind_or_null()
    {
        var registry = Build(new FakeNode("git.read", eligible: true));

        registry.Resolve("git.read").ShouldNotBeNull();
        registry.Resolve("git.read")!.Kind.ShouldBe("git.read");
        registry.Resolve("nope").ShouldBeNull();
        registry.Resolve("agent.code").ShouldBeNull("an ineligible node is not resolvable");
    }

    [Fact]
    public void Duplicate_tool_kinds_fail_loudly()
    {
        Should.Throw<InvalidOperationException>(() => Build(new FakeNode("dup", true), new FakeNode("dup", true)))
            .Message.ShouldContain("dup");
    }

    [Fact]
    public void An_empty_node_set_yields_an_empty_catalog()
    {
        Build().All.ShouldBeEmpty();
    }

    [Fact]
    public void The_real_run_command_node_is_marked_tool_eligible_and_appears_in_the_catalog()
    {
        var node = new AgentRunCommandNode(new StubRunCommandService());
        node.Manifest.IsAgentToolEligible.ShouldBeTrue("agent.run_command is a synchronous, standalone tool");

        Build(node).Resolve("agent.run_command").ShouldNotBeNull();
    }

    [Fact]
    public void The_read_only_git_nodes_project_as_eligible_non_destructive_tools()
    {
        // Manifest is static metadata + NodeAgentTool reads its risk flags from the manifest alone — neither
        // touches the IPullRequestService, so null! is safe (and cheaper than stubbing ~10 unused methods).
        INodeRuntime[] readNodes = { new GitFetchPrDiffNode(null!), new GitFetchPrChecksNode(null!), new GitListPullRequestsNode(null!) };

        foreach (var node in readNodes)
            node.Manifest.IsAgentToolEligible.ShouldBeTrue($"{node.TypeKey} is a synchronous read-only tool");

        var registry = Build(readNodes);

        registry.All.Select(t => t.Kind).ShouldBe(new[] { "git.fetch_pr_checks", "git.fetch_pr_diff", "git.list_prs" }, "sorted catalog");
        registry.All.ShouldAllBe(t => t.IsReadOnly && !t.IsDestructive);
    }

    [Theory]
    [InlineData("git.open_pr")]
    [InlineData("git.post_pr_comment")]
    [InlineData("git.pr_review")]
    public void The_three_reversible_git_write_nodes_project_as_eligible_destructive_gated_tools(string kind)
    {
        // null! is safe: the manifest is static metadata + NodeAgentTool reads its risk flags from the manifest
        // alone (neither touches the IPullRequestService). Each git write is side-effecting → destructive →
        // approval-gated by default; the autonomy tier decides whether to actually ask (D2) or ledger (C).
        var tool = Build(GitWriteNode(kind)).Resolve(kind);

        tool.ShouldNotBeNull($"{kind} is a synchronous, standalone git write → must project onto the tool fabric");
        tool!.IsReadOnly.ShouldBeFalse($"{kind} mutates provider state → not read-only");
        tool.IsDestructive.ShouldBeTrue($"{kind} is side-effecting → a destructive tool");
        tool.RequiresApproval.ShouldBeTrue($"{kind} is destructive → approval-gated by default");
    }

    [Fact]
    public void Git_merge_pr_is_NOT_eligible_and_never_resolves_as_a_tool()
    {
        // PIN: git.merge_pr is irreversible — it is DELIBERATELY excluded from the agent-tool surface and ships
        // last behind a per-tool force-approval policy. A future accidental flip of IsAgentToolEligible MUST fail
        // here (the registry would start projecting it). Keep merge ineligible until that policy lands.
        var merge = new GitMergePullRequestNode(null!);

        merge.Manifest.IsAgentToolEligible.ShouldBeFalse("git.merge_pr (irreversible) stays off the agent-tool surface until a per-tool force-approval policy ships");
        Build(merge).Resolve("git.merge_pr").ShouldBeNull("an ineligible node is never projected onto the fabric");
    }

    [Fact]
    public void Exposing_the_git_writes_does_not_regress_the_read_only_tools_or_run_command()
    {
        // No-regression guard: the three read-only git tools stay read-only/non-destructive and agent.run_command
        // stays destructive, side by side with the newly-exposed writes — the side-effect flag is the only axis.
        var registry = Build(
            new GitFetchPrDiffNode(null!), new GitFetchPrChecksNode(null!), new GitListPullRequestsNode(null!),
            new AgentRunCommandNode(new StubRunCommandService()),
            GitWriteNode("git.open_pr"), GitWriteNode("git.post_pr_comment"), GitWriteNode("git.pr_review"));

        foreach (var read in new[] { "git.fetch_pr_diff", "git.fetch_pr_checks", "git.list_prs" })
        {
            var tool = registry.Resolve(read).ShouldNotBeNull();
            tool.IsReadOnly.ShouldBeTrue($"{read} stays read-only");
            tool.IsDestructive.ShouldBeFalse($"{read} stays non-destructive");
        }

        registry.Resolve("agent.run_command").ShouldNotBeNull().IsDestructive.ShouldBeTrue("agent.run_command stays destructive");
    }

    private static INodeRuntime GitWriteNode(string kind) => kind switch
    {
        "git.open_pr" => new GitOpenPullRequestNode(null!),
        "git.post_pr_comment" => new GitPostPrCommentNode(null!),
        "git.pr_review" => new GitPrReviewNode(null!),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "not a reversible git-write node"),
    };
}
