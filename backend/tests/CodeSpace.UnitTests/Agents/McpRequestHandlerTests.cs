using System.Text.Json;
using CodeSpace.Core.Services.Agents;
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
    public async Task ToolsList_omits_an_empty_outputSchema_and_never_leaks_risk_flags_or_aliases()
    {
        // The default empty {} schema (a node with no declared output shape) is treated as absent — omitted.
        var tool = (await Respond(Handler(new FakeTool { Kind = "x" }), ListReq)).GetProperty("result").GetProperty("tools")[0];

        tool.TryGetProperty("outputSchema", out _).ShouldBeFalse();
        tool.TryGetProperty("isDestructive", out _).ShouldBeFalse();
        tool.TryGetProperty("isReadOnly", out _).ShouldBeFalse();
        tool.TryGetProperty("aliases", out _).ShouldBeFalse();
    }

    [Fact]
    public async Task ToolsList_projects_a_meaningful_outputSchema()
    {
        var schema = """{"type":"object","properties":{"number":{"type":"integer"}}}""";
        var tool = (await Respond(Handler(new FakeTool { Kind = "x", OutputSchema = Parse(schema) }), ListReq)).GetProperty("result").GetProperty("tools")[0];

        tool.TryGetProperty("outputSchema", out var projected).ShouldBeTrue();
        projected.GetProperty("properties").GetProperty("number").GetProperty("type").GetString().ShouldBe("integer");
    }

    [Fact]
    public async Task ToolsCall_returns_structuredContent_when_the_tool_declares_an_outputSchema()
    {
        var tool = new FakeTool
        {
            Kind = "echo",
            OutputSchema = Parse("""{"type":"object","properties":{"n":{"type":"integer"}}}"""),
            OnCall = (_, _) => Task.FromResult(AgentToolResult.Ok(Parse("""{"n":42}"""), 13)),
        };

        var result = (await Respond(Handler(tool), Call("echo", "{}"))).GetProperty("result");

        result.GetProperty("isError").GetBoolean().ShouldBeFalse();
        result.GetProperty("structuredContent").GetProperty("n").GetInt32().ShouldBe(42);
        result.GetProperty("content")[0].GetProperty("text").GetString().ShouldContain("42");   // text kept for clients that don't read structured output
    }

    [Fact]
    public async Task ToolsCall_omits_structuredContent_when_the_tool_declares_no_outputSchema()
    {
        // Default {} schema → text-only result (backward-compatible with a client that predates structured output).
        var result = (await Respond(Handler(new FakeTool { Kind = "echo" }), Call("echo", "{}"))).GetProperty("result");

        result.TryGetProperty("structuredContent", out _).ShouldBeFalse();
        result.GetProperty("content")[0].GetProperty("text").GetString().ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public async Task ToolsCall_redacts_secrets_in_structuredContent()
    {
        var tool = new FakeTool
        {
            Kind = "echo",
            OutputSchema = Parse("""{"type":"object","properties":{"leaked":{"type":"string"}}}"""),
            OnCall = (_, _) => Task.FromResult(AgentToolResult.Ok(Parse("""{"leaked":"SECRET-abc123"}"""), 13)),
        };
        var handler = new McpRequestHandler(new FakeRegistry(tool), AgentAutonomyLevel.Unleashed, null, new SecretRedactor(new[] { "SECRET-abc123" }));

        var result = (await Respond(handler, Call("echo", "{}"))).GetProperty("result");

        result.GetProperty("structuredContent").GetProperty("leaked").GetString().ShouldBe(SecretRedactor.Placeholder);
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

    // ── tools/call secret redaction ───────────────────────────────────────────

    private const string Secret = "SECRET-abc123";

    // A handler wired with the run's redactor: every tool-result text it returns must be masked at the single
    // ToolResult choke point. The redactor is the LAST positional ctor arg (defaulted), so an omitted one is the
    // no-op identity (the control test below proves it).
    private static McpRequestHandler RedactingHandler(params IAgentTool[] tools) =>
        new(new FakeRegistry(tools), AgentAutonomyLevel.Unleashed, Guid.NewGuid(), new SecretRedactor(new[] { Secret }));

    [Fact]
    public async Task ToolsCall_redacts_the_secret_from_SUCCESS_output()
    {
        // The biggest leak surface: a tool (e.g. run_command) whose successful output echoes an env var holding the
        // model key. The success path runs through ToolResult too, so the secret must be masked, not just on errors.
        var tool = new FakeTool { Kind = "run_command", OnCall = (_, _) => Task.FromResult(AgentToolResult.Ok(Parse($$"""{"stdout":"ANTHROPIC_API_KEY={{Secret}}"}"""), 40)) };

        var text = (await Respond(RedactingHandler(tool), Call("run_command", "{}"))).GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString();

        text.ShouldNotContain(Secret, customMessage: "a secret echoed in SUCCESS output must be redacted before reaching the model");
        text!.ShouldContain(SecretRedactor.Placeholder);
    }

    [Fact]
    public async Task ToolsCall_redacts_the_secret_from_an_isError_result()
    {
        var tool = new FakeTool { Kind = "boom", OnCall = (_, _) => Task.FromResult(AgentToolResult.Fail($"auth failed for {Secret}")) };

        var result = (await Respond(RedactingHandler(tool), Call("boom", "{}"))).GetProperty("result");

        result.GetProperty("isError").GetBoolean().ShouldBeTrue();
        var text = result.GetProperty("content")[0].GetProperty("text").GetString();
        text.ShouldNotContain(Secret);
        text!.ShouldContain(SecretRedactor.Placeholder);
    }

    [Fact]
    public async Task ToolsCall_redacts_the_secret_from_a_caught_exception_message()
    {
        var tool = new FakeTool { Kind = "throws", OnCall = (_, _) => throw new InvalidOperationException($"connecting with {Secret} failed") };

        var result = (await Respond(RedactingHandler(tool), Call("throws", "{}"))).GetProperty("result");

        result.GetProperty("isError").GetBoolean().ShouldBeTrue();
        var text = result.GetProperty("content")[0].GetProperty("text").GetString();
        text.ShouldNotContain(Secret);
        text!.ShouldContain(SecretRedactor.Placeholder);
    }

    [Fact]
    public async Task ToolsCall_with_no_redactor_returns_text_verbatim_identity_control()
    {
        // Control: the default ctor (no redactor → SecretRedactor.None) is the identity — a value that LOOKS like a
        // secret passes through untouched, proving the redaction in the tests above comes from the redactor, not a
        // coincidental transform of the choke point.
        var tool = new FakeTool { Kind = "echo", OnCall = (_, _) => Task.FromResult(AgentToolResult.Ok(Parse($$"""{"v":"{{Secret}}"}"""), 20)) };

        var text = (await Respond(Handler(tool), Call("echo", "{}"))).GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString();

        text.ShouldBe($$"""{"v":"{{Secret}}"}""", customMessage: "with no redactor the choke point is the identity — text is verbatim");
    }

    [Fact]
    public async Task A_gate_or_validation_message_carrying_no_secret_still_serializes_through_the_redactor()
    {
        // The gate/validation messages are our OWN strings (no secret), but they flow through the same redactor-bearing
        // ToolResult — confirm they still serialize cleanly and are unchanged.
        var denied = new FakeTool { Kind = "git.merge_pr", IsDestructiveOverride = true };
        var invalid = new FakeTool { Kind = "needs_q", OnValidate = _ => AgentToolValidation.Invalid("must have q") };

        var deniedText = (await Respond(new McpRequestHandler(new FakeRegistry(denied), AgentAutonomyLevel.Confined, Guid.NewGuid(), new SecretRedactor(new[] { Secret })), Call("git.merge_pr", "{}")))
            .GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString();
        deniedText.ShouldContain("not permitted");

        var invalidText = (await Respond(RedactingHandler(invalid), Call("needs_q", "{}")))
            .GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString();
        invalidText.ShouldBe("must have q");
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

    // ── tool governance (the ToolCallLedger wiring) ───────────────────────────

    [Fact]
    public void Governance_flag_env_var_literal_is_pinned()
    {
        // Renaming this silently turns governance off for an operator who enabled it via env (Rule 8). A bump must be
        // a deliberate, visible decision.
        McpRequestHandler.GovernanceEnabledEnvVar.ShouldBe("CODESPACE_AGENT_TOOL_GOVERNANCE_ENABLED");
    }

    /// <summary>A spy ledger: records every TryClaim/RecordTerminal call. Configurable claim outcome for the dedup test,
    /// a configurable terminal-record throw for the lost-CAS replay test, and a recorded-rows store the replay re-reads.</summary>
    private sealed class SpyLedger : IToolCallLedgerService
    {
        public List<(Guid RunId, Guid TeamId, string ToolKind, string Key, string InputHash, long Epoch)> Claims { get; } = new();
        public List<(Guid LedgerId, Guid TeamId, ToolCallLedgerStatus Status, string? ResultJson, string? Error)> Terminals { get; } = new();
        public Func<ToolCallClaim>? ClaimResult { get; init; }

        /// <summary>When set, RecordTerminalAsync throws this BEFORE recording — simulating a lost CAS / already-terminal row (FIX 2 replay path).</summary>
        public Func<ToolCallLedgerTransitionException>? OnRecordThrow { get; init; }

        /// <summary>Rows the replay path re-reads via GetForRunAsync after a lost CAS (keyed by the recorded terminal).</summary>
        public List<Core.Persistence.Entities.ToolCallLedger> Rows { get; } = new();

        public Task<ToolCallClaim> TryClaimAsync(Guid agentRunId, Guid teamId, string toolKind, string idempotencyKey, string inputHash, long fenceEpoch, CancellationToken ct)
        {
            Claims.Add((agentRunId, teamId, toolKind, idempotencyKey, inputHash, fenceEpoch));
            return Task.FromResult(ClaimResult?.Invoke() ?? ToolCallClaim.Proceed(Guid.NewGuid()));
        }

        public Task RecordTerminalAsync(Guid ledgerId, Guid teamId, ToolCallLedgerStatus status, string? resultJson, string? error, CancellationToken ct)
        {
            if (OnRecordThrow is { } make) throw make();

            Terminals.Add((ledgerId, teamId, status, resultJson, error));
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<Core.Persistence.Entities.ToolCallLedger>> GetForRunAsync(Guid agentRunId, Guid teamId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<Core.Persistence.Entities.ToolCallLedger>>(Rows);
    }

    private static McpRequestHandler GovernedHandler(SpyLedger ledger, bool governanceEnabled, params IAgentTool[] tools) =>
        new(new FakeRegistry(tools), AgentAutonomyLevel.Unleashed, Guid.NewGuid(), null, Guid.NewGuid(), ledger, fenceEpoch: 7, governanceEnabled: governanceEnabled);

    [Fact]
    public async Task Governance_OFF_writes_no_ledger_row_even_for_a_write_tool()
    {
        var ledger = new SpyLedger();
        var tool = new FakeTool { Kind = "git.merge_pr", IsDestructiveOverride = true };

        var result = (await Respond(GovernedHandler(ledger, governanceEnabled: false, tool), Call("git.merge_pr", "{}"))).GetProperty("result");

        result.GetProperty("isError").GetBoolean().ShouldBeFalse();
        tool.CallCount.ShouldBe(1, "flag-OFF runs the tool exactly as today");
        ledger.Claims.ShouldBeEmpty("flag-OFF must write NO ledger row — byte-identical to today");
        ledger.Terminals.ShouldBeEmpty();
    }

    [Fact]
    public async Task Governance_ON_skips_the_ledger_for_a_read_only_tool()
    {
        var ledger = new SpyLedger();
        var tool = new FakeTool { Kind = "git.list_prs" };   // read-only

        var result = (await Respond(GovernedHandler(ledger, governanceEnabled: true, tool), Call("git.list_prs", "{}"))).GetProperty("result");

        result.GetProperty("isError").GetBoolean().ShouldBeFalse();
        tool.CallCount.ShouldBe(1, "a read-only tool still runs");
        ledger.Claims.ShouldBeEmpty("a read-only tool is NEVER tracked — no side effect to dedup");
        ledger.Terminals.ShouldBeEmpty();
    }

    [Fact]
    public async Task Governance_ON_with_a_null_team_skips_the_ledger_for_a_write_tool()
    {
        // FIX 3: the ledger row's team_id is NOT NULL, so a governed write on a teamless run would hit an FK violation.
        // A null-team run is skipped exactly like a read-only tool — no claim, no terminal — and the tool still runs
        // (flag-OFF-equivalent for that call). Downstream NodeAgentTool tenancy still fail-closes on the null team.
        var ledger = new SpyLedger();
        var tool = new FakeTool { Kind = "git.merge_pr", IsDestructiveOverride = true };
        var handler = new McpRequestHandler(new FakeRegistry(tool), AgentAutonomyLevel.Unleashed, teamId: null, null, Guid.NewGuid(), ledger, fenceEpoch: 7, governanceEnabled: true);

        var result = (await Respond(handler, Call("git.merge_pr", "{}"))).GetProperty("result");

        result.GetProperty("isError").GetBoolean().ShouldBeFalse();
        tool.CallCount.ShouldBe(1, "a null-team governed run still runs the tool on the legacy path");
        ledger.Claims.ShouldBeEmpty("a null-team run is NEVER tracked — the FK-violating claim is never attempted");
        ledger.Terminals.ShouldBeEmpty();
    }

    [Fact]
    public async Task Governance_ON_claims_and_records_a_write_tool_with_the_server_derived_key()
    {
        var ledger = new SpyLedger();
        var tool = new FakeTool { Kind = "git.merge_pr", IsDestructiveOverride = true, OnCall = (_, _) => Task.FromResult(AgentToolResult.Ok(Parse("""{"merged":true}"""), 14)) };

        var result = (await Respond(GovernedHandler(ledger, governanceEnabled: true, tool), Call("git.merge_pr", """{"pr":7}"""))).GetProperty("result");

        result.GetProperty("isError").GetBoolean().ShouldBeFalse();
        tool.CallCount.ShouldBe(1, "a fresh claim (Proceed) runs the side effect once");

        var claim = ledger.Claims.ShouldHaveSingleItem();
        claim.ToolKind.ShouldBe("git.merge_pr");
        claim.Key.ShouldBe(ToolCallKey.For("git.merge_pr", ToolCallKey.InputHash(Parse("""{"pr":7}"""))), "the key is SERVER-derived from kind + canonical input, never the wire");
        claim.Epoch.ShouldBe(7, "the run's fence epoch is recorded on the ledger row");

        var terminal = ledger.Terminals.ShouldHaveSingleItem();
        terminal.Status.ShouldBe(ToolCallLedgerStatus.Succeeded);
        terminal.ResultJson.ShouldNotBeNull();
        terminal.ResultJson!.ShouldContain("merged");
    }

    [Fact]
    public async Task Governance_ON_records_a_failed_terminal_for_a_tool_error()
    {
        var ledger = new SpyLedger();
        var tool = new FakeTool { Kind = "git.merge_pr", IsDestructiveOverride = true, OnCall = (_, _) => Task.FromResult(AgentToolResult.Fail("merge conflict")) };

        var result = (await Respond(GovernedHandler(ledger, governanceEnabled: true, tool), Call("git.merge_pr", "{}"))).GetProperty("result");

        result.GetProperty("isError").GetBoolean().ShouldBeTrue();
        var terminal = ledger.Terminals.ShouldHaveSingleItem();
        terminal.Status.ShouldBe(ToolCallLedgerStatus.Failed);
        terminal.Error.ShouldBe("merge conflict");
        terminal.ResultJson.ShouldBeNull();
    }

    [Fact]
    public async Task Governance_ON_dedup_replays_the_prior_result_without_re_running_the_side_effect()
    {
        // The claim returns Duplicate (a prior terminal row for this run+key already exists): the handler must return
        // the stored result WITHOUT calling the tool again — exactly-once.
        var priorWire = """{"content":[{"type":"text","text":"{\"merged\":true}"}],"isError":false}""";
        var ledger = new SpyLedger { ClaimResult = () => ToolCallClaim.Duplicate(Guid.NewGuid(), ToolCallLedgerStatus.Succeeded, priorWire, null) };
        var tool = new FakeTool { Kind = "git.merge_pr", IsDestructiveOverride = true };

        var result = (await Respond(GovernedHandler(ledger, governanceEnabled: true, tool), Call("git.merge_pr", "{}"))).GetProperty("result");

        tool.CallCount.ShouldBe(0, "a duplicate MUST NOT re-run the side effect");
        ledger.Terminals.ShouldBeEmpty("a duplicate records no new terminal");
        result.GetProperty("isError").GetBoolean().ShouldBeFalse();
        result.GetProperty("content")[0].GetProperty("text").GetString().ShouldContain("merged", customMessage: "the prior stored result is replayed verbatim");
    }

    [Fact]
    public async Task Governance_ON_in_flight_returns_a_retry_message_without_running()
    {
        var ledger = new SpyLedger { ClaimResult = () => ToolCallClaim.InFlight(Guid.NewGuid()) };
        var tool = new FakeTool { Kind = "git.merge_pr", IsDestructiveOverride = true };

        var result = (await Respond(GovernedHandler(ledger, governanceEnabled: true, tool), Call("git.merge_pr", "{}"))).GetProperty("result");

        tool.CallCount.ShouldBe(0, "an in-flight call must not double-run");
        result.GetProperty("isError").GetBoolean().ShouldBeTrue();
        result.GetProperty("content")[0].GetProperty("text").GetString().ShouldContain("in progress");
    }

    [Fact]
    public async Task Governance_ON_lost_CAS_on_record_replays_the_recorded_terminal_not_a_protocol_error()
    {
        // FIX 2: the side effect ALREADY committed, then RecordTerminalAsync loses the CAS (a concurrent transition
        // won / the row is already terminal → ToolCallLedgerTransitionException). The handler must NOT surface a
        // JSON-RPC protocol error after the fact — it re-reads the recorded terminal and REPLAYS it (mirroring the
        // Duplicate path). Here the recorded terminal is a Succeeded row whose stored wire result we replay.
        var ledgerId = Guid.NewGuid();
        var storedWire = """{"content":[{"type":"text","text":"{\"merged\":true}"}],"isError":false}""";
        var ledger = new SpyLedger
        {
            ClaimResult = () => ToolCallClaim.Proceed(ledgerId),
            OnRecordThrow = () => new ToolCallLedgerTransitionException("a concurrent transition won the race"),
        };
        ledger.Rows.Add(new Core.Persistence.Entities.ToolCallLedger { Id = ledgerId, Status = ToolCallLedgerStatus.Succeeded, ResultJson = storedWire });

        var tool = new FakeTool { Kind = "git.merge_pr", IsDestructiveOverride = true, OnCall = (_, _) => Task.FromResult(AgentToolResult.Ok(Parse("""{"merged":true}"""), 14)) };

        var resp = await Respond(GovernedHandler(ledger, governanceEnabled: true, tool), Call("git.merge_pr", "{}"));

        resp.TryGetProperty("error", out _).ShouldBeFalse("a lost CAS AFTER the side effect committed must NOT surface a JSON-RPC protocol error");
        tool.CallCount.ShouldBe(1, "the side effect ran exactly once (the lost CAS is on the record, not the call)");
        resp.GetProperty("result").GetProperty("isError").GetBoolean().ShouldBeFalse();
        resp.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString().ShouldContain("merged", customMessage: "the recorded terminal is re-read and replayed verbatim");
    }

    // ── approvalConversationId carry-through (stored, UNUSED in this slice) ────

    [Fact]
    public async Task ApprovalConversationId_ctor_param_is_accepted_and_does_not_change_handler_behavior()
    {
        // D1: the handler carries the approval-conversation reference but nothing reads it yet — a handler built WITH it
        // must behave byte-identically to one built without it (same tool result, same single invocation).
        var conversationId = Guid.NewGuid();
        var tool = new FakeTool { Kind = "echo", OnCall = (_, _) => Task.FromResult(AgentToolResult.Ok(Parse("""{"echoed":42}"""), 13)) };
        var handler = new McpRequestHandler(new FakeRegistry(tool), AgentAutonomyLevel.Unleashed, null, null, default, null, 0, false, conversationId);

        var result = (await Respond(handler, Call("echo", """{"x":"hi"}"""))).GetProperty("result");

        result.GetProperty("isError").GetBoolean().ShouldBeFalse();
        result.GetProperty("content")[0].GetProperty("text").GetString().ShouldBe("""{"echoed":42}""");
        tool.CallCount.ShouldBe(1, "carrying an approval-conversation reference must not alter dispatch — it is stored, never read, in this slice");
    }

    [Fact]
    public async Task Governance_ON_redacts_before_persisting_the_ledger_result()
    {
        // Redact-before-persist: the result stored in the ledger must already be masked — the row is a leak surface.
        const string secret = "SECRET-abc123";
        var ledger = new SpyLedger();
        var tool = new FakeTool { Kind = "git.merge_pr", IsDestructiveOverride = true, OnCall = (_, _) => Task.FromResult(AgentToolResult.Ok(Parse($$"""{"stdout":"KEY={{secret}}"}"""), 20)) };
        var handler = new McpRequestHandler(new FakeRegistry(tool), AgentAutonomyLevel.Unleashed, Guid.NewGuid(), new SecretRedactor(new[] { secret }), Guid.NewGuid(), ledger, fenceEpoch: 1, governanceEnabled: true);

        await Respond(handler, Call("git.merge_pr", "{}"));

        var terminal = ledger.Terminals.ShouldHaveSingleItem();
        terminal.ResultJson!.ShouldNotContain(secret, customMessage: "the ledger must store the ALREADY-REDACTED result — no raw secret at rest");
        terminal.ResultJson!.ShouldContain(SecretRedactor.Placeholder);
    }
}
