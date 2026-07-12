using System.Text.Json;
using CodeSpace.Core.Services.Decisions;
using CodeSpace.Core.Services.Workflows.Runtime;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Decisions;
using CodeSpace.Messages.Enums;

namespace CodeSpace.Core.Services.Workflows.Nodes.Builtin;

/// <summary>
/// D1 of the durable Decision substrate — the node-grain primitive. Raises a typed <see cref="DecisionRequest"/> and
/// pauses the run until it is answered: the first execution returns <c>Suspend</c> with a <c>Decision</c> wait
/// carrying the envelope (the run parks Suspended at this EXACT node); an answerer (a human via the resume API / the
/// "Needs decision" queue, a policy auto-answer, a supervisor arbiter, or the MANDATORY bounded-wait deadline applying
/// the default) resolves the wait with a <see cref="DecisionAnswer"/> and re-dispatches; the resumed pass surfaces the
/// answer as outputs, so downstream branches on <c>{{nodes.&lt;id&gt;.outputs.selectedOption}}</c> / <c>.answeredBy</c>.
///
/// <para>The structured, policy-gated sibling of <c>flow.wait_approval</c> (which is the binary special case). It
/// reuses the engine's park/resume spine verbatim (<c>SuspendNodeAsync</c> + <c>ResolveWaitThenDispatchAsync</c>), so
/// it is durable + resume-from-exact-point for free; the <c>(run, node, iteration)</c> unique index makes a re-suspend
/// idempotent (AC1); the single-writer resolve CAS makes an answer resolve exactly once (AC2); the always-set
/// <c>DeadlineAt</c> guarantees it never hangs (AC4); <see cref="DecisionRequest.RootTraceId"/> makes the chain
/// one-line traceable (AC5).</para>
/// </summary>
public sealed class FlowDecisionNode : INodeRuntime
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private const int DefaultTimeoutSeconds = 3600;

    public string TypeKey => "flow.decision";

    public NodeManifest Manifest { get; } = new()
    {
        DisplayName = "Decision",
        Category = "Logic",
        Kind = NodeKind.Regular,
        CanSuspend = true,
        IconKey = "git-pull-request-arrow",
        Description = "Raises a typed decision and pauses until it's answered (human / policy / supervisor / bounded-wait default). Outputs { selectedOption, selectedOptions, freeText, answeredBy, rationale, timedOut }.",
        ConfigSchema = SchemaBuilder.Parse("""
            {
              "type": "object",
              "x-intent": "Ask: \"{question}\".",
              "x-intentPlaceholders": { "question": "a question" },
              "properties": {
                "question":          { "type": "string", "description": "The question shown to the answerer." },
                "decisionType":      { "type": "string", "enum": ["confirm","choose_one","choose_many","free_text","approve_action"], "default": "confirm", "title": "Decision type", "x-control": "radioCards", "x-enumLabels": { "confirm": "Confirm (yes / no)", "choose_one": "Choose one option", "choose_many": "Choose several options", "free_text": "Type a free-text answer", "approve_action": "Approve or reject an action" }, "x-optionConsequence": { "confirm": "The answerer clicks yes or no — the simplest gate.", "choose_one": "The answerer picks exactly one of the options below.", "choose_many": "The answerer picks any number of the options below.", "free_text": "The answerer writes an answer (optionally schema-checked).", "approve_action": "The answerer approves or rejects a side-effecting action before it runs." } },
                "options":           { "type": "array", "items": { "type": "object", "properties": { "id": {"type":"string"}, "label": {"type":"string"}, "isSideEffecting": {"type":"boolean"} }, "required": ["id","label"] }, "description": "Selectable options (for confirm/choose_one/choose_many/approve_action)." },
                "recommendedOption": { "type": "string", "description": "The recommended option id — REQUIRED for any auto-answerable policy." },
                "blockingReason":    { "type": "string", "description": "Why the run is blocked — REQUIRED for any auto-answerable policy." },
                "contextSummary":    { "type": "string", "description": "A short self-contained summary so the answerer needn't read the whole run." },
                "answerSchema":      { "type": "object", "description": "Optional JSON Schema the free-text answer must conform to." },
                "riskLevel":         { "type": "string", "enum": ["low","medium","high"], "default": "medium", "title": "Risk level", "x-control": "segmented", "x-enumLabels": { "low": "Low", "medium": "Medium", "high": "High" } },
                "policy":            { "type": "string", "enum": ["auto_allowed","supervisor_first","human_required"], "default": "human_required", "title": "Who answers", "x-control": "radioCards", "x-enumLabels": { "auto_allowed": "Auto-answer allowed", "supervisor_first": "Supervisor first, then a human", "human_required": "A human must answer" }, "x-optionConsequence": { "auto_allowed": "An arbiter may answer automatically when confident enough; otherwise it escalates.", "supervisor_first": "The supervisor attempts the answer; unresolved ones fall to a human.", "human_required": "Always pauses for a person — no automatic answer." } },
                "confidenceRequired":{ "type": "number", "minimum": 0, "maximum": 1, "description": "Minimum confidence an arbiter must clear to auto-answer." },
                "defaultAction":     { "type": "string", "description": "The option id applied on timeout (the never-hang default). Omit to surface a _timedOut answer the downstream handles." },
                "timeoutSeconds":    { "type": "integer", "minimum": 1, "default": 3600, "description": "The mandatory deadline — a decision never hangs forever." }
              },
              "required": ["question"]
            }
            """),
        InputSchema = SchemaBuilder.EmptyObject(),
        OutputSchema = SchemaBuilder.Parse("""
            {
              "type": "object",
              "properties": {
                "selectedOption":  { "type": ["string","null"] },
                "selectedOptions": { "type": "array" },
                "freeText":        { "type": ["string","null"] },
                "answeredBy":      { "type": "string" },
                "rationale":       { "type": ["string","null"] },
                "timedOut":        { "type": "boolean" }
              }
            }
            """),
    };

    public Task<NodeResult> RunAsync(NodeRunContext context, CancellationToken cancellationToken)
    {
        // Resumed pass: a decision was answered (human / policy / supervisor / timeout-default). Surface it as outputs.
        if (context.ResumePayload.HasValue)
            return Task.FromResult(NodeResult.Ok(MapAnswerToOutputs(context.ResumePayload.Value)));

        // First pass: build the typed envelope + park on a BOUNDED Decision wait (the deadline applies the default so
        // it can never hang). The (run, node, iteration) unique index makes a re-suspend idempotent.
        var request = BuildRequest(context);
        var timeoutAnswer = DecisionAnswer.Timeout(request, request.Id);

        return Task.FromResult(NodeResult.Suspend(new SuspensionToken
        {
            Kind = WorkflowWaitKinds.Decision,
            Payload = JsonSerializer.SerializeToElement(request, Json),
            DeadlineAt = request.TimeoutAt,
            TimeoutPayload = JsonSerializer.SerializeToElement(timeoutAnswer, Json),
        }));
    }

    private static DecisionRequest BuildRequest(NodeRunContext context)
    {
        var rootTraceId = ReadRunId(context);

        // The envelope is persisted to the wait payload + read back by the team-wide "Needs decision" queue / the
        // run-detail surface — both HUMAN surfaces that outlive the run. So build EVERY field from the REDACTED config
        // (a {{team.SECRET}} in author-written question / options / blockingReason becomes a "[REDACTED: path]" marker),
        // mirroring the agent grain (which redacts its envelope at park). Functional fields bear no secret refs, so the
        // redacted bag is identical to the resolved one for them. Falls back to Config only off the engine path (tests).
        var config = context.RedactedConfig ?? context.Config;
        var timeoutSeconds = ReadInt(config, "timeoutSeconds", DefaultTimeoutSeconds);

        var draft = new DecisionRequest
        {
            Id = Guid.NewGuid(),
            RootTraceId = rootTraceId,
            WorkflowRunId = rootTraceId,
            NodeId = context.NodeId,
            Scope = DecisionScopes.Node,
            RequesterType = DecisionRequesterTypes.WorkflowNode,
            DecisionType = ReadString(config, "decisionType", DecisionTypes.Confirm),
            Question = ReadString(config, "question", ""),
            Options = ReadOptions(config),
            RecommendedOption = ReadStringOrNull(config, "recommendedOption"),
            BlockingReason = ReadStringOrNull(config, "blockingReason"),
            ContextSummary = ReadStringOrNull(config, "contextSummary"),
            AnswerSchema = ReadObjectRaw(config, "answerSchema"),
            RiskLevel = ReadString(config, "riskLevel", DecisionRiskLevels.Medium),
            Policy = ReadString(config, "policy", DecisionPolicies.HumanRequired),
            ConfidenceRequired = ReadDoubleOrNull(config, "confidenceRequired"),
            DefaultAction = ReadStringOrNull(config, "defaultAction"),
            TimeoutAt = DateTimeOffset.UtcNow.AddSeconds(timeoutSeconds),
            DedupeKey = $"{rootTraceId:N}:{context.NodeId}",
            ResumeBackend = DecisionResumeBackends.WorkflowWait,
            Status = DecisionStatuses.Pending,
        };

        // The fail-closed floor clamps the declared policy up to human_required for a high-stakes ask — the stashed
        // envelope carries the EFFECTIVE policy, so the queue + the D4 arbiter never auto-resolve what only a human may.
        return draft with { Policy = DecisionPolicyFloor.Effective(draft) };
    }

    private static Dictionary<string, JsonElement> MapAnswerToOutputs(JsonElement resumePayload)
    {
        var answer = Deserialize(resumePayload);
        var first = answer.SelectedOptions.Count > 0 ? answer.SelectedOptions[0] : null;

        return new Dictionary<string, JsonElement>
        {
            ["selectedOption"]  = JsonSerializer.SerializeToElement(first),
            ["selectedOptions"] = JsonSerializer.SerializeToElement(answer.SelectedOptions),
            ["freeText"]        = JsonSerializer.SerializeToElement(answer.FreeText),
            ["answeredBy"]      = JsonSerializer.SerializeToElement(answer.AnsweredBy),
            ["rationale"]       = JsonSerializer.SerializeToElement(answer.Rationale),
            ["timedOut"]        = JsonSerializer.SerializeToElement(answer.TimedOut),
        };
    }

    /// <summary>Tolerant deserialize of the resume payload — a malformed/foreign payload degrades to a "no answer" rather than crashing the resumed walk.</summary>
    private static DecisionAnswer Deserialize(JsonElement payload)
    {
        try
        {
            return JsonSerializer.Deserialize<DecisionAnswer>(payload, Json) ?? Empty();
        }
        catch (JsonException)
        {
            return Empty();
        }
    }

    private static DecisionAnswer Empty() => new() { DecisionId = Guid.Empty, AnsweredBy = DecisionAnsweredByKinds.Human };

    private static Guid ReadRunId(NodeRunContext context) =>
        context.Scope.Sys.TryGetValue(SystemScopeKeys.WorkflowRunId, out var v) && v.ValueKind == JsonValueKind.String && Guid.TryParse(v.GetString(), out var id)
            ? id : Guid.Empty;

    private static IReadOnlyList<DecisionOption> ReadOptions(IReadOnlyDictionary<string, JsonElement> config)
    {
        if (!config.TryGetValue("options", out var v) || v.ValueKind != JsonValueKind.Array) return Array.Empty<DecisionOption>();

        try
        {
            return JsonSerializer.Deserialize<List<DecisionOption>>(v, Json) ?? new List<DecisionOption>();
        }
        catch (JsonException)
        {
            return Array.Empty<DecisionOption>();
        }
    }

    private static string ReadString(IReadOnlyDictionary<string, JsonElement> bag, string key, string fallback)
    {
        if (!bag.TryGetValue(key, out var v) || v.ValueKind != JsonValueKind.String) return fallback;
        var s = v.GetString();
        return string.IsNullOrWhiteSpace(s) ? fallback : s;
    }

    private static string? ReadStringOrNull(IReadOnlyDictionary<string, JsonElement> bag, string key)
    {
        if (!bag.TryGetValue(key, out var v) || v.ValueKind != JsonValueKind.String) return null;
        var s = v.GetString();
        return string.IsNullOrWhiteSpace(s) ? null : s;
    }

    private static string? ReadObjectRaw(IReadOnlyDictionary<string, JsonElement> bag, string key) =>
        bag.TryGetValue(key, out var v) && v.ValueKind == JsonValueKind.Object && v.EnumerateObject().Any() ? v.GetRawText() : null;

    private static int ReadInt(IReadOnlyDictionary<string, JsonElement> bag, string key, int fallback) =>
        bag.TryGetValue(key, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) && i > 0 ? i : fallback;

    private static double? ReadDoubleOrNull(IReadOnlyDictionary<string, JsonElement> bag, string key) =>
        bag.TryGetValue(key, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d) ? d : null;
}
