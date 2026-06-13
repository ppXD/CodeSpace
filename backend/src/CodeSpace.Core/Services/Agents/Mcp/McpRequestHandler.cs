using System.Text.Json;
using CodeSpace.Core.Services.Agents.Tools;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Agents.Mcp;

namespace CodeSpace.Core.Services.Agents.Mcp;

/// <summary>
/// Maps one MCP JSON-RPC 2.0 request to its response over the <see cref="IAgentToolRegistry"/> — the protocol core
/// the (future) stdio transport pumps messages through. It handles exactly three methods: <c>initialize</c>
/// (capability handshake), <c>tools/list</c> (project the catalog as name/description/inputSchema), and
/// <c>tools/call</c> (resolve → validate → invoke → map result). A request with no <c>id</c> is a JSON-RPC
/// notification: the handler runs NOTHING and returns null (no reply). Protocol-level problems (malformed envelope,
/// unknown method, bad params) use the JSON-RPC error channel; a well-formed call whose TOOL fails comes back as a
/// normal result with <c>isError:true</c> so the model can read and retry. HandleAsync never throws except to
/// propagate cancellation.
///
/// <para><b>UNGATED — no approval enforcement in this slice.</b> The handler executes whatever tool the registry
/// resolves, UNCONDITIONALLY: it deliberately does NOT consult <see cref="IAgentTool.RequiresApproval"/> /
/// <see cref="IAgentTool.IsDestructive"/> (the approval/autonomy gate is a separate later slice). Nothing in
/// production constructs this handler yet — there is no transport and no DI registration. The slice that first
/// wires a transport or registers this in DI MUST add the approval gate first, or a connected model could invoke a
/// destructive tool (e.g. git.merge_pr, agent.run_command) with no approval. That same slice must also route caught
/// exception messages + tool error text through the secret redactor before they reach the model.</para>
/// </summary>
public sealed class McpRequestHandler : IMcpRequestHandler
{
    /// <summary>The MCP protocol revision this server speaks. Pinned (see the pin test) — a bump is a deliberate, visible decision.</summary>
    public const string ProtocolVersion = "2024-11-05";

    /// <summary>The advertised server name — becomes the <c>mcp__codespace__*</c> tool prefix the CLI harnesses apply, which the later staging slice's allow-list must match. Pinned; renaming is a cross-slice cost.</summary>
    public const string ServerName = "codespace";

    /// <summary>The advertised server version (informational, sent in the initialize handshake).</summary>
    public const string ServerVersion = "0.1.0";

    private static readonly JsonElement NullId = JsonDocument.Parse("null").RootElement.Clone();
    private static readonly JsonElement EmptyObject = JsonDocument.Parse("{}").RootElement.Clone();

    private readonly IAgentToolRegistry _registry;

    public McpRequestHandler(IAgentToolRegistry registry)
    {
        _registry = registry;
    }

    public async Task<JsonElement?> HandleAsync(JsonElement request, CancellationToken cancellationToken)
    {
        if (request.ValueKind != JsonValueKind.Object) return Serialize(JsonRpcResponse.Fail(NullId, Error(JsonRpcError.InvalidRequest, "Request must be a single JSON-RPC 2.0 object (batch arrays are not supported).")));

        if (!request.TryGetProperty("id", out var id)) return null;   // no id → notification → run nothing, send no reply

        if (!IsSupportedVersion(request)) return Serialize(JsonRpcResponse.Fail(id, Error(JsonRpcError.InvalidRequest, "Request 'jsonrpc' must be \"2.0\".")));

        if (!TryReadMethod(request, out var method)) return Serialize(JsonRpcResponse.Fail(id, Error(JsonRpcError.InvalidRequest, "Request 'method' is required.")));

        return method switch
        {
            "initialize" => Serialize(JsonRpcResponse.Ok(id, InitializeResult())),
            "tools/list" => Serialize(JsonRpcResponse.Ok(id, ToolsListResult())),
            "tools/call" => Serialize(await HandleToolCallAsync(id, request, cancellationToken).ConfigureAwait(false)),
            _ => Serialize(JsonRpcResponse.Fail(id, Error(JsonRpcError.MethodNotFound, $"Method not found: {method}"))),
        };
    }

    private async Task<JsonRpcResponse> HandleToolCallAsync(JsonElement id, JsonElement request, CancellationToken cancellationToken)
    {
        if (!request.TryGetProperty("params", out var prms) || prms.ValueKind != JsonValueKind.Object)
            return JsonRpcResponse.Fail(id, Error(JsonRpcError.InvalidParams, "tools/call requires a 'params' object."));

        if (!prms.TryGetProperty("name", out var nameEl) || nameEl.ValueKind != JsonValueKind.String)
            return JsonRpcResponse.Fail(id, Error(JsonRpcError.InvalidParams, "tools/call requires a string 'name'."));

        var name = nameEl.GetString()!;
        var tool = _registry.Resolve(name);

        if (tool == null) return JsonRpcResponse.Fail(id, Error(JsonRpcError.InvalidParams, $"Unknown tool '{name}'."));

        // Absent arguments default to {}; present-but-wrong-type arguments pass through verbatim so the tool's own
        // ValidateInput rejects them (a teachable tool-result error, not a silent coercion).
        var arguments = prms.TryGetProperty("arguments", out var a) ? a : EmptyObject;

        var validation = tool.ValidateInput(arguments);

        if (!validation.IsValid) return JsonRpcResponse.Ok(id, ToolResult(isError: true, validation.Error ?? "Invalid tool input."));

        return JsonRpcResponse.Ok(id, await InvokeToolAsync(tool, arguments, cancellationToken).ConfigureAwait(false));
    }

    private static async Task<JsonElement> InvokeToolAsync(IAgentTool tool, JsonElement arguments, CancellationToken cancellationToken)
    {
        try
        {
            var result = await tool.CallAsync(new AgentToolCall { Input = arguments }, cancellationToken).ConfigureAwait(false);

            return result.IsError
                ? ToolResult(isError: true, result.Error ?? "Tool failed.")
                : ToolResult(isError: false, OutputText(result.Output));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A thrown tool exception is surfaced to the MODEL as a tool failure (isError), not a JSON-RPC protocol
            // error — the request itself was well-formed. (Cancellation propagates, by the filter above.)
            return ToolResult(isError: true, ex.Message);
        }
    }

    private static bool IsSupportedVersion(JsonElement request) =>
        !request.TryGetProperty("jsonrpc", out var v) || (v.ValueKind == JsonValueKind.String && v.GetString() == "2.0");

    private static bool TryReadMethod(JsonElement request, out string method)
    {
        method = "";

        if (!request.TryGetProperty("method", out var m) || m.ValueKind != JsonValueKind.String) return false;

        method = m.GetString() ?? "";
        return method.Length > 0;
    }

    private static JsonElement InitializeResult() => JsonSerializer.SerializeToElement(new
    {
        protocolVersion = ProtocolVersion,
        capabilities = new { tools = new { } },
        serverInfo = new { name = ServerName, version = ServerVersion },
    }, AgentJson.Options);

    private JsonElement ToolsListResult()
    {
        var tools = _registry.All
            .Select(t => new { name = t.Kind, description = t.Description, inputSchema = t.InputSchema })
            .ToArray();

        return JsonSerializer.SerializeToElement(new { tools }, AgentJson.Options);
    }

    private static JsonElement ToolResult(bool isError, string text) => JsonSerializer.SerializeToElement(new
    {
        content = new[] { new { type = "text", text } },
        isError,
    }, AgentJson.Options);

    private static string OutputText(JsonElement output) => output.ValueKind == JsonValueKind.Undefined ? "{}" : output.GetRawText();

    private static JsonElement Serialize(JsonRpcResponse response) => JsonSerializer.SerializeToElement(response, AgentJson.Options);

    private static JsonRpcError Error(int code, string message) => new() { Code = code, Message = message };
}
