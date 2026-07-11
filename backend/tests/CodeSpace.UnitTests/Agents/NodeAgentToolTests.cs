using System.Text.Json;
using CodeSpace.Core.Services.Agents.Commands;
using CodeSpace.Core.Services.Agents.Tools;
using CodeSpace.Core.Services.Workflows.Nodes;
using CodeSpace.Core.Services.Workflows.Nodes.Builtin;
using CodeSpace.Core.Services.Workflows.Runtime;
using CodeSpace.Messages.Agents;
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

    private static NodeAgentTool Tool(INodeRuntime node) => new(node, NullLogger.Instance);

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
}
