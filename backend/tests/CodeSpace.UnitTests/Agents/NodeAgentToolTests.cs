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
        var result = await Tool(new StubNode("agent.code", true, suspend)).CallAsync(new AgentToolCall { Input = EmptyObject }, CancellationToken.None);

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
        var node = new AgentRunCommandNode(new StubRunCommandService());
        IAgentTool tool = Tool(node);

        tool.Kind.ShouldBe("agent.run_command");
        tool.IsDestructive.ShouldBeTrue("running a command is side-effecting");

        var input = JsonSerializer.SerializeToElement(new { command = "echo" });
        var result = await tool.CallAsync(new AgentToolCall { Input = input }, CancellationToken.None);

        result.IsError.ShouldBeFalse();
        result.Output.GetProperty("exitCode").GetInt32().ShouldBe(0);
        result.Output.GetProperty("status").GetString().ShouldBe("Success");
    }
}
