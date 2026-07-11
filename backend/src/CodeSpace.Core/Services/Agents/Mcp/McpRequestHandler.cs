using System.Text.Json;
using CodeSpace.Core.Services.Agents.Tools;
using CodeSpace.Core.Services.Workflows.Nodes.Builtin;
using CodeSpace.Core.Services.Chat;
using CodeSpace.Core.Services.Chat.Interactions;
using CodeSpace.Core.Services.Decisions;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Agents.Mcp;
using CodeSpace.Messages.Decisions;
using CodeSpace.Messages.Dtos.Chat.Interactions;

namespace CodeSpace.Core.Services.Agents.Mcp;

/// <summary>
/// Maps one MCP JSON-RPC 2.0 request to its response over the <see cref="IAgentToolRegistry"/> — the protocol core
/// the (future) stdio transport pumps messages through. It handles exactly three methods: <c>initialize</c>
/// (capability handshake), <c>tools/list</c> (project the catalog as name/description/inputSchema), and
/// <c>tools/call</c> (resolve → validate → invoke → map result). A request with no <c>id</c> is a JSON-RPC
/// notification: the handler runs NOTHING and returns null (no reply). Protocol-level problems (malformed envelope,
/// unknown method, bad params) use the JSON-RPC error channel; a well-formed call whose TOOL fails comes back as a
/// normal result with <c>isError:true</c> so the model can read and retry. HandleAsync never throws except to
/// propagate cancellation — the governance/approval path does DB writes + a chat-bot card post OUTSIDE the per-call
/// tool try/catch, so <see cref="DispatchToolCallAsync"/> wraps the whole <c>tools/call</c> dispatch in a defensive
/// catch that degrades a transient DB/chat fault to a retryable <c>isError</c> result rather than dropping the
/// connection (NOT fail-open: a throw means the claim/approval gate never passed, so no side effect runs ungoverned).
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

    /// <summary>
    /// Env override for how long a side-effecting tool call BLOCKS awaiting a human approval before it returns the
    /// pending-ticket (item D2). The actual block is <c>min(this, well-under-run-timeout)</c>; this is the operator
    /// ceiling + the test seam (an integration test sets it tiny to exercise the timeout without a real wait). Rule 8:
    /// pinned by a unit test, read only through <see cref="ApprovalBoundSeconds"/>; a non-positive / unparseable value
    /// falls back to <see cref="DefaultApprovalBoundSeconds"/>.
    /// </summary>
    public const string ApprovalBoundSecondsEnvVar = "CODESPACE_AGENT_TOOL_APPROVAL_BOUND_SECONDS";

    /// <summary>The default bounded-block window (10 minutes) when the env override is unset. A real CLI tolerates a multi-minute synchronous tools/call; past this the call returns the pending-ticket so the turn never hangs forever.</summary>
    public const int DefaultApprovalBoundSeconds = 600;

    /// <summary>The approval card's two button keys. The resolver (<see cref="IToolCallApprovalResolver"/>) only ever acts on these two; both resolve the wait (first-wins) — reject fails the call, approve stamps the decision for the handler to execute.</summary>
    private const string ApproveKey = "approve";
    private const string RejectKey = "reject";

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
    // The conversation a run posts its tool-approval cards into (item D1). When non-null + governance + ledger + a team
    // are all present, a RequireApproval verdict on a side-effecting tool posts an approval card here and BLOCKS the
    // synchronous call until a human decides (item D2). Null = no approval surface → fail closed (the flat refusal).
    private readonly Guid? _approvalConversationId;
    // D2 approval-flow collaborators — all defaulted-null so flag-OFF + every existing call site is unaffected. They
    // are resolved per-connection by the endpoint only when governance is on; the approval path uses all three.
    private readonly IChatBotService? _bot;
    private readonly IToolApprovalWaiterRegistry? _waiters;
    private readonly IInteractionComponentRegistry? _components;
    // Which slice of the catalog this connection serves. The endpoint opens for every run; ReadOnly (the default for a
    // run that did NOT opt into the side-effecting fabric) serves only read-only tools — they are the only ones listed,
    // allow-listed, and callable. Full (the existing opt-in) serves the whole registry, byte-identical to before.
    private readonly McpCatalogMode _catalogMode;

    public McpRequestHandler(IAgentToolRegistry registry, AgentAutonomyLevel autonomy, Guid? teamId = null, SecretRedactor? redactor = null, Guid runId = default, IToolCallLedgerService? ledger = null, long fenceEpoch = 0, bool governanceEnabled = false, Guid? approvalConversationId = null, IChatBotService? bot = null, IToolApprovalWaiterRegistry? waiters = null, IInteractionComponentRegistry? components = null, McpCatalogMode catalogMode = McpCatalogMode.Full)
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
        _bot = bot;
        _waiters = waiters;
        _components = components;
        _catalogMode = catalogMode;
    }

    /// <summary>True when this run's catalog mode serves <paramref name="tool"/>: Full serves the whole registry; ReadOnly serves only read-only tools. The ONE predicate every catalog surface (tools/list, tools/call resolve, the allow-list) consults so they agree by construction.</summary>
    private bool Serves(IAgentTool tool) => _catalogMode == McpCatalogMode.Full || tool.IsReadOnly;

    /// <summary>The effective bounded-block window (seconds): the env override when positive + parseable, else <see cref="DefaultApprovalBoundSeconds"/> (Rule 8 — read only here).</summary>
    public static int ApprovalBoundSeconds()
    {
        var raw = Environment.GetEnvironmentVariable(ApprovalBoundSecondsEnvVar)?.Trim();

        return int.TryParse(raw, out var seconds) && seconds > 0 ? seconds : DefaultApprovalBoundSeconds;
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
            "tools/call" => Serialize(await DispatchToolCallAsync(id, request, cancellationToken).ConfigureAwait(false)),
            _ => Serialize(JsonRpcResponse.Fail(id, Error(JsonRpcError.MethodNotFound, $"Method not found: {method}"))),
        };
    }

    /// <summary>
    /// The defensive boundary around the tools/call path: the governance + durable-approval flow performs DB writes
    /// (the ledger claim / approval CAS chain / state reads) and a chat-bot card post OUTSIDE the per-call
    /// <c>tool.CallAsync</c> try/catch. A transient Postgres/chat fault on any of those would otherwise escape
    /// <see cref="HandleAsync"/> (violating its "never throws except cancellation" contract), propagate out of the
    /// framing loop, and drop the run's MCP connection for the rest of the turn. We degrade it instead to a RETRYABLE
    /// tool result (isError) the model can re-issue — NOT fail-open (no side effect runs ungoverned: a throw means the
    /// claim/approval gate never passed) and the visible-degradation posture is preserved. Cancellation still
    /// propagates. The message routes through the <see cref="ToolResult"/> redactor choke point.
    /// </summary>
    private async Task<JsonRpcResponse> DispatchToolCallAsync(JsonElement id, JsonElement request, CancellationToken cancellationToken)
    {
        try
        {
            return await HandleToolCallAsync(id, request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return JsonRpcResponse.Ok(id, ToolResult(isError: true, "This tool call could not be governed right now; retry shortly."));
        }
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

        // A side-effecting tool is not part of a ReadOnly run's catalog (it is absent from tools/list too) — refuse it
        // at call time so a stale/guessed name can't reach the gate or a side effect. Fail-closed, before the gate.
        if (!Serves(tool)) return JsonRpcResponse.Ok(id, ToolResult(isError: true, $"Tool '{name}' is not available: this run serves only read-only tools. The side-effecting tool fabric is opt-in."));

        // decision.request is an ASK, not a gated side effect — intercept it BEFORE the autonomy gate (a Confined tier
        // must never DENY a question) and drive the durable decision flow on the SAME tool-ledger spine the approval
        // flow uses, generalized from binary approve/reject to typed options. The tool is only in the registry when
        // governance is on (DI-gated), so a governance-OFF run never reaches here (Resolve returned null → Unknown).
        if (string.Equals(name, DecisionRequestTool.ToolKind, StringComparison.Ordinal))
        {
            var decisionArgs = prms.TryGetProperty("arguments", out var da) ? da : EmptyObject;

            return JsonRpcResponse.Ok(id, await RunDecisionFlowAsync(tool, decisionArgs, cancellationToken).ConfigureAwait(false));
        }

        // Authorize BEFORE validating input: a tool the run's autonomy tier won't permit is refused outright (a
        // tool result with isError:true, not a protocol error), so the model never sees input feedback for — or
        // runs — a tool it can't call. A Deny short-circuits here; a RequireApproval that can't be served (no approval
        // surface) ALSO short-circuits to the same flat refusal — the conversation-less-run safety (fail-closed).
        var gate = AgentToolGate.Decide(_autonomy, tool.RequiresApproval, tool.AlwaysRequiresApproval);

        // B3a: a DANGEROUS shell command tightens the gate — even an Unleashed run (the only tier where run_command is
        // Allow) must get a human to approve `rm -rf /`, a pipe-to-shell, `sudo`, a force-push, etc. before it runs.
        // Command-aware (safe commands keep auto-running); only ever escalates Allow→RequireApproval (never relaxes),
        // and only for agent.run_command. The sandbox bounds the blast radius; this is the human checkpoint H7 wanted.
        if (gate == AgentToolGateDecision.Allow
            && string.Equals(name, AgentRunCommandNode.NodeTypeKey, StringComparison.Ordinal)
            && CommandRiskClassifier.IsDangerous(ReadRunCommandLine(prms)))
            gate = AgentToolGateDecision.RequireApproval;

        if (gate == AgentToolGateDecision.Deny) return JsonRpcResponse.Ok(id, ToolResult(isError: true, GateMessage(gate, name)));

        if (gate == AgentToolGateDecision.RequireApproval && !await CanServeApprovalAsync(tool, cancellationToken).ConfigureAwait(false))
            return JsonRpcResponse.Ok(id, ToolResult(isError: true, GateMessage(gate, name)));

        // Absent arguments default to {}; present-but-wrong-type arguments pass through verbatim so the tool's own
        // ValidateInput rejects them (a teachable tool-result error, not a silent coercion).
        var arguments = prms.TryGetProperty("arguments", out var a) ? a : EmptyObject;

        var validation = tool.ValidateInput(arguments);

        if (!validation.IsValid) return JsonRpcResponse.Ok(id, ToolResult(isError: true, validation.Error ?? "Invalid tool input."));

        // RequireApproval + a servable approval surface → park the call: record AwaitingApproval, post the card, and
        // BLOCK until a human decides (or the bound elapses → pending-ticket). The side effect runs through the SAME
        // exactly-once ledger path the Allow case uses, gated on the approval decision (see RunApprovalFlowAsync).
        if (gate == AgentToolGateDecision.RequireApproval)
            return JsonRpcResponse.Ok(id, await RunApprovalFlowAsync(tool, name, arguments, cancellationToken).ConfigureAwait(false));

        // Governance OFF / no ledger / a read-only tool / a null-team run → today's path exactly: invoke + return,
        // no ledger row. Read-only tools are NEVER tracked (no side effect to dedup, no exactly-once need, no audit
        // value). A null-team run is skipped too: the ledger row's team_id is NOT NULL (FK to team), so a governed
        // side-effecting tool on a teamless run would hit a 23503 FK violation on the hot path — skipping makes it
        // behave exactly as flag-OFF for that call (and NodeAgentTool's tenancy still fail-closes downstream).
        if (!_governanceEnabled || _ledger is null || tool.IsReadOnly || _teamId is null)
            return JsonRpcResponse.Ok(id, await InvokeToolAsync(tool, arguments, cancellationToken).ConfigureAwait(false));

        return JsonRpcResponse.Ok(id, await InvokeWithLedgerAsync(tool, arguments, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>The full command line an <c>agent.run_command</c> call would run — the executable joined with its args — for the B3a risk classifier. A shell wrapper (<c>sh -c "…"</c>) carries its inner command in the args, so the joined text catches it too. Empty when absent / malformed.</summary>
    private static string ReadRunCommandLine(JsonElement prms)
    {
        if (!prms.TryGetProperty("arguments", out var args) || args.ValueKind != JsonValueKind.Object) return "";

        var command = args.TryGetProperty("command", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() ?? "" : "";

        var parts = new List<string> { command };
        if (args.TryGetProperty("args", out var argv) && argv.ValueKind == JsonValueKind.Array)
            parts.AddRange(argv.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString() ?? ""));

        return string.Join(' ', parts);
    }

    /// <summary>
    /// Whether a <c>RequireApproval</c> verdict can actually be SERVED as a durable approval (item D2) rather than
    /// fail-closing to the flat refusal. First the cheap surface check (<see cref="HasApprovalSurface"/>): governance
    /// on, the ledger + card-post + waiter + component-registry collaborators all present, a non-null approval
    /// conversation, a team, AND a side-effecting tool. Then the TENANCY check — the approval conversation must belong
    /// to the run's team: the conversation id is unvalidated node config, and <see cref="IChatBotService.PostAsBotAsync"/>
    /// derives the team FROM the conversation, so a cross-team / unknown id would post the card into (and auto-join the
    /// bot to) a FOREIGN team's chat. A failed tenancy check fail-closes EXACTLY like the conversation-less run (no card,
    /// no block, flat refusal). The DB read is gated behind the surface check, so flag-OFF / no-surface paths never hit it.
    /// </summary>
    private async Task<bool> CanServeApprovalAsync(IAgentTool tool, CancellationToken cancellationToken)
    {
        if (!HasApprovalSurface(tool)) return false;

        return await _bot!.ConversationBelongsToTeamAsync(_approvalConversationId!.Value, _teamId!.Value, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The cheap, synchronous half of <see cref="CanServeApprovalAsync"/>: every collaborator + conversation + team is present and the tool side-effects. ANY missing piece → the call refuses EXACTLY as pre-D2 (no DB read, no block, no card).</summary>
    private bool HasApprovalSurface(IAgentTool tool) =>
        _governanceEnabled && _ledger is not null && _bot is not null && _waiters is not null && _components is not null
        && _approvalConversationId is not null && _teamId is not null && !tool.IsReadOnly;

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
    /// The durable mid-turn HITL approval flow for a side-effecting tool the tier requires sign-off on (item D2). The
    /// claim arbitrates exactly-once across the blocked-call wake AND a model re-call (the deterministic key): a fresh
    /// claim parks + posts + blocks; a re-claim of the same parked row re-binds to its decision (no second card); a
    /// terminal duplicate replays. The side effect ALWAYS runs behind the single-winner AwaitingApproval→Running
    /// execution claim in <see cref="ClaimThenExecuteAsync"/> (BEFORE <c>tool.CallAsync</c>), so of any number of
    /// executors that reach an approved row exactly one runs it once and every other replays — no double side effect.
    /// </summary>
    private async Task<JsonElement> RunApprovalFlowAsync(IAgentTool tool, string name, JsonElement arguments, CancellationToken cancellationToken)
    {
        var teamId = _teamId!.Value;   // CanServeApprovalAsync already proved team + ledger + collaborators non-null AND the conversation is the run's team's

        var inputHash = ToolCallKey.InputHash(arguments);   // SERVER-derived — never read from the wire
        var key = ToolCallKey.For(tool.Kind, inputHash);

        var claim = await _ledger!.TryClaimAsync(_runId, teamId, tool.Kind, key, inputHash, _fenceEpoch, cancellationToken).ConfigureAwait(false);

        return claim.Outcome switch
        {
            ToolCallClaimOutcome.Duplicate => ReplayPriorResult(claim),                                            // already resolved — replay (approved+executed, rejected, or expired)
            ToolCallClaimOutcome.InFlight => await ResumeOrTicketAsync(tool, arguments, teamId, claim.LedgerId, cancellationToken).ConfigureAwait(false),   // a re-call of a still-parked row — never re-post the card
            _ => await ParkForApprovalAsync(tool, name, arguments, teamId, claim.LedgerId, cancellationToken).ConfigureAwait(false),                        // fresh claim — park + post + block
        };
    }

    /// <summary>
    /// A FRESH claim's park: CAS Pending → AwaitingApproval (stamping token + deadline), post the redacted approval
    /// card (stamping its message id), then BLOCK on the bounded wait. If the CAS is lost (a concurrent path already
    /// parked or terminated the row), DON'T post — re-bind to whatever the row became (the no-second-card guard).
    /// </summary>
    private async Task<JsonElement> ParkForApprovalAsync(IAgentTool tool, string name, JsonElement arguments, Guid teamId, Guid ledgerId, CancellationToken cancellationToken)
    {
        var token = Guid.NewGuid().ToString("N");
        var deadlineAt = DateTimeOffset.UtcNow.AddSeconds(ApprovalBoundSeconds());

        var parked = await _ledger!.TryBeginApprovalAsync(ledgerId, teamId, token, deadlineAt, cancellationToken).ConfigureAwait(false);

        if (!parked) return await ResumeOrTicketAsync(tool, arguments, teamId, ledgerId, cancellationToken).ConfigureAwait(false);

        var messageId = await PostApprovalCardAsync(tool, name, token, cancellationToken).ConfigureAwait(false);

        await _ledger.SetApprovalMessageAsync(ledgerId, teamId, messageId, cancellationToken).ConfigureAwait(false);

        return await BlockForDecisionAsync(tool, arguments, teamId, ledgerId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Re-bind to an already-parked (or just-terminated) row WITHOUT posting a second card (§5 — one card per (run,
    /// key)). Re-reads the durable row (the authority): a terminal row replays; an AwaitingApproval row that's already
    /// approved runs the side effect once; a still-undecided one blocks again on a freshly-registered waiter. The card
    /// was posted on the first park, so the existing waiter/deadline still drives it.
    /// </summary>
    private async Task<JsonElement> ResumeOrTicketAsync(IAgentTool tool, JsonElement arguments, Guid teamId, Guid ledgerId, CancellationToken cancellationToken)
    {
        var state = await _ledger!.ReadApprovalStateAsync(ledgerId, teamId, cancellationToken).ConfigureAwait(false);

        if (state is null) return ToolResult(isError: true, "This tool call's approval record is missing.");

        if (ToolCallLedgerStateMachine.IsTerminal(state.Status)) return ReplayTerminalState(state);

        if (state.ApprovedAt is not null) return await ClaimThenExecuteAsync(tool, arguments, teamId, ledgerId, cancellationToken).ConfigureAwait(false);

        return await BlockForDecisionAsync(tool, arguments, teamId, ledgerId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The exactly-once-after-approve gate: claim the APPROVED row for execution (single-winner CAS AwaitingApproval →
    /// Running) BEFORE running the side effect. ONLY the winner runs <see cref="ExecuteAndRecordAsync"/> (one
    /// <c>tool.CallAsync</c>); a concurrent executor that LOST the claim (the row is already Running or terminal) must
    /// NOT re-run the side effect — it re-reads the durable row and replays its terminal (or, if the winner hasn't
    /// recorded the terminal yet, returns the in-flight retry message). This closes the pre-terminal-CAS double-run
    /// window: two executors that both read <c>ApprovedAt != null</c> race here, not at <c>tool.CallAsync</c>.
    /// </summary>
    private async Task<JsonElement> ClaimThenExecuteAsync(IAgentTool tool, JsonElement arguments, Guid teamId, Guid ledgerId, CancellationToken cancellationToken)
    {
        var won = await _ledger!.TryBeginExecutionAsync(ledgerId, teamId, cancellationToken).ConfigureAwait(false);

        if (won) return await ExecuteAndRecordAsync(tool, arguments, teamId, ledgerId, cancellationToken).ConfigureAwait(false);

        return await ReplayClaimedElsewhereAsync(teamId, ledgerId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The loser of the execution claim: the winner already moved the row to Running (running the side effect now) or
    /// to a terminal. Re-read the durable row (the authority): a terminal replays its stored result; a still-Running row
    /// means the winner's side effect is in flight — return the in-flight retry message so the loser never re-runs it.
    /// </summary>
    private async Task<JsonElement> ReplayClaimedElsewhereAsync(Guid teamId, Guid ledgerId, CancellationToken cancellationToken)
    {
        var state = await _ledger!.ReadApprovalStateAsync(ledgerId, teamId, cancellationToken).ConfigureAwait(false);

        if (state is null) return ToolResult(isError: true, "This tool call's approval record is missing.");

        if (ToolCallLedgerStateMachine.IsTerminal(state.Status)) return ReplayTerminalState(state);

        return ToolResult(isError: true, "This tool call is already in progress; retry shortly.");
    }

    /// <summary>
    /// BLOCK the synchronous tools/call until a decision lands or the bound elapses. The in-memory waiter is a latency
    /// fast-path; the durable row is the authority, so we ALWAYS re-read it after the wake. A linked CTS cancels the
    /// leftover delay/waiter once one completes; the waiter is ALWAYS removed in finally. On ct cancellation during the
    /// block we let it propagate — the row stays AwaitingApproval for the reaper / a reattach to resume (NOT stranded
    /// Pending). The bound is capped under nothing here beyond the env ceiling (the run's own TimeoutSeconds cancels ct,
    /// which we honor by propagating).
    /// </summary>
    private async Task<JsonElement> BlockForDecisionAsync(IAgentTool tool, JsonElement arguments, Guid teamId, Guid ledgerId, CancellationToken cancellationToken)
    {
        var waiter = _waiters!.Register(ledgerId);

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        try
        {
            var bound = Task.Delay(TimeSpan.FromSeconds(ApprovalBoundSeconds()), linked.Token);

            await Task.WhenAny(waiter.Completion, bound).ConfigureAwait(false);

            linked.Cancel();   // cancel the loser (the leftover delay, or the never-signaled waiter's backing delay)

            cancellationToken.ThrowIfCancellationRequested();   // the run timed out / teardown — propagate, row stays AwaitingApproval

            // ALWAYS re-read the durable row — it's the authority (handles a cross-worker signal / a restart that
            // dropped the TCS). The TCS only told us "something happened, look now".
            var state = await _ledger!.ReadApprovalStateAsync(ledgerId, teamId, cancellationToken).ConfigureAwait(false);

            if (state is null) return ToolResult(isError: true, "This tool call's approval record is missing.");

            if (ToolCallLedgerStateMachine.IsTerminal(state.Status)) return ReplayTerminalState(state);   // rejected / expired by the reaper

            if (state.ApprovedAt is not null) return await ClaimThenExecuteAsync(tool, arguments, teamId, ledgerId, cancellationToken).ConfigureAwait(false);   // approved → claim for execution (single-winner) → run once

            return PendingTicket(ledgerId);   // the bound elapsed with no decision — the row stays AwaitingApproval
        }
        finally
        {
            _waiters.Remove(ledgerId);
        }
    }

    // ─── Decision flow (Decision substrate D2 — agent.run mid-run decision.request) ──────────────────
    // The agent-grain analogue of the approval flow: same durable tool-ledger spine (claim → park → block → resolve),
    // but a DECISION is an ASK with no side effect — the human's typed answer IS the terminal result, so there is no
    // Running execution hop. The resolver records a DecisionAnswer (AwaitingApproval → Succeeded, guarded on the
    // decision tool kind), and the handler wraps that bare answer into the MCP wire result at its own redacting choke
    // point (the resolver, living in chat-land, never touches MCP wire).

    /// <summary>
    /// Drive an agent-grain <c>decision.request</c>: surface-check (fail-closed if there's no answerer), validate, then
    /// claim the durable ledger row (exactly-once by the deterministic key — AC1) and either replay a settled answer, re-
    /// block a still-parked re-call (never a second card), or freshly park + post + block. Returns the DecisionAnswer
    /// (wrapped as the tool result) once answered, or a pending ticket if the synchronous block elapses first.
    /// </summary>
    private async Task<JsonElement> RunDecisionFlowAsync(IAgentTool tool, JsonElement arguments, CancellationToken cancellationToken)
    {
        // A decision can ALWAYS be raised when the durable substrate is present (governance + ledger + waiter + team) — its
        // CORE answer surface is the durable ledger row + the team-wide "Needs decision" queue, NOT a chat conversation. A
        // chat card is OPTIONAL notification (posted only when a conversation is configured + team-owned, in ParkDecisionAsync).
        // So a plain agent.run / workflow / supervisor-spawn run with NO conversation still parks a real, queue-answerable
        // decision — never a flat refusal. This decouples "can ask" from "has a chat surface" (the generic ask-human spine).
        if (!CanRaiseDecision()) return ToolResult(isError: true, "This run cannot raise a decision — decision governance is not enabled here.");

        var validation = tool.ValidateInput(arguments);

        if (!validation.IsValid) return ToolResult(isError: true, validation.Error ?? "Invalid decision input.");

        var teamId = _teamId!.Value;

        var inputHash = ToolCallKey.InputHash(arguments);   // SERVER-derived — never read from the wire
        var key = ToolCallKey.For(tool.Kind, inputHash);

        // D5c guardrail: bound this run's concurrent pending decisions (backpressure on a runaway agent raising many
        // DISTINCT asks). Count its OTHER pending decisions (exclude THIS key so a re-issue stays exempt — it replays via
        // the claim's Duplicate path, AC1), checked BEFORE the claim so a rejected raise never INSERTs a ghost row that
        // burns the dedupe key. Returned as isError (a throw would be degraded to a generic retryable error by the outer
        // dispatch catch); a count fault propagates → the raise fails closed (never silently admitted over the cap).
        var otherPending = await _ledger!.CountPendingDecisionsAsync(_runId, teamId, key, cancellationToken).ConfigureAwait(false);

        if (DecisionBounds.PendingCapBreach(otherPending) is { } breach) return ToolResult(isError: true, breach);

        var claim = await _ledger.TryClaimAsync(_runId, teamId, tool.Kind, key, inputHash, _fenceEpoch, cancellationToken).ConfigureAwait(false);

        return claim.Outcome switch
        {
            ToolCallClaimOutcome.Duplicate => WrapDecisionResult(claim.PriorStatus, claim.PriorResultJson, claim.PriorError),                          // already answered/expired — replay (AC1)
            ToolCallClaimOutcome.InFlight => await ResumeDecisionOrTicketAsync(teamId, claim.LedgerId, cancellationToken).ConfigureAwait(false),        // a re-call of a still-parked decision — never a 2nd card
            _ => await ParkDecisionAsync(arguments, key, teamId, claim.LedgerId, cancellationToken).ConfigureAwait(false),                              // fresh claim — park + post + block
        };
    }

    /// <summary>The CORE requirements to raise + park + block on a decision: the durable ledger (the row + the cross-grain "Needs decision" queue, which IS the answer surface), the in-process waiter (the block fast-path), the team (tenancy), behind the governance flag. A chat conversation is deliberately NOT required — a card is optional notification (see <see cref="HasDecisionCardSurface"/>), so every run shape (plain agent / workflow / supervisor-spawn) inherits ask-human.</summary>
    private bool CanRaiseDecision() =>
        _governanceEnabled && _ledger is not null && _waiters is not null && _teamId is not null;

    /// <summary>Whether a chat decision-card can ALSO be posted as a notification — only when the bot + component registry + an approval conversation are all configured. A run without these still parks a queue-answerable decision; the card is a convenience surface, never a gate on raising.</summary>
    private bool HasDecisionCardSurface() =>
        _bot is not null && _components is not null && _approvalConversationId is not null;

    /// <summary>
    /// A FRESH decision claim's park: CAS Pending → AwaitingApproval (stamping token + the bounded deadline), post the
    /// typed-options card (stamping its message id), then BLOCK until answered. If the park CAS is lost (a concurrent
    /// path already parked the row), DON'T post — re-bind to whatever it became (the no-second-card guard).
    /// </summary>
    private async Task<JsonElement> ParkDecisionAsync(JsonElement arguments, string key, Guid teamId, Guid ledgerId, CancellationToken cancellationToken)
    {
        var token = Guid.NewGuid().ToString("N");
        var deadlineAt = DateTimeOffset.UtcNow.AddSeconds(DecisionTimeoutSeconds(arguments));

        var parked = await _ledger!.TryBeginApprovalAsync(ledgerId, teamId, token, deadlineAt, cancellationToken).ConfigureAwait(false);

        if (!parked) return await ResumeDecisionOrTicketAsync(teamId, ledgerId, cancellationToken).ConfigureAwait(false);

        var request = BuildDecisionRequest(arguments, ledgerId, key, deadlineAt);

        // Stash the ALREADY-REDACTED envelope on the row so the cross-grain "Needs decision" queue (D3) can project this
        // decision's question / options / risk / policy without re-reading the card (symmetric with the node-grain
        // wait-payload stash). Redacted here because the queue is another human surface — same invariant the card upholds.
        await _ledger.SetDecisionEnvelopeAsync(ledgerId, teamId, _redactor.Redact(JsonSerializer.Serialize(request, AgentJson.Options)), cancellationToken).ConfigureAwait(false);

        // OPTIONAL notification: ALSO post a chat decision-card when (and only when) a conversation surface is configured AND
        // belongs to the run's team. A foreign / absent conversation skips the card (never a cross-tenant leak, never a flat
        // refusal) — the decision is already durably parked + queue-answerable above, so the card just mirrors it for chat answerers.
        if (await ShouldPostDecisionCardAsync(teamId, cancellationToken).ConfigureAwait(false))
        {
            var messageId = await PostDecisionCardAsync(request, token, cancellationToken).ConfigureAwait(false);

            await _ledger.SetApprovalMessageAsync(ledgerId, teamId, messageId, cancellationToken).ConfigureAwait(false);
        }

        return await BlockForDecisionAnswerAsync(teamId, ledgerId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Re-bind to an already-parked (or just-resolved) decision WITHOUT posting a second card: a terminal row replays its answer; a still-AwaitingApproval one blocks again on a freshly-registered waiter (the card was posted on the first park).</summary>
    private async Task<JsonElement> ResumeDecisionOrTicketAsync(Guid teamId, Guid ledgerId, CancellationToken cancellationToken)
    {
        var state = await _ledger!.ReadApprovalStateAsync(ledgerId, teamId, cancellationToken).ConfigureAwait(false);

        if (state is null) return ToolResult(isError: true, "This decision's record is missing.");

        if (ToolCallLedgerStateMachine.IsTerminal(state.Status)) return WrapDecisionResult(state.Status, state.ResultJson, state.Error);

        return await BlockForDecisionAnswerAsync(teamId, ledgerId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// BLOCK the synchronous decision.request call until an answer lands or the bound elapses (mirrors
    /// <see cref="BlockForDecisionAsync"/>, minus the approve→execute hop — a decision resolves straight to a terminal).
    /// The durable row is the authority, so we ALWAYS re-read after the wake; the in-memory waiter is a latency fast-path.
    /// On ct cancellation we propagate (the row stays AwaitingApproval for the reaper / a re-issue). On the bound elapsing
    /// with no answer, the pending ticket lets the model re-issue the exact call to keep waiting.
    /// </summary>
    private async Task<JsonElement> BlockForDecisionAnswerAsync(Guid teamId, Guid ledgerId, CancellationToken cancellationToken)
    {
        var waiter = _waiters!.Register(ledgerId);

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        try
        {
            var bound = Task.Delay(TimeSpan.FromSeconds(ApprovalBoundSeconds()), linked.Token);

            await Task.WhenAny(waiter.Completion, bound).ConfigureAwait(false);

            linked.Cancel();

            cancellationToken.ThrowIfCancellationRequested();

            var state = await _ledger!.ReadApprovalStateAsync(ledgerId, teamId, cancellationToken).ConfigureAwait(false);

            if (state is null) return ToolResult(isError: true, "This decision's record is missing.");

            if (ToolCallLedgerStateMachine.IsTerminal(state.Status)) return WrapDecisionResult(state.Status, state.ResultJson, state.Error);   // answered / expired by the reaper

            return PendingDecisionTicket(ledgerId);   // the bound elapsed with no answer — the row stays AwaitingApproval
        }
        finally
        {
            _waiters.Remove(ledgerId);
        }
    }

    /// <summary>Wrap a settled decision row into the MCP wire result at the redacting choke point: a Succeeded row replays its stored DecisionAnswer (as both text + structuredContent); anything else (expired / cancelled) is an isError the model can act on.</summary>
    private JsonElement WrapDecisionResult(ToolCallLedgerStatus status, string? resultJson, string? error)
    {
        if (status != ToolCallLedgerStatus.Succeeded || resultJson is not { Length: > 0 } answer)
            return ToolResult(isError: true, error ?? "This decision was not answered.");

        using var doc = JsonDocument.Parse(answer);

        return ToolResult(isError: false, answer, doc.RootElement.Clone());
    }

    /// <summary>The typed pending-ticket returned when the bounded block elapses with no answer — names the ledger ticket so the model can re-issue the exact call to keep waiting for a human.</summary>
    private JsonElement PendingDecisionTicket(Guid ledgerId) =>
        ToolResult(isError: true, $"Awaiting a human decision (ticket {ledgerId}); still pending. Re-issue this exact call to keep waiting.");

    /// <summary>The agent-declared deadline (seconds) clamped to a sane bounded range — a decision can never hang forever (AC4); the durable reaper expires the row past this.</summary>
    private static int DecisionTimeoutSeconds(JsonElement arguments)
    {
        const int min = 30, max = 86400, fallback = 3600;

        return arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty("timeoutSeconds", out var t)
            && t.ValueKind == JsonValueKind.Number && t.TryGetInt32(out var seconds) && seconds > 0
            ? Math.Clamp(seconds, min, max)
            : fallback;
    }

    /// <summary>Project the agent's call arguments into the typed <see cref="DecisionRequest"/> envelope, stamping the SERVER-controlled fields (the ledger id IS the decision id; the run is the root trace; the backend is the tool ledger). The agent declares only the semantic fields (question / options / risk / policy), already validated.</summary>
    private DecisionRequest BuildDecisionRequest(JsonElement args, Guid ledgerId, string key, DateTimeOffset deadlineAt)
    {
        var options = ReadDecisionOptions(args);

        var draft = new DecisionRequest
        {
            Id = ledgerId,
            RootTraceId = _runId,
            AgentRunId = _runId,
            ToolCallId = ledgerId.ToString("N"),
            Scope = DecisionScopes.Agent,
            RequesterType = DecisionRequesterTypes.Agent,
            // Default the type from the options' presence when the agent left it unset — option buttons vs a free-text submit.
            DecisionType = ReadDecisionString(args, "decisionType") ?? (options.Count > 0 ? DecisionTypes.ChooseOne : DecisionTypes.FreeText),
            Question = ReadDecisionString(args, "question") ?? "",
            Options = options,
            RecommendedOption = ReadDecisionString(args, "recommendedOption"),
            BlockingReason = ReadDecisionString(args, "blockingReason"),
            ContextSummary = ReadDecisionString(args, "contextSummary"),
            AnswerSchema = ReadDecisionString(args, "answerSchema"),
            RiskLevel = ReadDecisionString(args, "riskLevel") ?? DecisionRiskLevels.Medium,
            Policy = ReadDecisionString(args, "policy") ?? DecisionPolicies.HumanRequired,
            DefaultAction = ReadDecisionString(args, "defaultAction"),
            TimeoutAt = deadlineAt,
            DedupeKey = key,
            ResumeBackend = DecisionResumeBackends.ToolLedger,
            Status = DecisionStatuses.Pending,
        };

        // The fail-closed floor clamps the agent's declared policy up to human_required for a high-stakes ask — the
        // stashed envelope carries the EFFECTIVE policy so the queue + the D4 arbiter never auto-resolve a human-only one.
        return draft with { Policy = DecisionPolicyFloor.Effective(draft) };
    }

    private static string? ReadDecisionString(JsonElement obj, string name) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static IReadOnlyList<DecisionOption> ReadDecisionOptions(JsonElement args)
    {
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty("options", out var options) || options.ValueKind != JsonValueKind.Array)
            return Array.Empty<DecisionOption>();

        return options.EnumerateArray()
            .Where(o => o.ValueKind == JsonValueKind.Object)
            .Select(o => new DecisionOption
            {
                Id = ReadDecisionString(o, "id") ?? "",
                Label = ReadDecisionString(o, "label") ?? "",
                IsSideEffecting = ReadSideEffecting(o),
            })
            .Where(o => o.Id.Length > 0)
            .ToList();
    }

    /// <summary>The isSideEffecting flag drives the fail-closed policy floor (an irreversible option → human-only), so parse it LENIENTLY: a JSON boolean true OR a string "true" (a non-conformant model emitting the string form must not silently drop the safety flag).</summary>
    private static bool ReadSideEffecting(JsonElement option) =>
        option.TryGetProperty("isSideEffecting", out var s)
        && (s.ValueKind == JsonValueKind.True || (s.ValueKind == JsonValueKind.String && bool.TryParse(s.GetString(), out var b) && b));

    /// <summary>Build + post the REDACTED decision card into the run's approval conversation: the body carries the question + (redacted) blocking reason + recommendation; the buttons are the typed options (or a free-text submit). The server-side <see cref="DecisionRequestTarget"/> carries the resolution token (omitted from the client view).</summary>
    /// <summary>Whether to ALSO post the chat decision-card: a card surface is configured (<see cref="HasDecisionCardSurface"/>) AND the configured conversation belongs to the run's team. A foreign conversation is an operator misconfiguration — skip the card (never a cross-tenant leak), but the decision still parks + is answerable in the queue (the card is never a gate). Mirrors CanServeApprovalAsync's tenancy check, but as a card-only guard rather than a raise-gate.</summary>
    private async Task<bool> ShouldPostDecisionCardAsync(Guid teamId, CancellationToken cancellationToken) =>
        HasDecisionCardSurface() && await _bot!.ConversationBelongsToTeamAsync(_approvalConversationId!.Value, teamId, cancellationToken).ConfigureAwait(false);

    private async Task<Guid> PostDecisionCardAsync(DecisionRequest request, string token, CancellationToken cancellationToken)
    {
        var component = _components!.Build(DecisionButtonsConfig(request))
            ?? throw new InvalidOperationException("The decision action-buttons component factory is not registered.");

        var interaction = new MessageInteraction
        {
            Component = component,
            Target = new DecisionRequestTarget { Token = token },
            AllowedResponderUserIds = null,   // any team member may answer (team-scoped already)
            Resolve = new ResolvePolicy(),    // first responder wins
        };

        var posted = await _bot!.PostAsBotAsync(_approvalConversationId!.Value, DecisionCardBody(request), interaction, cancellationToken).ConfigureAwait(false);

        return posted.Id;
    }

    /// <summary>The typed-options button config: option id → button key for choose_one / approve_action; a single free-text submit (the reserved key + requiresComment) for confirm / free_text / an option-less ask. The recommended option gets the Primary emphasis. Option LABELS are agent-authored free text, so they go through the run redactor before reaching the human card — the same invariant <see cref="DecisionCardBody"/> upholds for the body (a button key is the option id, NOT displayed, so it stays verbatim as the resolution handle).</summary>
    private JsonElement DecisionButtonsConfig(DecisionRequest request)
    {
        var buttons = request.DecisionType == DecisionTypes.FreeText || request.Options.Count == 0
            ? new object[] { new { key = DecisionRequestResolver.FreeTextResponseKey, label = "Answer", style = "Primary", requiresComment = true } }
            : request.Options
                .Select(o => (object)new { key = o.Id, label = _redactor.Redact(o.Label), style = o.Id == request.RecommendedOption ? "Primary" : "Default", requiresComment = false })
                .ToArray();

        return JsonSerializer.SerializeToElement(new { kind = "action_buttons", buttons }, AgentJson.Options);
    }

    /// <summary>The redacted decision card body — the question + (optional) why + recommendation. Routed through the run's redactor so an echoed secret in the agent's text never reaches the human surface.</summary>
    private string DecisionCardBody(DecisionRequest request) =>
        _redactor.Redact(
            $"Agent run {_runId} needs a decision: **{request.Question}**"
            + (request.BlockingReason is { Length: > 0 } reason ? $"\n\n_Why:_ {reason}" : "")
            + (request.RecommendedOption is { Length: > 0 } rec ? $"\n\n_Recommended:_ {rec}" : ""));

    /// <summary>
    /// Build + post the REDACTED approval card into the run's approval conversation. The body names the tool + a
    /// redacted argument summary + the run id (no secret reaches the message); the server-side <see cref="ToolCallApprovalTarget"/>
    /// carries the token (omitted from the client-facing view). The component is built by the registry (mirrors
    /// ChatPostMessageNode) so a future card kind is a factory change, not an edit here. Returns the posted message id.
    /// </summary>
    private async Task<Guid> PostApprovalCardAsync(IAgentTool tool, string name, string token, CancellationToken cancellationToken)
    {
        var component = _components!.Build(ApprovalButtonsConfig())
            ?? throw new InvalidOperationException("The approval action-buttons component factory is not registered.");

        var interaction = new MessageInteraction
        {
            Component = component,
            Target = new ToolCallApprovalTarget { Token = token },
            AllowedResponderUserIds = null,   // any team member may approve (team-scoped already); revisit per-actor scoping in item E
            Resolve = new ResolvePolicy(),    // first responder wins
        };

        var posted = await _bot!.PostAsBotAsync(_approvalConversationId!.Value, ApprovalCardBody(name, tool), interaction, cancellationToken).ConfigureAwait(false);

        return posted.Id;
    }

    /// <summary>The two-button approve/reject config the registry builds into an ActionButtonsComponent (mirrors ChatPostMessageNode's ShimActions shape). Both buttons resolve the wait (first-wins); reject requires a reason.</summary>
    private static JsonElement ApprovalButtonsConfig() => JsonSerializer.SerializeToElement(new
    {
        kind = "action_buttons",
        buttons = new object[]
        {
            new { key = ApproveKey, label = "Approve", style = "Primary" },
            new { key = RejectKey, label = "Reject", style = "Danger", requiresComment = true },
        },
    }, AgentJson.Options);

    /// <summary>The redacted card body: tool + a redacted argument summary + the run id. Routed through <see cref="ToolResult"/>'s redactor indirectly via the redactor here — the message must never carry a secret.</summary>
    private string ApprovalCardBody(string name, IAgentTool tool) =>
        _redactor.Redact($"Agent run {_runId} requests approval to run **{name}** ({tool.Description}). Approve to let it proceed, or reject to refuse it.");

    /// <summary>The typed pending-ticket returned when the bound elapses with no decision — names the ledger ticket so the model (or operator) can re-issue the exact call to retry once a human approves.</summary>
    private JsonElement PendingTicket(Guid ledgerId) =>
        ToolResult(isError: true, $"Awaiting human approval (ticket {ledgerId}); the decision is still pending. Re-issue this exact call to retry.");

    /// <summary>Rebuild a wire result from a terminal <see cref="ToolCallApprovalState"/> snapshot (mirrors <see cref="ReplayPriorResult"/>): a Succeeded row replays its stored wire JSON; anything else rebuilds an isError result from its redacted error.</summary>
    private JsonElement ReplayTerminalState(ToolCallApprovalState state) =>
        state is { Status: ToolCallLedgerStatus.Succeeded, ResultJson: { Length: > 0 } stored }
            ? JsonDocument.Parse(stored).RootElement.Clone()
            : ToolResult(isError: true, state.Error ?? "This tool call was not approved.");

    /// <summary>
    /// Run the side effect once, build the ALREADY-REDACTED wire result (the single <see cref="ToolResult"/> choke
    /// point), persist that redacted payload to the ledger as the terminal (Succeeded with the verbatim wire result,
    /// or Failed with the redacted error), and return it. Redact-BEFORE-persist: the ledger row never holds a raw
    /// secret, and a later duplicate replays the exact bytes the model first saw. The CALLER has already won the
    /// single-winner claim — the Allow path via the Pending INSERT (<see cref="InvokeWithLedgerAsync"/>), the approval
    /// path via the AwaitingApproval→Running CAS (<see cref="ClaimThenExecuteAsync"/>) — so this runs <c>tool.CallAsync</c>
    /// exactly once; the terminal record below is Running/Pending → terminal.
    ///
    /// <para>Liveness: the row is non-terminal (Pending on Allow, Running on the approval path). A tool call that is CANCELLED (timeout / endpoint teardown /
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
            var result = await tool.CallAsync(new AgentToolCall { Input = arguments, TeamId = _teamId, RunId = _runId }, cancellationToken).ConfigureAwait(false);

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
            // non-terminal row (Pending on Allow, Running on the approval path) is never stranded, then re-throw —
            // cancellation must still propagate.
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
    /// even this fails the row stays non-terminal (Pending / Running) and the future Pending-row reaper (out of scope,
    /// see <see cref="ExecuteAndRecordAsync"/>) catches it. NEVER throws from the recovery path (the caller re-throws
    /// the original cancellation).
    /// </summary>
    private async Task RecordInterruptedThenRethrow(Guid teamId, Guid ledgerId)
    {
        try
        {
            await _ledger!.RecordTerminalAsync(ledgerId, teamId, ToolCallLedgerStatus.Failed, resultJson: null, error: "tool call interrupted before completion; safe to retry", CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // Swallow: the row stays non-terminal and the future reaper recovers it. Never throw from recovery.
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
            var result = await tool.CallAsync(new AgentToolCall { Input = arguments, TeamId = _teamId, RunId = _runId }, cancellationToken).ConfigureAwait(false);

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
        // Advertise only the tools this run's catalog mode serves — a ReadOnly run never even sees a side-effecting name.
        var tools = _registry.All.Where(Serves).Select(ToolDescriptor).ToArray();

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
