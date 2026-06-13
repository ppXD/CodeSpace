using System.Text.Json;
using CodeSpace.Core.Services.Agents.Mcp;
using CodeSpace.Core.Services.Agents.Tools;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Agents.Mcp;
using Shouldly;

namespace CodeSpace.UnitTests.Agents;

/// <summary>
/// Pins the MCP JSON-RPC protocol core: the initialize handshake, tools/list catalog projection, tools/call
/// resolve→validate→invoke→map, the two distinct failure planes (JSON-RPC protocol errors vs MCP isError tool
/// results), JSON-RPC notification semantics (no reply, no execution), and the autonomy gate on tools/call
/// (a gated tool is denied / requires approval per tier; read-only tools run at every tier).
/// </summary>
[Trait("Category", "Unit")]
public class McpRequestHandlerTests
{
    private const string Init = """{"jsonrpc":"2.0","id":1,"method":"initialize"}""";
    private const string ListReq = """{"jsonrpc":"2.0","id":1,"method":"tools/list"}""";

    private sealed class FakeTool : IAgentTool
    {
        public required string Kind { get; init; }
        public string Description { get; init; } = "desc";
        public JsonElement InputSchema { get; init; } = Parse("""{"type":"object","properties":{"x":{"type":"string"}}}""");
        public JsonElement OutputSchema { get; init; } = Parse("{}");
        public bool IsDestructiveOverride { get; init; }
        public bool IsDestructive => IsDestructiveOverride;
        public bool IsReadOnly => !IsDestructiveOverride;

        public Func<JsonElement, AgentToolValidation>? OnValidate { get; init; }
        public Func<AgentToolCall, CancellationToken, Task<AgentToolResult>>? OnCall { get; init; }
        public int CallCount { get; private set; }

        public AgentToolValidation ValidateInput(JsonElement input) => OnValidate?.Invoke(input) ?? AgentToolValidation.Valid;

        public Task<AgentToolResult> CallAsync(AgentToolCall call, CancellationToken cancellationToken)
        {
            CallCount++;
            return OnCall?.Invoke(call, cancellationToken) ?? Task.FromResult(AgentToolResult.Ok(Parse("""{"ok":true}"""), 11));
        }
    }

    private sealed class FakeRegistry : IAgentToolRegistry
    {
        private readonly IReadOnlyList<IAgentTool> _tools;
        public FakeRegistry(params IAgentTool[] tools) => _tools = tools.OrderBy(t => t.Kind, StringComparer.Ordinal).ToList();
        public IReadOnlyList<IAgentTool> All => _tools;
        public IAgentTool? Resolve(string kind) => _tools.FirstOrDefault(t => t.Kind == kind);
    }

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement.Clone();
    // Default to Unleashed so the protocol-focused tests see a transparent gate; gate-specific tests pass a tier.
    private static McpRequestHandler Handler(params IAgentTool[] tools) => Handler(AgentAutonomyLevel.Unleashed, tools);
    private static McpRequestHandler Handler(AgentAutonomyLevel autonomy, params IAgentTool[] tools) => new(new FakeRegistry(tools), autonomy);
    private static McpRequestHandler Handler(AgentAutonomyLevel autonomy, Guid? teamId, params IAgentTool[] tools) => new(new FakeRegistry(tools), autonomy, teamId);
    private static async Task<JsonElement> Respond(McpRequestHandler handler, string requestJson) => (await handler.HandleAsync(Parse(requestJson), CancellationToken.None))!.Value;
    private static string Call(string name, string argsJson) => $$$"""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"{{{name}}}","arguments":{{{argsJson}}}}}""";

    // ── Pins ────────────────────────────────────────────────────────────────

    [Fact]
    public void Protocol_constants_are_pinned()
    {
        // ServerName feeds the mcp__codespace__* tool prefix the later staging slice's allow-list must match — a
        // rename here is a cross-slice cost, so it is pinned as a deliberate decision (Rule 8 spirit).
        McpRequestHandler.ProtocolVersion.ShouldBe("2024-11-05");
        McpRequestHandler.ServerName.ShouldBe("codespace");
        McpRequestHandler.ServerVersion.ShouldBe("0.1.0");
    }

    [Fact]
    public void JsonRpcError_code_constants_are_pinned()
    {
        JsonRpcError.ParseError.ShouldBe(-32700);
        JsonRpcError.InvalidRequest.ShouldBe(-32600);
        JsonRpcError.MethodNotFound.ShouldBe(-32601);
        JsonRpcError.InvalidParams.ShouldBe(-32602);
        JsonRpcError.InternalError.ShouldBe(-32603);
    }

    // ── initialize ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Initialize_returns_pinned_protocol_version_and_tools_capability()
    {
        var result = (await Respond(Handler(), Init)).GetProperty("result");

        result.GetProperty("protocolVersion").GetString().ShouldBe("2024-11-05");
        result.GetProperty("capabilities").TryGetProperty("tools", out _).ShouldBeTrue();
    }

    [Theory]
    [InlineData("7")]
    [InlineData("\"abc\"")]
    public async Task Initialize_echoes_the_request_id_verbatim(string idLiteral)
    {
        var resp = await Respond(Handler(), $$"""{"jsonrpc":"2.0","id":{{idLiteral}},"method":"initialize"}""");

        resp.GetProperty("id").GetRawText().ShouldBe(idLiteral);
    }

    [Fact]
    public async Task Initialize_serverInfo_name_and_version_are_pinned()
    {
        var info = (await Respond(Handler(), Init)).GetProperty("result").GetProperty("serverInfo");

        info.GetProperty("name").GetString().ShouldBe("codespace");
        info.GetProperty("version").GetString().ShouldBe("0.1.0");
    }

    [Fact]
    public async Task Initialize_ignores_client_requested_protocol_version()
    {
        var resp = await Respond(Handler(), """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"1999-01-01"}}""");

        resp.GetProperty("result").GetProperty("protocolVersion").GetString().ShouldBe("2024-11-05");
    }

    // ── tools/list ──────────────────────────────────────────────────────────

    [Fact]
    public async Task ToolsList_projects_every_tool_to_name_description_inputSchema_verbatim()
    {
        var schema = Parse("""{"type":"object","properties":{"q":{"type":"string"}}}""");

        var tools = (await Respond(Handler(new FakeTool { Kind = "git.list_prs", Description = "List PRs", InputSchema = schema }), ListReq))
            .GetProperty("result").GetProperty("tools");

        tools.GetArrayLength().ShouldBe(1);
        tools[0].GetProperty("name").GetString().ShouldBe("git.list_prs");
        tools[0].GetProperty("description").GetString().ShouldBe("List PRs");
        tools[0].GetProperty("inputSchema").GetRawText().ShouldBe(schema.GetRawText());
    }

    [Fact]
    public async Task ToolsList_preserves_registry_ordinal_ordering()
    {
        var resp = await Respond(Handler(new FakeTool { Kind = "git.list_prs" }, new FakeTool { Kind = "agent.run_command" }, new FakeTool { Kind = "git.fetch_pr_diff" }), ListReq);

        var names = resp.GetProperty("result").GetProperty("tools").EnumerateArray().Select(t => t.GetProperty("name").GetString()).ToArray();

        names.ShouldBe(new[] { "agent.run_command", "git.fetch_pr_diff", "git.list_prs" });
    }

    [Fact]
    public async Task ToolsList_does_not_leak_outputSchema_or_risk_flags_or_aliases()
    {
        var tool = (await Respond(Handler(new FakeTool { Kind = "x" }), ListReq)).GetProperty("result").GetProperty("tools")[0];

        tool.TryGetProperty("outputSchema", out _).ShouldBeFalse();
        tool.TryGetProperty("isDestructive", out _).ShouldBeFalse();
        tool.TryGetProperty("isReadOnly", out _).ShouldBeFalse();
        tool.TryGetProperty("aliases", out _).ShouldBeFalse();
    }

    [Fact]
    public async Task ToolsList_on_empty_registry_returns_empty_array_not_null()
    {
        var tools = (await Respond(Handler(), ListReq)).GetProperty("result").GetProperty("tools");

        tools.ValueKind.ShouldBe(JsonValueKind.Array);
        tools.GetArrayLength().ShouldBe(0);
    }

    // ── tools/call (happy + tool-failure plane) ───────────────────────────────

    [Fact]
    public async Task ToolsCall_resolves_and_returns_success_content()
    {
        var tool = new FakeTool { Kind = "echo", OnCall = (_, _) => Task.FromResult(AgentToolResult.Ok(Parse("""{"echoed":42}"""), 13)) };

        var result = (await Respond(Handler(tool), Call("echo", """{"x":"hi"}"""))).GetProperty("result");

        result.GetProperty("isError").GetBoolean().ShouldBeFalse();
        result.GetProperty("content")[0].GetProperty("type").GetString().ShouldBe("text");
        result.GetProperty("content")[0].GetProperty("text").GetString().ShouldBe("""{"echoed":42}""");
        tool.CallCount.ShouldBe(1);
    }

    [Fact]
    public async Task ToolsCall_absent_arguments_defaults_to_empty_object_input()
    {
        JsonElement seen = default;
        var tool = new FakeTool { Kind = "echo", OnCall = (c, _) => { seen = c.Input; return Task.FromResult(AgentToolResult.Ok(Parse("{}"), 2)); } };

        await Respond(Handler(tool), """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"echo"}}""");

        seen.ValueKind.ShouldBe(JsonValueKind.Object);
        seen.GetRawText().ShouldBe("{}");
    }

    [Fact]
    public async Task ToolsCall_invalid_input_returns_isError_true_not_a_protocol_error()
    {
        var tool = new FakeTool { Kind = "echo", OnValidate = _ => AgentToolValidation.Invalid("must have q") };

        var resp = await Respond(Handler(tool), Call("echo", """{"x":1}"""));

        resp.TryGetProperty("error", out _).ShouldBeFalse("a validation failure is a tool result, not a JSON-RPC error");
        resp.GetProperty("result").GetProperty("isError").GetBoolean().ShouldBeTrue();
        resp.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString().ShouldBe("must have q");
        tool.CallCount.ShouldBe(0);
    }

    [Fact]
    public async Task ToolsCall_present_but_non_object_arguments_reach_validate_input()
    {
        // Mirrors NodeAgentTool.ValidateInput: a non-object input is rejected. The handler must pass it through
        // VERBATIM (not coerce to {}), so the tool sees the wrong-type value and returns a teachable isError.
        var tool = new FakeTool { Kind = "echo", OnValidate = input => input.ValueKind == JsonValueKind.Object ? AgentToolValidation.Valid : AgentToolValidation.Invalid("Tool input must be a JSON object.") };

        var resp = await Respond(Handler(tool), """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"echo","arguments":5}}""");

        resp.GetProperty("result").GetProperty("isError").GetBoolean().ShouldBeTrue();
        resp.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString().ShouldBe("Tool input must be a JSON object.");
    }

    [Fact]
    public async Task ToolsCall_tool_error_result_maps_to_isError_true_with_error_text()
    {
        var tool = new FakeTool { Kind = "boom", OnCall = (_, _) => Task.FromResult(AgentToolResult.Fail("clone failed")) };

        var result = (await Respond(Handler(tool), Call("boom", "{}"))).GetProperty("result");

        result.GetProperty("isError").GetBoolean().ShouldBeTrue();
        result.GetProperty("content")[0].GetProperty("text").GetString().ShouldBe("clone failed");
    }

    [Fact]
    public async Task ToolsCall_undefined_output_emits_empty_object_text_without_throwing()
    {
        // AgentToolResult with a default (Undefined) Output must not throw on GetRawText — OutputText guards it.
        var tool = new FakeTool { Kind = "weird", OnCall = (_, _) => Task.FromResult(new AgentToolResult { IsError = false }) };

        var result = (await Respond(Handler(tool), Call("weird", "{}"))).GetProperty("result");

        result.GetProperty("isError").GetBoolean().ShouldBeFalse();
        result.GetProperty("content")[0].GetProperty("text").GetString().ShouldBe("{}");
    }

    [Fact]
    public async Task ToolsCall_thrown_exception_is_caught_and_mapped_to_isError_true()
    {
        var tool = new FakeTool { Kind = "throws", OnCall = (_, _) => throw new InvalidOperationException("kaboom") };

        var resp = await Respond(Handler(tool), Call("throws", "{}"));

        resp.TryGetProperty("error", out _).ShouldBeFalse("a thrown tool exception is a tool failure, not a JSON-RPC error");
        resp.GetProperty("result").GetProperty("isError").GetBoolean().ShouldBeTrue();
        resp.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString().ShouldBe("kaboom");
    }

    [Fact]
    public async Task ToolsCall_cancellation_propagates_and_is_not_swallowed()
    {
        var tool = new FakeTool { Kind = "cancels", OnCall = (_, ct) => throw new OperationCanceledException(ct) };

        await Should.ThrowAsync<OperationCanceledException>(async () =>
            await Handler(tool).HandleAsync(Parse(Call("cancels", "{}")), new CancellationToken(canceled: true)));
    }

    // ── tools/call autonomy gate ──────────────────────────────────────────────

    [Fact]
    public async Task ToolsCall_at_Unleashed_runs_a_destructive_tool()
    {
        var tool = new FakeTool { Kind = "git.merge_pr", IsDestructiveOverride = true, OnCall = (_, _) => Task.FromResult(AgentToolResult.Ok(Parse("""{"merged":true}"""), 14)) };

        ((IAgentTool)tool).RequiresApproval.ShouldBeTrue("a destructive tool requires approval by the fabric's fail-closed default");

        var result = (await Respond(Handler(AgentAutonomyLevel.Unleashed, tool), Call("git.merge_pr", "{}"))).GetProperty("result");

        result.GetProperty("isError").GetBoolean().ShouldBeFalse();
        tool.CallCount.ShouldBe(1, "Unleashed runs a gated tool unattended");
    }

    [Fact]
    public async Task ToolsCall_at_Confined_denies_a_destructive_tool_without_running_it()
    {
        var tool = new FakeTool { Kind = "git.merge_pr", IsDestructiveOverride = true };

        var resp = await Respond(Handler(AgentAutonomyLevel.Confined, tool), Call("git.merge_pr", "{}"));

        resp.TryGetProperty("error", out _).ShouldBeFalse("a gate denial is a tool result, not a JSON-RPC error");
        resp.GetProperty("result").GetProperty("isError").GetBoolean().ShouldBeTrue();
        resp.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString().ShouldContain("not permitted");
        tool.CallCount.ShouldBe(0, "a denied tool must never execute");
    }

    [Theory]
    [InlineData(AgentAutonomyLevel.Standard)]
    [InlineData(AgentAutonomyLevel.Trusted)]
    public async Task ToolsCall_requires_approval_for_a_destructive_tool_without_running_it(AgentAutonomyLevel level)
    {
        var tool = new FakeTool { Kind = "git.merge_pr", IsDestructiveOverride = true };

        var resp = await Respond(Handler(level, tool), Call("git.merge_pr", "{}"));

        resp.TryGetProperty("error", out _).ShouldBeFalse();
        resp.GetProperty("result").GetProperty("isError").GetBoolean().ShouldBeTrue();
        resp.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString().ShouldContain("approval");
        tool.CallCount.ShouldBe(0, "a tool needing approval must not run until approved");
    }

    [Theory]
    [InlineData(AgentAutonomyLevel.Confined)]
    [InlineData(AgentAutonomyLevel.Standard)]
    [InlineData(AgentAutonomyLevel.Trusted)]
    [InlineData(AgentAutonomyLevel.Unleashed)]
    public async Task ToolsCall_runs_a_read_only_tool_at_every_tier(AgentAutonomyLevel level)
    {
        var tool = new FakeTool { Kind = "git.list_prs" };   // read-only → RequiresApproval false → ungated

        var result = (await Respond(Handler(level, tool), Call("git.list_prs", "{}"))).GetProperty("result");

        result.GetProperty("isError").GetBoolean().ShouldBeFalse();
        tool.CallCount.ShouldBe(1, "a read-only tool runs regardless of tier");
    }

    // ── tools/call team scope stamping ────────────────────────────────────────

    [Fact]
    public async Task ToolsCall_stamps_the_handlers_team_onto_the_tool_call()
    {
        var teamId = Guid.NewGuid();
        Guid? seen = null;
        var tool = new FakeTool { Kind = "echo", OnCall = (c, _) => { seen = c.TeamId; return Task.FromResult(AgentToolResult.Ok(Parse("{}"), 2)); } };

        await Respond(Handler(AgentAutonomyLevel.Unleashed, teamId, tool), Call("echo", "{}"));

        seen.ShouldBe(teamId, "the run's team must travel handler → AgentToolCall so a repo-touching tool resolves within it");
    }

    [Fact]
    public async Task ToolsCall_with_no_team_on_the_handler_stamps_null_preserving_the_fail_closed_default()
    {
        Guid? seen = Guid.NewGuid();   // sentinel: must be overwritten to null by the call
        var tool = new FakeTool { Kind = "echo", OnCall = (c, _) => { seen = c.TeamId; return Task.FromResult(AgentToolResult.Ok(Parse("{}"), 2)); } };

        await Respond(Handler(tool), Call("echo", "{}"));   // 3rd ctor arg omitted via the defaulted param

        seen.ShouldBeNull("no team on the handler → null TeamId → the tool's synthetic scope has no team_id (fail-closed)");
    }

    [Fact]
    public async Task A_gate_denied_tool_is_never_invoked_so_no_team_is_stamped()
    {
        // Composition-order pin: the gate short-circuits BEFORE the tool is invoked, so a denied (destructive,
        // Confined) tool never reaches CallAsync — no TeamId is ever stamped on it.
        var teamId = Guid.NewGuid();
        var tool = new FakeTool { Kind = "git.merge_pr", IsDestructiveOverride = true };

        var resp = await Respond(Handler(AgentAutonomyLevel.Confined, teamId, tool), Call("git.merge_pr", "{}"));

        resp.GetProperty("result").GetProperty("isError").GetBoolean().ShouldBeTrue();
        resp.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString().ShouldContain("not permitted");
        tool.CallCount.ShouldBe(0, "a denied tool must never execute, so it is never stamped/invoked");
    }

    // ── tools/call (protocol-error plane) ─────────────────────────────────────

    [Fact]
    public async Task ToolsCall_unknown_tool_returns_InvalidParams_echoing_id()
    {
        var resp = await Respond(Handler(new FakeTool { Kind = "known" }), Call("nope", "{}"));

        resp.GetProperty("error").GetProperty("code").GetInt32().ShouldBe(JsonRpcError.InvalidParams);
        resp.GetProperty("error").GetProperty("message").GetString().ShouldContain("nope");
        resp.GetProperty("id").GetRawText().ShouldBe("1");
    }

    [Theory]
    [InlineData("""{"jsonrpc":"2.0","id":1,"method":"tools/call"}""")]               // params absent
    [InlineData("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":5}""")]     // params not an object
    [InlineData("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{}}""")]    // name absent
    [InlineData("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":5}}""")] // name not a string
    public async Task ToolsCall_bad_params_return_InvalidParams(string requestJson)
    {
        var resp = await Respond(Handler(new FakeTool { Kind = "x" }), requestJson);

        resp.GetProperty("error").GetProperty("code").GetInt32().ShouldBe(JsonRpcError.InvalidParams);
    }

    // ── routing + envelope ────────────────────────────────────────────────────

    [Fact]
    public async Task Unknown_method_returns_MethodNotFound_with_method_name()
    {
        var resp = await Respond(Handler(), """{"jsonrpc":"2.0","id":1,"method":"resources/list"}""");

        resp.GetProperty("error").GetProperty("code").GetInt32().ShouldBe(JsonRpcError.MethodNotFound);
        resp.GetProperty("error").GetProperty("message").GetString().ShouldContain("resources/list");
    }

    [Theory]
    [InlineData("""{"jsonrpc":"2.0","id":1}""")]                       // method absent
    [InlineData("""{"jsonrpc":"2.0","id":1,"method":""}""")]           // method empty
    [InlineData("""{"jsonrpc":"2.0","id":1,"method":5}""")]            // method not a string
    public async Task Missing_or_empty_method_returns_InvalidRequest(string requestJson)
    {
        var resp = await Respond(Handler(), requestJson);

        resp.GetProperty("error").GetProperty("code").GetInt32().ShouldBe(JsonRpcError.InvalidRequest);
    }

    [Fact]
    public async Task JsonRpc_version_other_than_2_0_returns_InvalidRequest()
    {
        var resp = await Respond(Handler(), """{"jsonrpc":"1.0","id":1,"method":"initialize"}""");

        resp.GetProperty("error").GetProperty("code").GetInt32().ShouldBe(JsonRpcError.InvalidRequest);
    }

    [Fact]
    public async Task Batch_array_request_returns_InvalidRequest_with_null_id()
    {
        var resp = await Respond(Handler(), """[{"jsonrpc":"2.0","id":1,"method":"initialize"}]""");

        resp.GetProperty("error").GetProperty("code").GetInt32().ShouldBe(JsonRpcError.InvalidRequest);
        resp.GetProperty("id").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    // ── notification semantics (no reply, no execution) ───────────────────────

    [Fact]
    public async Task Notification_without_id_produces_no_response()
    {
        var result = await Handler().HandleAsync(Parse("""{"jsonrpc":"2.0","method":"notifications/initialized"}"""), CancellationToken.None);

        result.ShouldBeNull();
    }

    [Fact]
    public async Task A_request_without_id_never_executes_a_tool()
    {
        var tool = new FakeTool { Kind = "echo" };

        var result = await Handler(tool).HandleAsync(Parse("""{"jsonrpc":"2.0","method":"tools/call","params":{"name":"echo","arguments":{}}}"""), CancellationToken.None);

        result.ShouldBeNull("a no-id request is a notification — no reply");
        tool.CallCount.ShouldBe(0, "a notification must not trigger tool execution");
    }

    // ── invariants ────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("""{"jsonrpc":"2.0","id":1,"method":"initialize"}""")]
    [InlineData("""{"jsonrpc":"2.0","id":1,"method":"tools/list"}""")]
    [InlineData("""{"jsonrpc":"2.0","id":1,"method":"bogus"}""")]
    public async Task Response_always_has_jsonrpc_2_0_and_never_both_result_and_error(string requestJson)
    {
        var resp = await Respond(Handler(), requestJson);

        resp.GetProperty("jsonrpc").GetString().ShouldBe("2.0");
        (resp.TryGetProperty("result", out _) ^ resp.TryGetProperty("error", out _)).ShouldBeTrue("exactly one of result/error must be present");
    }

    [Fact]
    public async Task HandleAsync_never_throws_for_any_routed_input()
    {
        var handler = Handler(new FakeTool { Kind = "echo" });

        foreach (var req in new[]
        {
            Init,
            ListReq,
            """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"echo","arguments":{}}}""",
            """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"missing"}}""",
            """{"jsonrpc":"2.0","id":1,"method":"unknown"}""",
            """{"jsonrpc":"2.0","method":"notifications/initialized"}""",
            """[1,2,3]""",
            "{}",
        })
        {
            await Should.NotThrowAsync(async () => await handler.HandleAsync(Parse(req), CancellationToken.None));
        }
    }
}
