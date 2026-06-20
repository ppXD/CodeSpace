using System.Text.Json;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Decisions;

namespace CodeSpace.Core.Services.Agents.Tools;

/// <summary>
/// The first-party <c>decision.request</c> MCP tool (Decision substrate D2): the seam an <c>agent.code</c> mid-run
/// uses to PAUSE and ask a typed question — pick an option, confirm, or free-text — and BLOCK until a human (or, once
/// D4 lands, a policy / supervisor arbiter) answers. It is an ASK, never a side effect, so it never flows through the
/// autonomy gate; <see cref="McpRequestHandler"/> special-cases it BEFORE the gate and drives the durable park/block
/// on the SAME tool-ledger spine the approval flow uses, generalized from binary approve/reject to typed options.
///
/// <para>This class is the CATALOG entry only — its <see cref="InputSchema"/> teaches the model how to phrase a
/// decision, and its <see cref="OutputSchema"/> describes the <c>DecisionAnswer</c> it gets back. <see cref="CallAsync"/>
/// is NEVER reached on the live path (the handler intercepts the call name and runs the decision flow itself); it
/// fail-closes defensively so a future mis-wiring surfaces as a tool error, not a silent no-op. It is registered as a
/// first-party tool ONLY when governance is on (the decision flow needs the ledger + approval surface), so a
/// governance-OFF run never sees it in <c>tools/list</c> — byte-identical to pre-D2.</para>
/// </summary>
public sealed class DecisionRequestTool : IAgentTool
{
    /// <summary>The reserved tool name — pinned in Messages so the handler's special-case and the ledger answer-CAS guard share one literal.</summary>
    public const string ToolKind = DecisionToolKinds.DecisionRequest;

    public string Kind => ToolKind;

    public string Description =>
        "Pause and ask a human a typed decision (confirm / choose_one / free_text), then block until answered. " +
        "Use when you are genuinely blocked and need a human (or supervisor) to choose a path — provide a recommendedOption and blockingReason.";

    public JsonElement InputSchema { get; } = BuildInputSchema();

    public JsonElement OutputSchema { get; } = BuildOutputSchema();

    // An ask, not a side effect: read-only is false (it's not cache-safe — it has an external effect of asking a human),
    // but it is NOT destructive and does NOT itself require the autonomy-gate's approval (the handler special-cases it
    // before the gate). The decision's OWN risk/policy travels in the envelope, not in these tool-level flags.
    public bool IsReadOnly => false;
    public bool IsConcurrencySafe => false;
    public bool IsDestructive => false;
    public bool RequiresApproval => false;

    public AgentToolValidation ValidateInput(JsonElement input)
    {
        if (input.ValueKind != JsonValueKind.Object) return AgentToolValidation.Invalid("decision.request input must be a JSON object.");

        if (!TryReadNonEmptyString(input, "question", out _)) return AgentToolValidation.Invalid("decision.request requires a non-empty 'question'.");

        // Options are required ONLY for an EXPLICIT option-bearing type; an unset / confirm / free_text ask validates
        // without them (a bare question is a yes/no or open answer — the card renders a free-text submit).
        return RequiresOptions(ReadString(input, "decisionType")) ? ValidateOptions(input) : AgentToolValidation.Valid;
    }

    /// <summary>NEVER reached on the live path (the handler intercepts <c>decision.request</c> before dispatch). Fail-closed so a future mis-wiring surfaces as a tool error rather than silently doing nothing.</summary>
    public Task<AgentToolResult> CallAsync(AgentToolCall call, CancellationToken cancellationToken) =>
        Task.FromResult(AgentToolResult.Fail("decision.request is resolved by the decision substrate, not executed directly."));

    // ── validation helpers ──

    /// <summary>The option-bearing decision shapes — choose_one / choose_many / approve_action need at least one selectable option; an unset / confirm / free_text type does not.</summary>
    private static bool RequiresOptions(string? decisionType) =>
        decisionType is DecisionTypes.ChooseOne or DecisionTypes.ChooseMany or DecisionTypes.ApproveAction;

    private static AgentToolValidation ValidateOptions(JsonElement input)
    {
        if (!input.TryGetProperty("options", out var options) || options.ValueKind != JsonValueKind.Array || options.GetArrayLength() == 0)
            return AgentToolValidation.Invalid("this decisionType requires a non-empty 'options' array of { id, label }.");

        var ids = new HashSet<string>(StringComparer.Ordinal);

        foreach (var option in options.EnumerateArray())
        {
            if (!TryReadNonEmptyString(option, "id", out var id) || !TryReadNonEmptyString(option, "label", out _))
                return AgentToolValidation.Invalid("every option requires a non-empty 'id' and 'label'.");

            if (!ids.Add(id)) return AgentToolValidation.Invalid($"duplicate option id '{id}' — option ids must be unique.");
        }

        var recommended = ReadString(input, "recommendedOption");

        if (recommended is not null && !ids.Contains(recommended))
            return AgentToolValidation.Invalid($"recommendedOption '{recommended}' is not one of the option ids.");

        return AgentToolValidation.Valid;
    }

    private static bool TryReadNonEmptyString(JsonElement obj, string name, out string value)
    {
        value = ReadString(obj, name) ?? "";

        return !string.IsNullOrWhiteSpace(value);
    }

    private static string? ReadString(JsonElement obj, string name) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    // ── schemas ──

    private static JsonElement BuildInputSchema() => JsonSerializer.SerializeToElement(new
    {
        type = "object",
        required = new[] { "question" },
        properties = new
        {
            question = new { type = "string", description = "The decision to ask the human." },
            decisionType = new { type = "string", @enum = new[] { DecisionTypes.Confirm, DecisionTypes.ChooseOne, DecisionTypes.FreeText, DecisionTypes.ApproveAction }, description = "Shape of the ask. Defaults to choose_one when options are given, else free_text." },
            options = new
            {
                type = "array",
                description = "Selectable options (required for choose_one / choose_many / approve_action).",
                items = new
                {
                    type = "object",
                    required = new[] { "id", "label" },
                    properties = new
                    {
                        id = new { type = "string" },
                        label = new { type = "string" },
                        isSideEffecting = new { type = "boolean", description = "True if choosing this is irreversible — the policy floor forces such a choice to a human." },
                    },
                },
            },
            recommendedOption = new { type = "string", description = "The option id you recommend — required for the request to ever be auto-answerable." },
            blockingReason = new { type = "string", description = "Why you are blocked — the context the answerer needs." },
            contextSummary = new { type = "string", description = "A short self-contained summary so the answerer needn't read the whole run." },
            riskLevel = new { type = "string", @enum = new[] { DecisionRiskLevels.Low, DecisionRiskLevels.Medium, DecisionRiskLevels.High }, description = "Your declared risk. The server floor can only raise it." },
            policy = new { type = "string", @enum = new[] { DecisionPolicies.AutoAllowed, DecisionPolicies.SupervisorFirst, DecisionPolicies.HumanRequired }, description = "Who may answer. Clamped by the server fail-closed floor." },
            timeoutSeconds = new { type = "integer", description = "How long to wait before the decision expires (bounded — it never hangs; on timeout the call returns an error so you can re-issue)." },
        },
    }, AgentJson.Options);

    private static JsonElement BuildOutputSchema() => JsonSerializer.SerializeToElement(new
    {
        type = "object",
        required = new[] { "decisionId", "answeredBy" },
        properties = new
        {
            decisionId = new { type = "string" },
            answeredBy = new { type = "string", @enum = new[] { DecisionAnsweredByKinds.Policy, DecisionAnsweredByKinds.Supervisor, DecisionAnsweredByKinds.Human, DecisionAnsweredByKinds.Timeout } },
            selectedOptions = new { type = "array", items = new { type = "string" } },
            freeText = new { type = "string" },
            rationale = new { type = "string" },
            timedOut = new { type = "boolean" },
        },
    }, AgentJson.Options);
}
