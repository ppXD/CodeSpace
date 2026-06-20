using System.Text.Json;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Agents.Commands;
using CodeSpace.Core.Services.Agents.Tools;
using CodeSpace.Core.Services.PullRequests;
using CodeSpace.Core.Services.Workflows.Nodes;
using CodeSpace.Core.Services.Workflows.Nodes.Builtin;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Dtos.Providers;
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

    private static AgentToolRegistry Build(params INodeRuntime[] nodes) => new(nodes, Array.Empty<IAgentTool>(), NullLoggerFactory.Instance);

    private static AgentToolRegistry BuildWith(IEnumerable<INodeRuntime> nodes, IEnumerable<IAgentTool> firstParty) => new(nodes, firstParty, NullLoggerFactory.Instance);

    /// <summary>A minimal first-party (non-node) tool, the shape DecisionRequestTool registers under.</summary>
    private sealed class FakeFirstPartyTool : IAgentTool
    {
        public FakeFirstPartyTool(string kind) => Kind = kind;
        public string Kind { get; }
        public string Description => "fake";
        public JsonElement InputSchema { get; } = SchemaBuilder.EmptyObject();
        public JsonElement OutputSchema { get; } = SchemaBuilder.EmptyObject();
        public AgentToolValidation ValidateInput(JsonElement input) => AgentToolValidation.Valid;
        public Task<AgentToolResult> CallAsync(AgentToolCall call, CancellationToken ct) => Task.FromResult(AgentToolResult.Fail("n/a"));
    }

    [Fact]
    public void First_party_tools_merge_into_the_catalog_alongside_node_tools()
    {
        var registry = BuildWith(
            new INodeRuntime[] { new FakeNode("git.read", eligible: true) },
            new IAgentTool[] { new FakeFirstPartyTool("decision.request") });

        registry.All.Select(t => t.Kind).ShouldBe(new[] { "decision.request", "git.read" }, "sorted union of node + first-party tools");
        registry.Resolve("decision.request").ShouldNotBeNull("a first-party tool resolves by kind");
    }

    [Fact]
    public void No_first_party_tools_is_byte_identical_to_node_only()
    {
        // The governance-OFF posture: an empty first-party set leaves the catalog exactly as node-only (the D2 byte-identical pin).
        BuildWith(new INodeRuntime[] { new FakeNode("git.read", eligible: true) }, Array.Empty<IAgentTool>())
            .All.Select(t => t.Kind).ShouldBe(new[] { "git.read" });
    }

    [Fact]
    public void A_first_party_tool_colliding_with_a_node_kind_fails_loudly()
    {
        Should.Throw<InvalidOperationException>(() => BuildWith(
                new INodeRuntime[] { new FakeNode("git.read", eligible: true) },
                new IAgentTool[] { new FakeFirstPartyTool("git.read") }))
            .Message.ShouldContain("git.read");
    }

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
        var node = new AgentRunCommandNode(new StubRunCommandService(), null!);
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
        tool.AlwaysRequiresApproval.ShouldBeFalse($"{kind} is REVERSIBLE → Allow-able at Unleashed (only the irreversible merge is always-approve)");
    }

    [Fact]
    public void Git_merge_pr_is_eligible_destructive_and_always_requires_approval()
    {
        // PIN: git.merge_pr is now EXPOSED on the agent-tool surface — but because the merge is IRREVERSIBLE it is
        // marked AlwaysRequiresApproval, so it can NEVER auto-run at any tier. A future accidental flip of either
        // flag (un-exposing it, or dropping the always-approve guard) MUST fail here.
        var merge = new GitMergePullRequestNode(null!);

        merge.Manifest.IsAgentToolEligible.ShouldBeTrue("git.merge_pr now projects onto the agent-tool surface");
        merge.Manifest.AlwaysRequiresApproval.ShouldBeTrue("git.merge_pr is irreversible → it must always require a human approval");

        var tool = Build(merge).Resolve("git.merge_pr").ShouldNotBeNull("git.merge_pr now resolves from the registry");
        tool.IsDestructive.ShouldBeTrue("merging integrates code → a destructive tool");
        tool.RequiresApproval.ShouldBeTrue("git.merge_pr is destructive → approval-gated by default");
        tool.AlwaysRequiresApproval.ShouldBeTrue("git.merge_pr can never auto-run — always-approve");

        // THE invariant a future regression must not break: an always-approve merge can never be Allow-ed (auto-run),
        // not even at the most permissive tier.
        AgentToolGate.Decide(AgentAutonomyLevel.Unleashed, requiresApproval: true, alwaysRequiresApproval: true)
            .ShouldBe(AgentToolGateDecision.RequireApproval, "git.merge_pr at Unleashed escalates to RequireApproval — never Allow");
    }

    [Fact]
    public void Exposing_the_git_writes_does_not_regress_the_read_only_tools_or_run_command()
    {
        // No-regression guard: the three read-only git tools stay read-only/non-destructive and agent.run_command
        // stays destructive, side by side with the newly-exposed writes — the side-effect flag is the only axis.
        var registry = Build(
            new GitFetchPrDiffNode(null!), new GitFetchPrChecksNode(null!), new GitListPullRequestsNode(null!),
            new AgentRunCommandNode(new StubRunCommandService(), null!),
            GitWriteNode("git.open_pr"), GitWriteNode("git.post_pr_comment"), GitWriteNode("git.pr_review"));

        foreach (var read in new[] { "git.fetch_pr_diff", "git.fetch_pr_checks", "git.list_prs" })
        {
            var tool = registry.Resolve(read).ShouldNotBeNull();
            tool.IsReadOnly.ShouldBeTrue($"{read} stays read-only");
            tool.IsDestructive.ShouldBeFalse($"{read} stays non-destructive");
        }

        registry.Resolve("agent.run_command").ShouldNotBeNull().IsDestructive.ShouldBeTrue("agent.run_command stays destructive");
    }

    [Theory]
    [InlineData("git.open_pr")]
    [InlineData("git.pr_review")]
    public async Task A_model_supplied_actAsUserId_is_stripped_on_the_tool_path_so_the_write_uses_the_connection_credential(string kind)
    {
        // SAFETY: actAsUserId ("act as this CodeSpace user's own linked identity", Model B) is only safe on the
        // engine respond path, where ActorIdentityRequirementGate proves the AUTHENTICATED responder IS that user
        // before their stored OAuth token is spent. No such gate runs on the synthetic NodeAgentTool path — so a
        // model-supplied actAsUserId there would forge a PR / an APPROVE review as ANY teammate who linked an
        // identity. NodeAgentTool must STRIP it: the node calls the service with actorUserId == null (→ the repo
        // CONNECTION credential), making the "not a wider attack surface" claim true.
        var pr = new CapturingPullRequestService();
        var node = ActAsUserNode(kind, pr);
        var tool = new NodeAgentTool(node, NullLogger.Instance);

        var teamId = Guid.NewGuid();
        var victim = Guid.NewGuid();   // a teammate the model tries to impersonate
        var input = JsonSerializer.SerializeToElement(new
        {
            repositoryId = Guid.NewGuid().ToString(),
            title = "t", sourceBranch = "feature", targetBranch = "main",   // git.open_pr requireds
            number = 7, verdict = "approve",                                // git.pr_review requireds
            actAsUserId = victim.ToString(),                                // the impersonation attempt
        });

        var result = await tool.CallAsync(new AgentToolCall { Input = input, TeamId = teamId }, CancellationToken.None);

        result.IsError.ShouldBeFalse($"{kind} should reach the service (fake succeeds) once actAsUserId is stripped");
        pr.LastActorUserId.ShouldBeNull($"{kind} via the tool path must NOT honour a model-supplied actAsUserId — it acts as the connection credential, never as {victim}");
    }

    /// <summary>Captures the actorUserId the node forwards; everything else returns a minimal success shape. Only
    /// the two act-as-user writes (open_pr / pr_review) are exercised; the rest throw so a misuse is loud.</summary>
    private sealed class CapturingPullRequestService : IPullRequestService
    {
        public Guid? LastActorUserId { get; private set; }

        public Task<RemotePullRequest> OpenPullRequestAsync(Guid repositoryId, Guid teamId, OpenPullRequestInput input, Guid? actorUserId, CancellationToken ct)
        {
            LastActorUserId = actorUserId;
            return Task.FromResult(new RemotePullRequest
            {
                ExternalId = "1", Number = 1, Title = input.Title, State = PullRequestState.Open,
                SourceBranch = input.SourceBranch, TargetBranch = input.TargetBranch,
                CommentsCount = 0, CreatedDate = DateTimeOffset.UnixEpoch, UpdatedDate = DateTimeOffset.UnixEpoch, WebUrl = "https://x",
            });
        }

        public Task<RemotePullRequestReview> SubmitReviewAsync(Guid repositoryId, Guid teamId, int number, PullRequestReviewVerdict verdict, string? body, Guid? actorUserId, CancellationToken ct)
        {
            LastActorUserId = actorUserId;
            return Task.FromResult(new RemotePullRequestReview { Verdict = verdict, WebUrl = "https://x" });
        }

        public Task<IReadOnlyList<RemotePullRequest>> ListAsync(Guid repositoryId, Guid teamId, PullRequestState? state, int page, int perPage, CancellationToken ct) => throw new NotSupportedException();
        public Task<RemotePullRequest> GetAsync(Guid repositoryId, Guid teamId, int number, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<RemotePullRequestCommit>> ListCommitsAsync(Guid repositoryId, Guid teamId, int number, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<RemotePullRequestFile>> ListFilesAsync(Guid repositoryId, Guid teamId, int number, CancellationToken ct) => throw new NotSupportedException();
        public Task<RemotePullRequestCounts> GetCountsAsync(Guid repositoryId, Guid teamId, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<RemotePullRequestCheck>> ListChecksAsync(Guid repositoryId, Guid teamId, int number, CancellationToken ct) => throw new NotSupportedException();
        public Task<RemotePullRequestComment> PostCommentAsync(Guid repositoryId, Guid teamId, int number, string body, CancellationToken ct) => throw new NotSupportedException();
        public Task<RemotePullRequestMergeResult> MergePullRequestAsync(Guid repositoryId, Guid teamId, int number, MergePullRequestInput input, Guid? actorUserId, CancellationToken ct) => throw new NotSupportedException();
    }

    private static INodeRuntime ActAsUserNode(string kind, IPullRequestService pr) => kind switch
    {
        "git.open_pr" => new GitOpenPullRequestNode(pr),
        "git.pr_review" => new GitPrReviewNode(pr),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "not an act-as-user git-write node"),
    };

    private static INodeRuntime GitWriteNode(string kind) => kind switch
    {
        "git.open_pr" => new GitOpenPullRequestNode(null!),
        "git.post_pr_comment" => new GitPostPrCommentNode(null!),
        "git.pr_review" => new GitPrReviewNode(null!),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "not a reversible git-write node"),
    };
}
