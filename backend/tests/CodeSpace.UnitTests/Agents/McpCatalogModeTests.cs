using System.Text.Json;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Agents.Mcp;
using CodeSpace.Core.Services.Agents.Tools;
using CodeSpace.Messages.Agents;
using Shouldly;

namespace CodeSpace.UnitTests.Agents;

/// <summary>
/// Pins the read-only catalog mode: the per-run MCP endpoint opens for EVERY run, but a run that did NOT opt into the
/// side-effecting fabric serves ONLY read-only tools. Covers the mode resolution (<see cref="AgentRunExecutor.ResolveMcpCatalogMode"/>)
/// and the handler's enforcement of it at the two model-facing surfaces — tools/list (a side-effecting tool is not even
/// advertised) and tools/call (a side-effecting name is refused before the gate). Full mode is byte-identical to before.
/// </summary>
[Trait("Category", "Unit")]
public class McpCatalogModeTests
{
    private sealed class FakeTool : IAgentTool
    {
        public required string Kind { get; init; }
        public string Description => "desc";
        public JsonElement InputSchema { get; } = Parse("""{"type":"object"}""");
        public JsonElement OutputSchema { get; } = Parse("{}");
        public required bool ReadOnly { get; init; }
        public bool IsReadOnly => ReadOnly;
        public bool IsDestructive => !ReadOnly;
        public AgentToolValidation ValidateInput(JsonElement input) => AgentToolValidation.Valid;
        public Task<AgentToolResult> CallAsync(AgentToolCall call, CancellationToken ct) => Task.FromResult(AgentToolResult.Ok(Parse("""{"ok":true}"""), 11));
    }

    private sealed class FakeRegistry : IAgentToolRegistry
    {
        private readonly IReadOnlyList<IAgentTool> _tools;
        public FakeRegistry(params IAgentTool[] tools) => _tools = tools.OrderBy(t => t.Kind, StringComparer.Ordinal).ToList();
        public IReadOnlyList<IAgentTool> All => _tools;
        public IAgentTool? Resolve(string kind) => _tools.FirstOrDefault(t => t.Kind == kind);
    }

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static readonly IAgentTool Read = new FakeTool { Kind = "get_context", ReadOnly = true };
    private static readonly IAgentTool Write = new FakeTool { Kind = "git.open_pr", ReadOnly = false };

    // Unleashed so the side-effecting tool would otherwise be Allow — proving the mode (not the gate) is what hides it.
    private static McpRequestHandler Handler(McpCatalogMode mode) =>
        new(new FakeRegistry(Read, Write), AgentAutonomyLevel.Unleashed, catalogMode: mode);

    private static async Task<JsonElement> Respond(McpRequestHandler handler, string requestJson) => (await handler.HandleAsync(Parse(requestJson), CancellationToken.None))!.Value;
    private static string Call(string name) =>
        "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\",\"params\":{\"name\":\"" + name + "\",\"arguments\":{}}}";
    private static List<string> ToolNames(JsonElement listResponse) =>
        listResponse.GetProperty("result").GetProperty("tools").EnumerateArray().Select(t => t.GetProperty("name").GetString()!).ToList();

    // ─── mode resolution ──────────────────────────────────────────────────────

    [Theory]
    [InlineData(null, McpCatalogMode.Full)]       // the DEFAULT — no per-run choice → the committed default (full)
    [InlineData(true, McpCatalogMode.Full)]       // explicit opt-in → full
    [InlineData(false, McpCatalogMode.ReadOnly)]  // explicit opt-OUT → read-only
    public void ResolveMcpCatalogMode_takes_the_per_run_choice_else_the_committed_default(bool? perRunChoice, McpCatalogMode expected)
    {
        var task = new AgentTask { Goal = "g", Harness = "codex-cli", EnableMcpEndpoint = perRunChoice };

        AgentRunExecutor.ResolveMcpCatalogMode(task).ShouldBe(expected);
    }

    // ─── tools/list filtering ─────────────────────────────────────────────────

    [Fact]
    public async Task ReadOnly_lists_only_read_only_tools()
    {
        var names = ToolNames(await Respond(Handler(McpCatalogMode.ReadOnly), """{"jsonrpc":"2.0","id":1,"method":"tools/list"}"""));

        names.ShouldContain("get_context", customMessage: "the safe read is advertised by default");
        names.ShouldNotContain("git.open_pr", customMessage: "a side-effecting tool is not even advertised in read-only mode");
    }

    [Fact]
    public async Task Full_lists_every_tool_byte_identical()
    {
        var names = ToolNames(await Respond(Handler(McpCatalogMode.Full), """{"jsonrpc":"2.0","id":1,"method":"tools/list"}"""));

        names.ShouldBe(new[] { "get_context", "git.open_pr" });   // full mode serves the whole registry, as before
    }

    // ─── tools/call enforcement ───────────────────────────────────────────────

    [Fact]
    public async Task ReadOnly_serves_a_read_only_tool_call()
    {
        var result = (await Respond(Handler(McpCatalogMode.ReadOnly), Call("get_context"))).GetProperty("result");

        result.GetProperty("isError").GetBoolean().ShouldBeFalse("a read-only tool runs normally in read-only mode");
    }

    [Fact]
    public async Task ReadOnly_refuses_a_side_effecting_tool_call_before_the_gate()
    {
        var result = (await Respond(Handler(McpCatalogMode.ReadOnly), Call("git.open_pr"))).GetProperty("result");

        result.GetProperty("isError").GetBoolean().ShouldBeTrue("a side-effecting tool is refused in read-only mode even at Unleashed");
        result.GetProperty("content")[0].GetProperty("text").GetString().ShouldContain("read-only", customMessage: "the refusal explains the run serves only read-only tools");
    }

    [Fact]
    public async Task Full_serves_a_side_effecting_tool_call()
    {
        var result = (await Respond(Handler(McpCatalogMode.Full), Call("git.open_pr"))).GetProperty("result");

        result.GetProperty("isError").GetBoolean().ShouldBeFalse("full mode at Unleashed runs the side-effecting tool, as before");
    }
}
