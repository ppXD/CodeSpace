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
/// <para>Tool calls are gated by the run's autonomy tier via <see cref="AgentToolGate"/>: a gated (destructive)
/// tool the tier does not permit comes back as a tool result with <c>isError:true</c> + a reason — never silently
/// run. Per-call TENANCY is enforced: the handler stamps the run's <c>teamId</c> onto every <c>AgentToolCall</c>,
/// which <c>NodeAgentTool</c> writes to the synthetic scope's <c>sys.team_id</c> so a repo-touching tool resolves
/// the run's tenant (a foreign repository id still fail-closes; a null team → no team → fail-closed). EVERY
/// tool-result text the model receives — success output, tool error, AND the caught-exception message — is run
/// through the run's <see cref="SecretRedactor"/> at the single <see cref="ToolResult"/> choke point, so an echoed
/// model key can never reach the model through a tool call.</para>
/// </summary>
public sealed class McpRequestHandler : IMcpRequestHandler
{
    /// <summary>The MCP protocol revision this server speaks. Pinned (see the pin test) — a bump is a deliberate, visible decision.</summary>
    public const string ProtocolVersion = "2024-11-05";

    /// <summary>The advertised server name — becomes the <c>mcp__codespace__*</c> tool prefix the CLI harnesses apply, which the later staging slice's allow-list must match. Pinned; renaming is a cross-slice cost.</summary>
    public const string ServerName = "codespace";

    /// <summary>The advertised server version (informational, sent in the initialize handshake).</summary>
    public const string ServerVersion = "0.1.0";

    /// <summary>
    /// Env flag that opts a run into tool governance (the ToolCallLedger: exactly-once + audit for side-effecting
    /// tools). Default-OFF, opt-in ("1"/"true"/"TRUE" only — mirrors <see cref="AgentRunExecutor.IsMcpEndpointEnabled"/>):
    /// flag-OFF writes NO ledger rows and the handler is byte-identical to its pre-governance behavior. Rule 8: pinned
    /// by a unit test, read in production only through <see cref="IsGovernanceEnabled"/>.
    /// </summary>
    public const string GovernanceEnabledEnvVar = "CODESPACE_AGENT_TOOL_GOVERNANCE_ENABLED";

    private static readonly JsonElement NullId = JsonDocument.Parse("null").RootElement.Clone();
    private static readonly JsonElement EmptyObject = JsonDocument.Parse("{}").RootElement.Clone();

    private readonly IAgentToolRegistry _registry;
    private readonly AgentAutonomyLevel _autonomy;
    private readonly Guid? _teamId;
    private readonly SecretRedactor _redactor;
    private readonly Guid _runId;
    private readonly IToolCallLedgerService? _ledger;
    private readonly long _fenceEpoch;
    private readonly bool _governanceEnabled;
    // The conversation a run posts its tool-approval cards into — carried through but UNUSED here; a later slice reads
    // it to route an approval card when a tool needs human sign-off (null = no approval surface, fails closed then).
    private readonly Guid? _approvalConversationId;

    public McpRequestHandler(IAgentToolRegistry registry, AgentAutonomyLevel autonomy, Guid? teamId = null, SecretRedactor? redactor = null, Guid runId = default, IToolCallLedgerService? ledger = null, long fenceEpoch = 0, bool governanceEnabled = false, Guid? approvalConversationId = null)
    {
        _registry = registry;
        _autonomy = autonomy;
        _teamId = teamId;
        _redactor = redactor ?? SecretRedactor.None;
        _runId = runId;
        _ledger = ledger;
        _fenceEpoch = fenceEpoch;
        _governanceEnabled = governanceEnabled;
        _approvalConversationId = approvalConversationId;
    }

    /// <summary>True ONLY for "1"/"true"/"TRUE" (trimmed); fail-closed default-OFF otherwise. Mirrors <see cref="AgentRunExecutor.IsMcpEndpointEnabled"/> exactly (Rule 8). Production reads governance opt-in through this single gate.</summary>
    public static bool IsGovernanceEnabled()
    {
        var raw = Environment.GetEnvironmentVariable(GovernanceEnabledEnvVar)?.Trim();

        return raw is "1" or "true" or "TRUE";
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

        // Authorize BEFORE validating input: a tool the run's autonomy tier won't permit is refused outright (a
        // tool result with isError:true, not a protocol error), so the model never sees input feedback for — or
        // runs — a tool it can't call. A denied/needs-approval verdict short-circuits here.
        var gate = AgentToolGate.Decide(_autonomy, tool.RequiresApproval);

        // This short-circuit is BEFORE the ledger branch, so a denial writes NO audit row in item C (the Denied ledger
        // status is unused for now) — denial-auditing is deferred to the later audit slice.
        if (gate != AgentToolGateDecision.Allow) return JsonRpcResponse.Ok(id, ToolResult(isError: true, GateMessage(gate, name)));

        // Absent arguments default to {}; present-but-wrong-type arguments pass through verbatim so the tool's own
        // ValidateInput rejects them (a teachable tool-result error, not a silent coercion).
        var arguments = prms.TryGetProperty("arguments", out var a) ? a : EmptyObject;

        var validation = tool.ValidateInput(arguments);

        if (!validation.IsValid) return JsonRpcResponse.Ok(id, ToolResult(isError: true, validation.Error ?? "Invalid tool input."));

        // Governance OFF / no ledger / a read-only tool / a null-team run → today's path exactly: invoke + return,
        // no ledger row. Read-only tools are NEVER tracked (no side effect to dedup, no exactly-once need, no audit
        // value). A null-team run is skipped too: the ledger row's team_id is NOT NULL (FK to team), so a governed
        // side-effecting tool on a teamless run would hit a 23503 FK violation on the hot path — skipping makes it
        // behave exactly as flag-OFF for that call (and NodeAgentTool's tenancy still fail-closes downstream).
        if (!_governanceEnabled || _ledger is null || tool.IsReadOnly || _teamId is null)
            return JsonRpcResponse.Ok(id, await InvokeToolAsync(tool, arguments, cancellationToken).ConfigureAwait(false));

        return JsonRpcResponse.Ok(id, await InvokeWithLedgerAsync(tool, arguments, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>
    /// The governed invocation of a SIDE-EFFECTING tool: derive the server-side key, claim the ledger row (INSERT-first
    /// dedup against the unique index), and either run the side effect exactly once + record the (already-redacted)
    /// terminal, or — on a duplicate — return the prior result WITHOUT re-running. The gate already let this through
    /// as Allow (a Deny/RequireApproval short-circuited before the ledger), so reaching here means "run it, once".
    /// </summary>
    private async Task<JsonElement> InvokeWithLedgerAsync(IAgentTool tool, JsonElement arguments, CancellationToken cancellationToken)
    {
        var teamId = _teamId!.Value;   // a null-team run was diverted to the flag-OFF path before reaching here (team_id is NOT NULL)

        var inputHash = ToolCallKey.InputHash(arguments);   // SERVER-derived — never read from the wire (no forgery surface)
        var key = ToolCallKey.For(tool.Kind, inputHash);

        var claim = await _ledger!.TryClaimAsync(_runId, teamId, tool.Kind, key, inputHash, _fenceEpoch, cancellationToken).ConfigureAwait(false);

        return claim.Outcome switch
        {
            ToolCallClaimOutcome.Duplicate => ReplayPriorResult(claim),   // exactly-once: the side effect already ran; replay the stored (already-redacted) result
            ToolCallClaimOutcome.InFlight => ToolResult(isError: true, "This tool call is already in progress; retry shortly."),
            _ => await ExecuteAndRecordAsync(tool, arguments, teamId, claim.LedgerId, cancellationToken).ConfigureAwait(false),
        };
    }

    /// <summary>
    /// Run the side effect once, build the ALREADY-REDACTED wire result (the single <see cref="ToolResult"/> choke
    /// point), persist that redacted payload to the ledger as the terminal (Succeeded with the verbatim wire result,
    /// or Failed with the redacted error), and return it. Redact-BEFORE-persist: the ledger row never holds a raw
    /// secret, and a later duplicate replays the exact bytes the model first saw.
    ///
    /// <para>Liveness: the claim INSERTed a Pending row. A tool call that is CANCELLED (timeout / endpoint teardown /
    /// harness disconnect) or that throws MUST NOT leave that row stranded Pending forever — the key is deterministic,
    /// so a re-call on the reattached run would otherwise hit InFlight indefinitely and the interrupted side effect
    /// could never be retried. So an interruption records a terminal Failed on a BEST-EFFORT basis BEFORE propagating
    /// (see <see cref="RecordInterruptedThenRethrow"/>). The only remaining stranded-Pending window is a hard crash
    /// (SIGKILL) between the Pending INSERT and the recovery write; that is recovered by a future Pending-row reaper —
    /// the item-C analogue of the run-level reconciler that recovers stranded Running runs — which is out of scope for
    /// this PR.</para>
    /// </summary>
    private async Task<JsonElement> ExecuteAndRecordAsync(IAgentTool tool, JsonElement arguments, Guid teamId, Guid ledgerId, CancellationToken cancellationToken)
    {
        try
        {
            var result = await tool.CallAsync(new AgentToolCall { Input = arguments, TeamId = _teamId }, cancellationToken).ConfigureAwait(false);

            if (result.IsError)
            {
                var errorText = _redactor.Redact(result.Error ?? "Tool failed.");

                return await RecordTerminalOrReplayAsync(teamId, ledgerId, ToolCallLedgerStatus.Failed, resultJson: null, errorText, ToolResult(isError: true, errorText), cancellationToken).ConfigureAwait(false);
            }

            var structured = DeclaresSchema(tool.OutputSchema) && result.Output.ValueKind != JsonValueKind.Undefined ? result.Output : (JsonElement?)null;
            var wire = ToolResult(isError: false, OutputText(result.Output), structured);   // the REDACTED wire result the model receives

            return await RecordTerminalOrReplayAsync(teamId, ledgerId, ToolCallLedgerStatus.Succeeded, resultJson: wire.GetRawText(), error: null, wire, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The call was interrupted (timeout / teardown / disconnect). Record a terminal Failed best-effort so the
            // Pending row is never stranded, then re-throw — cancellation must still propagate.
            await RecordInterruptedThenRethrow(teamId, ledgerId).ConfigureAwait(false);
            throw;
        }
        catch (Exception ex)
        {
            // A thrown tool exception is a tool failure (isError), not a protocol error. Persist the redacted message
            // as the terminal so a re-call dedups to it rather than re-running the (partially-applied) side effect.
            var errorText = _redactor.Redact(ex.Message);

            return await RecordTerminalOrReplayAsync(teamId, ledgerId, ToolCallLedgerStatus.Failed, resultJson: null, errorText, ToolResult(isError: true, errorText), cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Record the terminal and return <paramref name="wire"/> — but if the CAS is LOST (a concurrent transition won, or
    /// the row is already terminal: <see cref="ToolCallLedgerTransitionException"/>), the side effect has ALREADY
    /// committed, so we must NOT surface a JSON-RPC protocol error after the fact. Instead re-read the recorded terminal
    /// and REPLAY it (mirroring the Duplicate path). Unreachable under today's pure-C single-winner path; reachable once
    /// durable mid-turn HITL (item D) lands — fixed now so the side effect's outcome is never masked by a late protocol
    /// error.
    /// </summary>
    private async Task<JsonElement> RecordTerminalOrReplayAsync(Guid teamId, Guid ledgerId, ToolCallLedgerStatus status, string? resultJson, string? error, JsonElement wire, CancellationToken cancellationToken)
    {
        try
        {
            await _ledger!.RecordTerminalAsync(ledgerId, teamId, status, resultJson, error, cancellationToken).ConfigureAwait(false);

            return wire;
        }
        catch (ToolCallLedgerTransitionException)
        {
            return await ReplayRecordedTerminalAsync(teamId, ledgerId, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Re-read the (already-recorded) terminal row for this (run, ledger) and rebuild its wire result — a success replays the verbatim stored wire JSON, anything else rebuilds an isError result from its redacted error. Mirrors <see cref="ReplayPriorResult"/>.</summary>
    private async Task<JsonElement> ReplayRecordedTerminalAsync(Guid teamId, Guid ledgerId, CancellationToken cancellationToken)
    {
        var rows = await _ledger!.GetForRunAsync(_runId, teamId, cancellationToken).ConfigureAwait(false);
        var row = rows.FirstOrDefault(r => r.Id == ledgerId);

        return row is { Status: ToolCallLedgerStatus.Succeeded, ResultJson: { Length: > 0 } stored }
            ? JsonDocument.Parse(stored).RootElement.Clone()
            : ToolResult(isError: true, row?.Error ?? "This tool call previously failed.");
    }

    /// <summary>
    /// Best-effort terminal write for an interrupted call, under <see cref="CancellationToken.None"/> so cancellation
    /// can't skip it. SWALLOWS any failure of the recovery write (e.g. the scope is disposing during teardown) — if
    /// even this fails the row stays Pending and the future Pending-row reaper (out of scope, see
    /// <see cref="ExecuteAndRecordAsync"/>) catches it. NEVER throws from the recovery path (the caller re-throws the
    /// original cancellation).
    /// </summary>
    private async Task RecordInterruptedThenRethrow(Guid teamId, Guid ledgerId)
    {
        try
        {
            await _ledger!.RecordTerminalAsync(ledgerId, teamId, ToolCallLedgerStatus.Failed, resultJson: null, error: "tool call interrupted before completion; safe to retry", CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // Swallow: the row stays Pending and the future Pending-row reaper recovers it. Never throw from recovery.
        }
    }

    /// <summary>Reconstruct the wire result from the prior terminal row WITHOUT re-running the side effect: a success replays the verbatim (already-redacted) stored wire JSON; a failure/denial rebuilds an isError result from its redacted error.</summary>
    private JsonElement ReplayPriorResult(ToolCallClaim claim) =>
        claim.PriorStatus == ToolCallLedgerStatus.Succeeded && claim.PriorResultJson is { Length: > 0 } stored
            ? JsonDocument.Parse(stored).RootElement.Clone()
            : ToolResult(isError: true, claim.PriorError ?? "This tool call previously failed.");

    private async Task<JsonElement> InvokeToolAsync(IAgentTool tool, JsonElement arguments, CancellationToken cancellationToken)
    {
        try
        {
            var result = await tool.CallAsync(new AgentToolCall { Input = arguments, TeamId = _teamId }, cancellationToken).ConfigureAwait(false);

            if (result.IsError) return ToolResult(isError: true, result.Error ?? "Tool failed.");

            // A tool that DECLARES an outputSchema also returns structuredContent (the typed result) alongside the text
            // (kept for clients that don't read structured output) — per the MCP structured-output contract.
            var structured = DeclaresSchema(tool.OutputSchema) && result.Output.ValueKind != JsonValueKind.Undefined ? result.Output : (JsonElement?)null;

            return ToolResult(isError: false, OutputText(result.Output), structured);
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
        var tools = _registry.All.Select(ToolDescriptor).ToArray();

        return JsonSerializer.SerializeToElement(new { tools }, AgentJson.Options);
    }

    /// <summary>Project a tool to its MCP descriptor: name/description/inputSchema, plus outputSchema ONLY when the tool
    /// declares a meaningful one (an empty {} schema is omitted). Risk flags / aliases are deliberately NOT exposed.</summary>
    private static Dictionary<string, object> ToolDescriptor(IAgentTool tool)
    {
        var descriptor = new Dictionary<string, object> { ["name"] = tool.Kind, ["description"] = tool.Description, ["inputSchema"] = tool.InputSchema };

        if (DeclaresSchema(tool.OutputSchema)) descriptor["outputSchema"] = tool.OutputSchema;

        return descriptor;
    }

    /// <summary>A schema is "declared" when it's a non-empty object — the empty {} default a node carries when it has no output shape is treated as absent.</summary>
    private static bool DeclaresSchema(JsonElement schema) => schema.ValueKind == JsonValueKind.Object && schema.EnumerateObject().Any();

    // The SINGLE choke point for every tool-result text the model receives — success output, tool error, the caught
    // exception message, AND the gate/validation messages all flow through here. Redact at this one point so an
    // echoed model key (e.g. a run_command that prints an env var) can never reach the model through a tool call.
    private JsonElement ToolResult(bool isError, string text, JsonElement? structuredContent = null)
    {
        text = _redactor.Redact(text);

        var result = new Dictionary<string, object>
        {
            ["content"] = new[] { new { type = "text", text } },
            ["isError"] = isError,
        };

        // structuredContent carries the same secrets the text might, so redact it too (serialize → redact → reparse).
        if (structuredContent is { } structured) result["structuredContent"] = RedactStructured(structured);

        return JsonSerializer.SerializeToElement(result, AgentJson.Options);
    }

    private static string OutputText(JsonElement output) => output.ValueKind == JsonValueKind.Undefined ? "{}" : output.GetRawText();

    /// <summary>Redact secrets from a structured result by serializing → redacting → reparsing (Clone so it outlives the temp doc). Identity when the redactor is empty.</summary>
    private JsonElement RedactStructured(JsonElement structured)
    {
        if (_redactor.IsEmpty) return structured;

        using var doc = JsonDocument.Parse(_redactor.Redact(structured.GetRawText()));

        return doc.RootElement.Clone();
    }

    private static JsonElement Serialize(JsonRpcResponse response) => JsonSerializer.SerializeToElement(response, AgentJson.Options);

    private static JsonRpcError Error(int code, string message) => new() { Code = code, Message = message };

    private static string GateMessage(AgentToolGateDecision decision, string tool) => decision == AgentToolGateDecision.RequireApproval
        ? $"Tool '{tool}' requires human approval, which this run's autonomy level cannot grant on its own."
        : $"Tool '{tool}' is not permitted at this run's autonomy level.";
}
