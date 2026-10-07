using System.Text.Json;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Agents.Commands;
using CodeSpace.Core.Services.Agents.Tools;
using CodeSpace.Core.Services.PullRequests;
using CodeSpace.Core.Services.Workflows.Nodes;
using CodeSpace.Core.Services.Workflows.Nodes.Builtin;
using CodeSpace.Core.Services.Workflows.Runtime;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Dtos.Providers;
using CodeSpace.Messages.Enums;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace CodeSpace.UnitTests.Agents;

/// <summary>
/// Pins the node→tool adapter: the node manifest becomes the tool schema, the side-effect flag drives the
/// fail-closed risk (read-only → safe/no-approval, side-effecting → destructive/gated), success maps to a
/// structured result, failure to a typed error, and a SUSPENDING node is rejected (a tool call must be
/// synchronous). Exercised against a real agent.run_command node plus configurable fakes.
/// </summary>
[Trait("Category", "Unit")]
public class NodeAgentToolTests
{
    private static readonly JsonElement EmptyObject = JsonDocument.Parse("{}").RootElement;

    private sealed class StubNode : INodeRuntime
    {
        private readonly NodeResult _result;
        public StubNode(string typeKey, bool sideEffecting, NodeResult result)
        {
            TypeKey = typeKey;
            _result = result;
            Manifest = new NodeManifest
            {
                DisplayName = typeKey, Category = "Test", Kind = NodeKind.Regular, Description = "desc",
                IsSideEffecting = sideEffecting,
                ConfigSchema = SchemaBuilder.EmptyObject(), InputSchema = SchemaBuilder.EmptyObject(), OutputSchema = SchemaBuilder.EmptyObject(),
            };
        }
        public string TypeKey { get; }
        public NodeManifest Manifest { get; }
        public Task<NodeResult> RunAsync(NodeRunContext context, CancellationToken ct) => Task.FromResult(_result);
    }

    private sealed class StubRunCommandService : IRunCommandService
    {
        public SandboxResult Result = new() { Status = SandboxStatus.Success, ExitCode = 0, Stdout = "ok", Stderr = "" };
        public Task<SandboxResult> RunAsync(RunCommandRequest request, CancellationToken ct) => Task.FromResult(Result);
    }

    /// <summary>Captures the NodeRunContext the adapter builds, so tests can assert what landed on the synthetic scope.</summary>
    private sealed class CapturingNode : INodeRuntime
    {
        public NodeRunContext? Captured { get; private set; }
        public string TypeKey => "test.capture";
        public NodeManifest Manifest { get; } = new()
        {
            DisplayName = "capture", Category = "Test", Kind = NodeKind.Regular, Description = "desc", IsSideEffecting = false,
            ConfigSchema = SchemaBuilder.EmptyObject(), InputSchema = SchemaBuilder.EmptyObject(), OutputSchema = SchemaBuilder.EmptyObject(),
        };
        public Task<NodeResult> RunAsync(NodeRunContext context, CancellationToken ct)
        {
            Captured = context;
            return Task.FromResult(NodeResult.Ok());
        }
    }

    private static NodeAgentTool Tool(INodeRuntime node, IAgentRepositoryPolicy? repositoryPolicy = null, IAgentToolPreviewer? previewer = null) => new(node, new TestNodeInvocations(node), repositoryPolicy ?? new RecordingRepositoryPolicy(refusal: null), previewer ?? new RecordingPreviewer(), NullLogger.Instance);

    /// <summary>Records what the tool hands the previewer — the manifest and the inputs as the node reads them — and answers with an empty preview.</summary>
    private sealed class RecordingPreviewer : IAgentToolPreviewer
    {
        public List<(NodeManifest Manifest, AgentToolCall Call, IReadOnlyDictionary<string, JsonElement> Inputs)> Asked { get; } = new();

        public Task<ToolCallPreview> PreviewAsync(NodeManifest manifest, AgentToolCall call, IReadOnlyDictionary<string, JsonElement> inputs, CancellationToken cancellationToken)
        {
            Asked.Add((manifest, call, inputs));
            return Task.FromResult(new ToolCallPreview());
        }
    }

    /// <summary>A node that declares a repository input (<see cref="NodeManifest.RepositoryInput"/>) and records whether it ran — the shape every repository-taking builtin node has.</summary>
    private sealed class RepositoryNode : INodeRuntime
    {
        public RepositoryNode(bool writes, string? refInputKey = null) => Manifest = new NodeManifest
        {
            DisplayName = "repo", Category = "Test", Kind = NodeKind.Regular, Description = "desc", IsSideEffecting = writes,
            RepositoryInput = new RepositoryInputSpec { InputKey = "repositoryId", WritesRepository = writes, RefInputKey = refInputKey },
            ConfigSchema = SchemaBuilder.EmptyObject(), InputSchema = SchemaBuilder.EmptyObject(), OutputSchema = SchemaBuilder.EmptyObject(),
        };
        public bool Ran { get; private set; }
        public string TypeKey => "test.repository";
        public NodeManifest Manifest { get; }
        public Task<NodeResult> RunAsync(NodeRunContext context, CancellationToken ct)
        {
            Ran = true;
            return Task.FromResult(NodeResult.Ok());
        }
    }

    /// <summary>Records every use the tool asked the repository's policy about, and answers with a fixed refusal (null = the policy lets the use through).</summary>
    private sealed class RecordingRepositoryPolicy(string? refusal) : IAgentRepositoryPolicy
    {
        public List<AgentRepositoryUse> Asked { get; } = new();
        public Task<string?> RefusalAsync(AgentRepositoryUse use, CancellationToken cancellationToken)
        {
            Asked.Add(use);
            return Task.FromResult(refusal);
        }
    }

    private sealed class TestNodeInvocations(INodeRuntime node) : INodeInvocationExecutor
    {
        public Task<NodeResult> ExecuteAsync(NodeInvocation invocation, CancellationToken cancellationToken)
        {
            invocation.TypeKey.ShouldBe(node.TypeKey);
            return node.RunAsync(invocation.Context, cancellationToken);
        }
    }

    [Fact]
    public void A_read_only_node_maps_to_a_safe_unguarded_tool()
    {
        IAgentTool tool = Tool(new StubNode("git.read", sideEffecting: false, NodeResult.Ok()));

        tool.Kind.ShouldBe("git.read");
        tool.IsReadOnly.ShouldBeTrue();
        tool.IsConcurrencySafe.ShouldBeTrue();
        tool.IsDestructive.ShouldBeFalse();
        tool.RequiresApproval.ShouldBeFalse();
    }

    [Fact]
    public void A_side_effecting_node_maps_to_a_destructive_gated_tool()
    {
        IAgentTool tool = Tool(new StubNode("git.open_pr", sideEffecting: true, NodeResult.Ok()));

        tool.IsReadOnly.ShouldBeFalse();
        tool.IsConcurrencySafe.ShouldBeFalse();
        tool.IsDestructive.ShouldBeTrue();
        tool.RequiresApproval.ShouldBeTrue("a side-effecting node is gated by default");
    }

    [Fact]
    public async Task Success_maps_node_outputs_to_a_structured_ok_result()
    {
        var outputs = new Dictionary<string, JsonElement> { ["n"] = JsonSerializer.SerializeToElement(7) };
        var tool = Tool(new StubNode("t", false, NodeResult.Ok(outputs)));

        var result = await tool.CallAsync(new AgentToolCall { Input = EmptyObject }, CancellationToken.None);

        result.IsError.ShouldBeFalse();
        result.Output.GetProperty("n").GetInt32().ShouldBe(7);
        result.OutputBytes.ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task Failure_maps_to_a_typed_error()
    {
        var result = await Tool(new StubNode("t", false, NodeResult.Fail("boom"))).CallAsync(new AgentToolCall { Input = EmptyObject }, CancellationToken.None);

        result.IsError.ShouldBeTrue();
        result.Error.ShouldContain("boom");
    }

    [Fact]
    public async Task A_suspending_node_is_not_tool_callable()
    {
        var suspend = NodeResult.Suspend(new SuspensionToken { Kind = "agent_run", Payload = EmptyObject });
        var result = await Tool(new StubNode("agent.run", true, suspend)).CallAsync(new AgentToolCall { Input = EmptyObject }, CancellationToken.None);

        result.IsError.ShouldBeTrue();
        result.Error.ShouldContain("suspends");
    }

    [Fact]
    public void Non_object_input_is_rejected_by_validate()
    {
        var tool = Tool(new StubNode("t", false, NodeResult.Ok()));

        tool.ValidateInput(EmptyObject).IsValid.ShouldBeTrue();
        tool.ValidateInput(JsonSerializer.SerializeToElement("a string")).IsValid.ShouldBeFalse();
    }

    [Theory]
    [InlineData("""{"zz":1}""", "'zz'")]                                   // an inert key that would make a rejected call look new
    [InlineData("""{"number":7,"note":"x","Number":8}""", "'note', 'Number'")]   // keys are matched exactly, as the node reads them
    public void An_input_the_schema_does_not_declare_is_refused_naming_what_the_tool_takes(string inputJson, string named)
    {
        var tool = Tool(new GitMergePullRequestNode(null!));

        var validation = tool.ValidateInput(JsonDocument.Parse(inputJson).RootElement);

        validation.IsValid.ShouldBeFalse();
        validation.Error.ShouldBe($"Tool 'git.merge_pr' does not take {named}. It takes only: repositoryId, number, method, commitTitle, commitMessage, deleteSourceBranch, expectedHeadSha, expectedBaseBranch, actAsUserId.");
    }

    [Fact]
    public void Every_declared_input_is_accepted()
    {
        var tool = Tool(new GitMergePullRequestNode(null!));
        var input = JsonSerializer.SerializeToElement(new { repositoryId = Guid.NewGuid().ToString(), number = 7, method = "squash", commitTitle = "t", commitMessage = "m", deleteSourceBranch = true, expectedHeadSha = "0a1b", expectedBaseBranch = "main", actAsUserId = Guid.NewGuid().ToString() });

        tool.ValidateInput(input).IsValid.ShouldBeTrue(tool.ValidateInput(input).Error);
    }

    [Fact]
    public void The_advertised_schema_is_the_nodes_own_closed_to_its_declared_inputs()
    {
        var node = new GitMergePullRequestNode(null!);

        var schema = Tool(node).InputSchema;

        schema.GetProperty("additionalProperties").GetBoolean().ShouldBeFalse("the model is told up front that only declared inputs are taken");
        JsonElement.DeepEquals(schema.GetProperty("properties"), node.Manifest.InputSchema.GetProperty("properties")).ShouldBeTrue("every declared input is advertised as the node declares it");
        JsonElement.DeepEquals(schema.GetProperty("required"), node.Manifest.InputSchema.GetProperty("required")).ShouldBeTrue();
        node.Manifest.InputSchema.TryGetProperty("additionalProperties", out _).ShouldBeFalse("the workflow editor's manifest is not changed");
    }

    [Fact]
    public async Task The_preview_is_resolved_from_the_manifest_over_the_inputs_the_node_would_read()
    {
        var previewer = new RecordingPreviewer();
        var node = new GitMergePullRequestNode(null!);
        var call = new AgentToolCall { Input = JsonSerializer.SerializeToElement(new { repositoryId = BoundRepository.ToString(), number = 7, actAsUserId = Guid.NewGuid().ToString() }), TeamId = Guid.NewGuid(), CallerPosture = BoundTo(BoundRepository) };

        await Tool(node, previewer: previewer).PreviewAsync(call, CancellationToken.None);

        var asked = previewer.Asked.ShouldHaveSingleItem();
        asked.Manifest.ShouldBeSameAs(node.Manifest);
        asked.Call.ShouldBeSameAs(call);
        asked.Inputs.Keys.ShouldBe(["repositoryId", "number"], ignoreOrder: true, "the actor key is stripped as the call strips it, so the card never shows an identity the call will not act as");
    }

    [Fact]
    public async Task A_real_run_command_node_projects_as_a_destructive_tool_and_runs()
    {
        var node = new AgentRunCommandNode(new StubRunCommandService(), null!);
        IAgentTool tool = Tool(node);

        tool.Kind.ShouldBe("agent.run_command");
        tool.IsDestructive.ShouldBeTrue("running a command is side-effecting");

        var input = JsonSerializer.SerializeToElement(new { command = "echo" });
        var result = await tool.CallAsync(new AgentToolCall { Input = input }, CancellationToken.None);

        result.IsError.ShouldBeFalse();
        result.Output.GetProperty("exitCode").GetInt32().ShouldBe(0);
        result.Output.GetProperty("status").GetString().ShouldBe("Success");
    }

    // ── team scope stamping ────────────────────────────────────────────────────

    [Fact]
    public async Task A_call_with_a_team_stamps_sys_team_id_as_a_string_that_the_scope_reader_resolves()
    {
        // Load-bearing: the Guid MUST serialize as a JSON STRING element (byte-shape parity with
        // WorkflowEngine.BuildSysScope), because NodeScopeReader.TryReadTeamId requires ValueKind==String +
        // Guid.TryParse. Serialize it as a number/raw and the team silently fails to resolve (the fail-closed bug).
        var teamId = Guid.NewGuid();
        var node = new CapturingNode();

        await Tool(node).CallAsync(new AgentToolCall { Input = EmptyObject, TeamId = teamId }, CancellationToken.None);

        var captured = node.Captured.ShouldNotBeNull();
        captured.Scope.Sys[SystemScopeKeys.TeamId].ValueKind.ShouldBe(JsonValueKind.String, "the Guid must be a JSON string, not a number — else the scope reader fails closed");
        NodeScopeReader.TryReadTeamId(captured, out var read).ShouldBeTrue();
        read.ShouldBe(teamId);
    }

    [Fact]
    public async Task A_call_with_no_team_leaves_sys_empty_so_the_scope_reader_fails_closed()
    {
        var node = new CapturingNode();

        await Tool(node).CallAsync(new AgentToolCall { Input = EmptyObject }, CancellationToken.None);   // TeamId omitted → null

        var captured = node.Captured.ShouldNotBeNull();
        captured.Scope.Sys.ContainsKey(SystemScopeKeys.TeamId).ShouldBeFalse("no team → no team_id → today's fail-closed default");
        NodeScopeReader.TryReadTeamId(captured, out _).ShouldBeFalse();
    }

    [Fact]
    public async Task A_call_carries_its_runs_posture_onto_the_synthetic_context_and_a_call_without_one_carries_none()
    {
        // The posture rides the call (stamped by the run's endpoint), never the model's input, and lands on the typed
        // context field a sandbox-starting node reads — so agent.run_command runs no wider than the calling run.
        var posture = new AgentRunPosture { Autonomy = AgentAutonomyLevel.Unleashed, Permissions = new AgentPermissions { Network = AgentNetworkAccess.Off } };
        var withPosture = new CapturingNode();
        var without = new CapturingNode();

        await Tool(withPosture).CallAsync(new AgentToolCall { Input = EmptyObject, CallerPosture = posture }, CancellationToken.None);
        await Tool(without).CallAsync(new AgentToolCall { Input = EmptyObject }, CancellationToken.None);

        withPosture.Captured.ShouldNotBeNull().CallerPosture.ShouldBeSameAs(posture);
        without.Captured.ShouldNotBeNull().CallerPosture.ShouldBeNull("no calling run → the node keeps its own posture");
    }

    [Fact]
    public async Task TeamId_does_not_alter_inputs_rawinputs_config_or_observability()
    {
        // Surgical-change pin: stamping the team touches ONLY Scope.Sys — every other facet of the synthetic
        // context is identical with or without a team.
        var input = JsonSerializer.SerializeToElement(new { repositoryId = "abc", command = "echo" });
        var withTeam = new CapturingNode();
        var without = new CapturingNode();

        await Tool(withTeam).CallAsync(new AgentToolCall { Input = input, TeamId = Guid.NewGuid() }, CancellationToken.None);
        await Tool(without).CallAsync(new AgentToolCall { Input = input }, CancellationToken.None);

        var a = withTeam.Captured.ShouldNotBeNull();
        var b = without.Captured.ShouldNotBeNull();
        a.RawInputs.GetRawText().ShouldBe(b.RawInputs.GetRawText());
        JsonSerializer.SerializeToElement(a.Inputs).GetRawText().ShouldBe(JsonSerializer.SerializeToElement(b.Inputs).GetRawText());
        a.Config.Count.ShouldBe(b.Config.Count);
        a.Config.Count.ShouldBe(0);
        a.Observability.ShouldBeSameAs(b.Observability);   // both NodeObservability.NoOp
    }

    // ── the calling run's repository binding ───────────────────────────────────

    private static readonly Guid BoundRepository = Guid.NewGuid();

    private static AgentRunPosture BoundTo(params Guid[] repositoryIds) => new()
    {
        Autonomy = AgentAutonomyLevel.Unleashed,
        Permissions = new AgentPermissions(),
        Repositories = repositoryIds.Select(id => new WorkspaceRepositorySpec { Alias = id.ToString("N"), RepositoryId = id }).ToList(),
    };

    private static AgentToolCall CallNaming(string repositoryIdJson, AgentRunPosture? caller) =>
        new() { Input = JsonDocument.Parse($$"""{"repositoryId":{{repositoryIdJson}}}""").RootElement.Clone(), TeamId = Guid.NewGuid(), CallerPosture = caller };

    [Theory]
    [InlineData("bound", false)]
    [InlineData("unbound", true)]
    [InlineData("unbound-braced", true)]   // "{uuid}" parses as a uuid, so a node would act on it — the binding must see it too
    public async Task A_tool_a_run_calls_reaches_only_a_repository_the_run_is_bound_to(string target, bool refused)
    {
        var unbound = Guid.NewGuid();
        var named = target switch { "bound" => $"\"{BoundRepository}\"", "unbound" => $"\"{unbound}\"", _ => $"\"{{{unbound}}}\"" };
        var node = new RepositoryNode(writes: false);

        var result = await Tool(node).CallAsync(CallNaming(named, BoundTo(BoundRepository)), CancellationToken.None);

        result.IsError.ShouldBe(refused, $"a {target} repository: {result.Error}");
        node.Ran.ShouldBe(!refused, "a refused call never reaches the node, so no clone, provider call or command runs");
        if (refused) result.Error.ShouldBe(AgentRepositoryBinding.NotFound(unbound), "an unbound repository gets exactly the not-found a missing or foreign one gets — no existence oracle");
    }

    [Theory]
    [InlineData("\"abc\"")]
    [InlineData("\"\"")]
    [InlineData("42")]
    [InlineData("null")]
    public async Task A_repository_value_no_node_would_act_on_reaches_the_node_which_refuses_or_ignores_it_as_it_always_did(string repositoryIdJson)
    {
        // Every repository-taking node reads its id as a JSON string that parses as a uuid. Anything else it treats as
        // absent (agent.run_command runs with no checkout) or invalid (the git tools fail) — no repository is reached,
        // so there is nothing for the binding to hold.
        var node = new RepositoryNode(writes: false);

        var result = await Tool(node).CallAsync(CallNaming(repositoryIdJson, BoundTo(BoundRepository)), CancellationToken.None);

        result.IsError.ShouldBeFalse(result.Error);
        node.Ran.ShouldBeTrue();
    }

    [Fact]
    public async Task A_call_with_no_calling_run_reaches_any_repository_as_a_workflow_node_always_did()
    {
        // No CallerPosture → not an agent's tool call (a workflow node, a test): the node resolves its repository within
        // its team exactly as before. The binding is a property of an agent run, never of the node.
        var node = new RepositoryNode(writes: true);
        var policy = new RecordingRepositoryPolicy(refusal: "should never be asked");

        var result = await Tool(node, policy).CallAsync(CallNaming($"\"{Guid.NewGuid()}\"", caller: null), CancellationToken.None);

        result.IsError.ShouldBeFalse(result.Error);
        node.Ran.ShouldBeTrue();
        policy.Asked.ShouldBeEmpty("off the agent path the repository's publish policy is the publish path's business, not the tool's");
    }

    [Fact]
    public async Task A_node_that_declares_no_repository_input_is_not_held_to_the_binding()
    {
        var node = new CapturingNode();

        var result = await Tool(node).CallAsync(CallNaming($"\"{Guid.NewGuid()}\"", BoundTo()), CancellationToken.None);

        result.IsError.ShouldBeFalse(result.Error);
        node.Captured.ShouldNotBeNull("a node with no repository input never names a repository, so the binding has nothing to hold");
    }

    [Theory]
    [InlineData(false, "bound", false)]    // a read never meets the publish policy
    [InlineData(true, "bound", true)]      // a write to a bound repository does
    [InlineData(true, "unbound", false)]   // an unbound one is refused as not found first — the policy is never asked about it
    public async Task Only_a_write_to_a_bound_repository_meets_the_repositorys_publish_policy(bool writes, string target, bool asked)
    {
        var named = target == "bound" ? BoundRepository : Guid.NewGuid();
        var policy = new RecordingRepositoryPolicy(refusal: null);
        var call = CallNaming($"\"{named}\"", BoundTo(BoundRepository));

        await Tool(new RepositoryNode(writes), policy).CallAsync(call, CancellationToken.None);

        policy.Asked.Count.ShouldBe(asked ? 1 : 0);
        if (asked) (policy.Asked[0].Bound.RepositoryId, policy.Asked[0].TeamId, policy.Asked[0].Writes).ShouldBe((named, call.TeamId!.Value, true), "the policy is asked about the named repository's write within the run's own team");
    }

    [Fact]
    public async Task A_write_the_publish_policy_refuses_never_reaches_the_node()
    {
        var node = new RepositoryNode(writes: true);
        var policy = new RecordingRepositoryPolicy(refusal: "Repository x does not take agent pull-request writes: the repository requires patch-only publishing.");

        var result = await Tool(node, policy).CallAsync(CallNaming($"\"{BoundRepository}\"", BoundTo(BoundRepository)), CancellationToken.None);

        result.IsError.ShouldBeTrue();
        result.Error.ShouldContain("patch-only");
        node.Ran.ShouldBeFalse("a write the repository's policy refuses never reaches the provider");
    }

    [Theory]
    // access                 branch (JSON)          asked   ref the policy judges
    [InlineData(WorkspaceAccess.Read, "\"secret-branch\"", true, "secret-branch")]
    [InlineData(WorkspaceAccess.Read, "\"  release/1 \"", true, "release/1")]     // trimmed, as the node reads it
    [InlineData(WorkspaceAccess.Read, "\"  \"", false, null)]                      // blank is the default branch — nothing to judge
    [InlineData(WorkspaceAccess.Read, "42", false, null)]                            // not a string: the node checks out its default branch
    [InlineData(WorkspaceAccess.Write, "\"secret-branch\"", false, null)]          // the run's own repository: any ref
    public async Task A_ref_named_on_read_only_context_is_put_to_the_repositorys_policy_before_the_call_is_admitted(WorkspaceAccess access, string branchJson, bool asked, string? judgedRef)
    {
        var policy = new RecordingRepositoryPolicy(refusal: null);
        var caller = new AgentRunPosture { Autonomy = AgentAutonomyLevel.Unleashed, Permissions = new AgentPermissions(), Repositories = [new() { Alias = "ctx", RepositoryId = BoundRepository, Access = access }] };
        var call = new AgentToolCall { Input = JsonDocument.Parse($$"""{"repositoryId":"{{BoundRepository}}","branch":{{branchJson}}}""").RootElement.Clone(), TeamId = Guid.NewGuid(), CallerPosture = caller };

        (await Tool(new RepositoryNode(writes: false, refInputKey: "branch"), policy).RefusalAsync(call, CancellationToken.None)).ShouldBeNull();

        policy.Asked.Count.ShouldBe(asked ? 1 : 0);
        if (asked) (policy.Asked[0].Ref, policy.Asked[0].Writes, policy.Asked[0].Bound.Access).ShouldBe((judgedRef, false, access));
    }

    [Fact]
    public async Task The_admission_check_and_the_call_refuse_the_same_way()
    {
        // The MCP layer asks RefusalAsync before it parks a call for approval; CallAsync asks again when it runs. Both
        // must give the model the same answer, and neither reaches the node.
        var unbound = Guid.NewGuid();
        var node = new RepositoryNode(writes: true);
        var tool = Tool(node);
        var call = CallNaming($"\"{unbound}\"", BoundTo(BoundRepository));

        var admitted = await tool.RefusalAsync(call, CancellationToken.None);
        var called = await tool.CallAsync(call, CancellationToken.None);

        admitted.ShouldBe(AgentRepositoryBinding.NotFound(unbound));
        called.Error.ShouldBe(admitted);
        node.Ran.ShouldBeFalse();
    }

    [Fact]
    public async Task A_call_with_no_calling_run_is_admitted_without_a_look_at_the_repository()
    {
        var policy = new RecordingRepositoryPolicy(refusal: "should never be asked");

        (await Tool(new RepositoryNode(writes: true), policy).RefusalAsync(CallNaming($"\"{Guid.NewGuid()}\"", caller: null), CancellationToken.None)).ShouldBeNull();

        policy.Asked.ShouldBeEmpty();
    }

    [Theory]
    // tool                    access                 refused
    [InlineData("git.open_pr", WorkspaceAccess.Write, false)]
    [InlineData("git.open_pr", WorkspaceAccess.Read, true)]
    [InlineData("git.merge_pr", WorkspaceAccess.Write, false)]
    [InlineData("git.merge_pr", WorkspaceAccess.Read, true)]
    [InlineData("git.pr_review", WorkspaceAccess.Write, false)]
    [InlineData("git.pr_review", WorkspaceAccess.Read, true)]
    [InlineData("git.post_pr_comment", WorkspaceAccess.Write, false)]
    [InlineData("git.post_pr_comment", WorkspaceAccess.Read, true)]
    [InlineData("git.post_pr_comment", (WorkspaceAccess)99, true)]   // an access this code does not know is held like read-only context
    public async Task A_pull_request_write_reaches_only_a_repository_the_run_is_bound_to_with_write_access(string kind, WorkspaceAccess access, bool refused)
    {
        // The production write nodes over a provider that records what reached it. Read-only context is something the run
        // reads; it is not the run's to open, merge, review or comment on with the repository's connection credential.
        var provider = new RecordingPullRequestService();
        var node = PullRequestWriteNode(kind, provider);
        var policy = new RecordingRepositoryPolicy(refusal: null);
        var caller = new AgentRunPosture { Autonomy = AgentAutonomyLevel.Unleashed, Permissions = new AgentPermissions(), Repositories = [new() { Alias = "ctx", RepositoryId = BoundRepository, Access = access }] };
        var call = new AgentToolCall { Input = PullRequestWriteArguments(BoundRepository), TeamId = Guid.NewGuid(), CallerPosture = caller };

        var outcome = await OutcomeAsync(Tool(node, policy), call);

        if (refused)
        {
            outcome.ShouldBe(AgentRepositoryBinding.ReadOnlyContextWrite(BoundRepository));
            provider.Calls.ShouldBeEmpty("a write to read-only context never reaches the provider");
            policy.Asked.ShouldBeEmpty("refused on the binding's own access, before the repository's publish policy is consulted");
        }
        else provider.Calls.ShouldBe([$"{kind}:{BoundRepository}"], "a write to a repository the run is bound to writably reaches the provider");
    }

    private static INodeRuntime PullRequestWriteNode(string kind, IPullRequestService provider) => kind switch
    {
        "git.open_pr" => new GitOpenPullRequestNode(provider),
        "git.merge_pr" => new GitMergePullRequestNode(provider),
        "git.pr_review" => new GitPrReviewNode(provider),
        _ => new GitPostPrCommentNode(provider),
    };

    /// <summary>The required inputs of every pull-request write at once — each node reads its own keys and ignores the rest.</summary>
    private static JsonElement PullRequestWriteArguments(Guid repositoryId) => JsonSerializer.SerializeToElement(new
    {
        repositoryId = repositoryId.ToString(), number = 7, title = "t", sourceBranch = "feature", targetBranch = "main", body = "b", verdict = "comment",
    });

    /// <summary>What a call came back with — "reached" when the provider was called, else the tool's error.</summary>
    private static async Task<string> OutcomeAsync(NodeAgentTool tool, AgentToolCall call)
    {
        try
        {
            var result = await tool.CallAsync(call, CancellationToken.None);
            return result.IsError ? result.Error ?? "" : "ok";
        }
        catch (ProviderReachedException)
        {
            return "reached";
        }
    }

    private sealed class ProviderReachedException : Exception;

    /// <summary>Records each pull-request write that reached it, then stops the node — what happens past the provider call is not under test.</summary>
    private sealed class RecordingPullRequestService : IPullRequestService
    {
        public List<string> Calls { get; } = new();

        private Exception Reached(string kind, Guid repositoryId)
        {
            Calls.Add($"{kind}:{repositoryId}");
            return new ProviderReachedException();
        }

        public Task<IReadOnlyList<RemotePullRequest>> ListAsync(Guid repositoryId, Guid teamId, PullRequestState? state, int page, int perPage, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<RemotePullRequest> GetAsync(Guid repositoryId, Guid teamId, int number, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<RemotePullRequestCommit>> ListCommitsAsync(Guid repositoryId, Guid teamId, int number, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<RemotePullRequestFile>> ListFilesAsync(Guid repositoryId, Guid teamId, int number, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<RemotePullRequestCounts> GetCountsAsync(Guid repositoryId, Guid teamId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<RemotePullRequestCheck>> ListChecksAsync(Guid repositoryId, Guid teamId, int number, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<RemotePullRequestComment> PostCommentAsync(Guid repositoryId, Guid teamId, int number, string body, CancellationToken cancellationToken) => throw Reached("git.post_pr_comment", repositoryId);
        public Task<RemotePullRequestReview> SubmitReviewAsync(Guid repositoryId, Guid teamId, int number, SubmitPullRequestReviewInput input, Guid? actorUserId, CancellationToken cancellationToken) => throw Reached("git.pr_review", repositoryId);
        public Task<RemotePullRequest> OpenPullRequestAsync(Guid repositoryId, Guid teamId, OpenPullRequestInput input, Guid? actorUserId, CancellationToken cancellationToken) => throw Reached("git.open_pr", repositoryId);
        public Task<RemotePullRequestMergeResult> MergePullRequestAsync(Guid repositoryId, Guid teamId, int number, MergePullRequestInput input, Guid? actorUserId, CancellationToken cancellationToken) => throw Reached("git.merge_pr", repositoryId);
    }
}
