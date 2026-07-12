using System.Text.Json;
using CodeSpace.Core.Services.Agents.Cost;
using CodeSpace.Core.Services.Agents.ModelCredentials;
using CodeSpace.Core.Services.Workflows.Llm;
using CodeSpace.Core.Services.Workflows.Runtime;
using CodeSpace.Messages.Enums;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CodeSpace.Core.Services.Workflows.Nodes.Builtin;

/// <summary>
/// Single-turn LLM completion. Config picks <c>provider</c> (Anthropic, etc) + <c>model</c>;
/// inputs supply the rendered system + user prompts. Outputs the completion text + token
/// counts. Two distinct knobs:
///   - <see cref="NodeManifest.ConfigSchema"/> — "which LLM" (rarely changes at runtime)
///   - <see cref="NodeManifest.InputSchema"/> — "what to say" (almost always {{ref}}'d from upstream node outputs)
/// This split is what makes the AI Code Review template "drop in YOUR repo" without editing
/// the LLM config — the user only edits the trigger and the prompt inputs.
/// </summary>
public sealed class LlmCompleteNode : INodeRuntime
{
    private readonly ILLMClientRegistry _clientRegistry;
    private readonly IServiceScopeFactory _scopeFactory;

    // The node is a DI SINGLETON, so it must NOT capture the scoped IModelPoolSelector (which holds a scoped
    // DbContext) — concurrent flow.map branches would then share one DbContext and collide. Resolve the selector
    // from a FRESH scope per RunAsync (same pattern as AgentSupervisorNode); the returned pick is a detached POCO.
    public LlmCompleteNode(ILLMClientRegistry clientRegistry, IServiceScopeFactory scopeFactory)
    {
        _clientRegistry = clientRegistry;
        _scopeFactory = scopeFactory;
    }

    public string TypeKey => "llm.complete";

    public NodeManifest Manifest { get; } = new()
    {
        DisplayName = "LLM completion",
        Category = "AI",
        Kind = NodeKind.Regular,
        IconKey = "sparkles",
        Description = "Calls an LLM with a system + user prompt and returns the completion text.",
        // LLM completions have BILLING side effects: every call is metered. A retry that
        // re-bills is not "duplicate effect on the world" in the same way as a PR comment,
        // but the operator still doesn't want to be charged twice when the engine recovers
        // from a worker crash. Mark side-effecting.
        IsSideEffecting = true,
        ConfigSchema = SchemaBuilder.Parse("""
            {
              "type": "object",
              "x-intent": "Complete a prompt with {provider}, using {model}.",
              "x-intentPlaceholders": { "provider": "Anthropic", "model": "the pool's recommended model" },
              "properties": {
                "provider": { "type": "string", "enum": ["Anthropic", "OpenAI"], "default": "Anthropic", "x-control": "radioCards", "x-enumLabels": { "Anthropic": "Anthropic (Claude)", "OpenAI": "OpenAI-compatible" }, "x-optionConsequence": { "Anthropic": "Calls Anthropic's Claude models on their native wire.", "OpenAI": "Any OpenAI-compatible gateway (OpenAI, LiteLLM, OpenRouter, vLLM…) set up as a model credential." }, "description": "The wire to use. 'OpenAI' covers any OpenAI-compatible gateway (LiteLLM/OpenRouter/vLLM/…) configured as a model credential." },
                "model": { "type": "string", "description": "Optional. Pins ONE model from the team's credentialed-model pool for this provider; it must be an enabled pool model. Omit to let the pool pick (its recommended model)." },
                "maxTokens": { "type": "integer", "minimum": 1, "default": 2048, "description": "Output-token cap. No artificial ceiling — the transport clamps it to the MODEL's real output ceiling, so pinning a high value never limits a model below its true limit. A value above the streaming threshold (~21k) is STREAMED on the wire so a long generation can't idle-timeout. The default stays small so an ordinary output remains a single buffered call." },
                "temperature": { "type": "number", "minimum": 0, "maximum": 1, "default": 0.2 },
                "topP": { "type": "number", "minimum": 0, "maximum": 1, "description": "Optional nucleus sampling. Omit to leave it at the provider default." },
                "frequencyPenalty": { "type": "number", "minimum": -2, "maximum": 2, "description": "Optional. OpenAI-wire only (ignored on Anthropic, whose API has no equivalent)." },
                "presencePenalty": { "type": "number", "minimum": -2, "maximum": 2, "description": "Optional. OpenAI-wire only." },
                "stop": { "type": "array", "items": { "type": "string" }, "maxItems": 4, "description": "Optional stop sequences — generation halts when any is produced." },
                "responseSchema": { "type": "object", "description": "Optional JSON Schema. When set, the model is constrained to return JSON matching it, surfaced on the 'json' output (downstream can index into it, e.g. {{nodes.this.outputs.json.items[0]}}). Requires a provider that supports structured output." }
              },
              "required": ["provider"]
            }
            """),
        InputSchema = SchemaBuilder.Parse("""
            {
              "type": "object",
              "properties": {
                "systemPrompt": { "type": "string" },
                "userPrompt": { "type": "string", "minLength": 1 }
              },
              "required": ["userPrompt"]
            }
            """),
        OutputSchema = SchemaBuilder.Parse("""
            {
              "type": "object",
              "x-dynamic-output": "json",
              "properties": {
                "text": { "type": "string" },
                "json": { "type": ["object","null"] },
                "model": { "type": "string" },
                "inputTokens": { "type": ["integer","null"] },
                "outputTokens": { "type": ["integer","null"] },
                "finishReason": { "type": ["string","null"], "description": "The provider's stop reason (Anthropic stop_reason / OpenAI finish_reason), e.g. 'stop' / 'length' / 'tool_calls'. Null if not reported." },
                "costUsd": { "type": ["number","null"], "description": "Estimated USD cost of this call from (model, input, output) tokens. Null when the model is unpriced or the provider reported no usage." }
              }
            }
            """)
    };

    public async Task<NodeResult> RunAsync(NodeRunContext context, CancellationToken cancellationToken)
    {
        var provider = ReadString(context.Config, "provider", "Anthropic");
        var modelPin = ReadStringOrNull(context.Config, "model");   // optional pin; null = let the pool pick its recommended model
        var maxTokens = ReadInt(context.Config, "maxTokens", 2048);
        var temperature = ReadDouble(context.Config, "temperature", 0.2);
        var sampling = BuildSampling(context.Config);

        var systemPrompt = ReadString(context.Inputs, "systemPrompt", "");
        var userPrompt = ReadString(context.Inputs, "userPrompt", "");

        if (string.IsNullOrWhiteSpace(userPrompt)) return NodeResult.Fail("Input 'userPrompt' is required.");

        var client = _clientRegistry.Resolve(provider);

        var useStructured = TryReadObject(context.Config, "responseSchema", out var responseSchema);

        // Pure pool-driven (S6b): the model + credential come ENTIRELY from the team's credentialed-model pool for this
        // provider — no env key, no default model. The config 'model' is a PIN (must be an enabled pool model), else the
        // pool's recommended one. The pool is GENERIC — it never gates on a capability flag; a schema-bearing node just
        // routes through the structured client (which degrades a non-structured model). Fail closed when nothing
        // qualifies — never silently substitute or reach an ambient env key.
        if (!NodeScopeReader.TryReadTeamId(context, out var teamId))
            return NodeResult.Fail("The run carries no team context — llm.complete resolves its model + credential from the team's pool.");

        var pick = await ResolvePoolPickAsync(teamId, provider, modelPin, cancellationToken).ConfigureAwait(false);

        if (pick is null)
            return NodeResult.Fail(NoPoolModelMessage(provider, modelPin));

        // Structured mode: a responseSchema constrains the model to schema-valid JSON, surfaced on the
        // 'json' output. Routes through the IStructuredLLMClient sibling capability — clean fail if the
        // resolved provider doesn't offer it.
        if (useStructured)
            return await RunStructuredAsync(context, client, provider, pick, systemPrompt, userPrompt, maxTokens, temperature, sampling, responseSchema, cancellationToken).ConfigureAwait(false);

        // Wrap the LLM call with ledger emission. The completed record's response_payload
        // carries the token counts (cheap to inline). The full text lives in workflow_artifact,
        // keeping the ledger payload small while still preserving the model output for replay
        // / audit.
        var request = new LLMCompletionRequest
        {
            Model = pick.ModelId,
            Credential = pick.Credential,
            SystemPrompt = systemPrompt,
            UserPrompt = userPrompt,
            MaxOutputTokens = maxTokens,
            Temperature = temperature,
            Sampling = sampling,
        };
        var target = $"{provider.ToLowerInvariant()}:{pick.ModelId}";
        var audit = BuildRequestPayloadAudit(pick.ModelId, systemPrompt, userPrompt, maxTokens, temperature);

        // A LARGE output (an explicit maxTokens above the streaming gate) STREAMS through the streaming sibling: the deltas
        // flow through the recording decorator as interaction.delta AND the wire can't idle-timeout, while the traced
        // stream folds back into the SAME whole completion the buffered path returns. A small output stays a single
        // buffered call — only an explicit large maxTokens is streamed, never forced onto every node call.
        LLMCompletion completion;
        if (maxTokens > LlmModelCapabilities.StreamingThreshold && client is IStreamingLLMClient streaming)
        {
            var traced = context.Observability.TraceExternalStreamAsync(target, "complete", audit, ct => streaming.StreamAsync(request, ct), cancellationToken);
            completion = await LlmTextStreamFold.AccumulateAsync(traced, request.Model, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            completion = await context.Observability.TraceExternalCallAsync(target, "complete", audit,
                ct => client.CompleteAsync(request, ct),
                result => LedgerCompletion(result.Model, result.Usage, result.Text?.Length ?? 0),
                cancellationToken).ConfigureAwait(false);
        }

        context.Logger.LogInformation("LLM completion {Model} in={InTok} out={OutTok} finish={Finish}", completion.Model, completion.Usage.InputTokens, completion.Usage.OutputTokens, completion.Usage.FinishReason);

        var outputs = BuildOutputs(JsonSerializer.SerializeToElement(completion.Text), JsonSerializer.SerializeToElement((object?)null), completion.Model, completion.Usage);

        return NodeResult.Ok(outputs);
    }

    private async Task<NodeResult> RunStructuredAsync(NodeRunContext context, ILLMClient client, string provider, ModelPoolPick pick, string systemPrompt, string userPrompt, int maxTokens, double temperature, LlmSamplingOptions? sampling, JsonElement responseSchema, CancellationToken cancellationToken)
    {
        if (client is not IStructuredLLMClient structured)
            return NodeResult.Fail($"Provider '{provider}' doesn't support structured output (responseSchema). Remove the schema to use plain text, or pick a provider that does.");

        var completion = await context.Observability.TraceExternalCallAsync(
            target: $"{provider.ToLowerInvariant()}:{pick.ModelId}",
            method: "complete_structured",
            requestPayload: BuildRequestPayloadAudit(pick.ModelId, systemPrompt, userPrompt, maxTokens, temperature),
            action: ct => structured.CompleteStructuredAsync(new StructuredLLMCompletionRequest
            {
                Model = pick.ModelId,
                Credential = pick.Credential,
                SystemPrompt = systemPrompt,
                UserPrompt = userPrompt,
                JsonSchema = responseSchema,
                MaxOutputTokens = maxTokens,
                Temperature = temperature,
                Sampling = sampling
            }, ct),
            completionExtractor: result => LedgerCompletion(result.Model, result.Usage),
            cancellationToken: cancellationToken).ConfigureAwait(false);

        context.Logger.LogInformation("LLM structured completion {Model} in={InTok} out={OutTok} finish={Finish}", completion.Model, completion.Usage.InputTokens, completion.Usage.OutputTokens, completion.Usage.FinishReason);

        // Emit the parsed object on 'json' (downstream can index into it); keep 'text' populated with the
        // serialized form so a node wired to either output still works.
        var outputs = BuildOutputs(JsonSerializer.SerializeToElement(completion.Json.GetRawText()), completion.Json, completion.Model, completion.Usage);

        return NodeResult.Ok(outputs);
    }

    /// <summary>The node's output map for both paths — text/json differ, the model + usage envelope (tokens, finish reason, derived USD cost) are shared. Keeping it one place means the two completion shapes can't drift in what they surface.</summary>
    private static Dictionary<string, JsonElement> BuildOutputs(JsonElement text, JsonElement json, string model, LlmUsage usage) => new()
    {
        ["text"] = text,
        ["json"] = json,
        ["model"] = JsonSerializer.SerializeToElement(model),
        ["inputTokens"] = JsonSerializer.SerializeToElement(usage.InputTokens),
        ["outputTokens"] = JsonSerializer.SerializeToElement(usage.OutputTokens),
        ["finishReason"] = JsonSerializer.SerializeToElement(usage.FinishReason),
        ["costUsd"] = JsonSerializer.SerializeToElement(CostUsdOf(model, usage))
    };

    /// <summary>The redacted ledger record for one LLM call — model + the usage envelope (tokens, finish reason, derived cost); the full text lives in a workflow_artifact, so only its length rides here. Shared by the plain + structured paths so the audit payload can't drift.</summary>
    private static ExternalCallCompletion LedgerCompletion(string model, LlmUsage usage, int? textLength = null) => new()
    {
        ResponsePayload = JsonSerializer.SerializeToElement(new
        {
            model,
            input_tokens = usage.InputTokens,
            output_tokens = usage.OutputTokens,
            finish_reason = usage.FinishReason,
            cost_usd = CostUsdOf(model, usage),
            text_length = textLength
        })
    };

    /// <summary>Derive the USD cost of one call from (model, in, out) via the shared pricing table. Null when the provider reported no token counts (can't price) or the model is unpriced — fail-open, never throws (matches <c>AgentCostPricing</c>).</summary>
    private static decimal? CostUsdOf(string model, LlmUsage usage) =>
        usage.InputTokens is { } input && usage.OutputTokens is { } output ? AgentCostPricing.CostUsd(model, input, output) : null;

    /// <summary>Reads a non-empty JSON object from the bag (an empty <c>{}</c> counts as absent → text path).</summary>
    private static bool TryReadObject(IReadOnlyDictionary<string, JsonElement> bag, string key, out JsonElement value)
    {
        value = default;
        if (!bag.TryGetValue(key, out var v) || v.ValueKind != JsonValueKind.Object) return false;
        if (!v.EnumerateObject().Any()) return false;

        value = v;
        return true;
    }

    /// <summary>
    /// Build the redacted summary of the LLM request for the ledger. We persist counts +
    /// model, NOT the prompts themselves — prompts can carry tenant data and are kept in the
    /// engine's resolved-inputs path (already redacted upstream) instead of the call-side
    /// audit. Total prompt characters is enough to triage "why was this call so expensive".
    /// </summary>
    private static JsonElement BuildRequestPayloadAudit(string model, string systemPrompt, string userPrompt, int maxTokens, double temperature) =>
        JsonSerializer.SerializeToElement(new
        {
            model,
            max_tokens = maxTokens,
            temperature,
            system_prompt_chars = systemPrompt?.Length ?? 0,
            user_prompt_chars = userPrompt?.Length ?? 0,
        });

    /// <summary>Resolve the pool pick in a FRESH DI scope (the node is a singleton — it must not capture the scoped selector's DbContext, which concurrent map branches would share + collide on). The pick is a detached POCO, safe to use after the scope disposes.</summary>
    private async Task<ModelPoolPick?> ResolvePoolPickAsync(Guid teamId, string provider, string? modelPin, CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var selector = scope.ServiceProvider.GetRequiredService<IModelPoolSelector>();

        return await selector.SelectAsync(teamId, provider, allowedModels: null, pinnedModel: modelPin, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The fail-closed message when nothing in the team's pool qualifies — names the provider + the pin (if any) so the operator knows exactly what to add to the pool.</summary>
    private static string NoPoolModelMessage(string provider, string? modelPin)
    {
        var pinPart = modelPin is null ? "" : $" matching '{modelPin}'";
        return $"No model is available in the team's pool for provider '{provider}'{pinPart}. Add a credentialed, enabled model to run this node.";
    }

    private static string ReadString(IReadOnlyDictionary<string, JsonElement> bag, string key, string fallback)
    {
        if (!bag.TryGetValue(key, out var value)) return fallback;
        if (value.ValueKind != JsonValueKind.String) return fallback;
        return value.GetString() ?? fallback;
    }

    /// <summary>An optional string config value — null when absent, non-string, or blank (so an empty <c>model</c> means "no pin", not a pin on "").</summary>
    private static string? ReadStringOrNull(IReadOnlyDictionary<string, JsonElement> bag, string key)
    {
        if (!bag.TryGetValue(key, out var value) || value.ValueKind != JsonValueKind.String) return null;
        var s = value.GetString();
        return string.IsNullOrWhiteSpace(s) ? null : s;
    }

    private static int ReadInt(IReadOnlyDictionary<string, JsonElement> bag, string key, int fallback)
    {
        if (!bag.TryGetValue(key, out var value)) return fallback;
        if (value.ValueKind != JsonValueKind.Number) return fallback;
        return value.TryGetInt32(out var i) ? i : fallback;
    }

    private static double ReadDouble(IReadOnlyDictionary<string, JsonElement> bag, string key, double fallback)
    {
        if (!bag.TryGetValue(key, out var value)) return fallback;
        if (value.ValueKind != JsonValueKind.Number) return fallback;
        return value.TryGetDouble(out var d) ? d : fallback;
    }

    /// <summary>An optional number config value — null when absent / non-numeric (so a knob is sent only when the author set it).</summary>
    private static double? ReadDoubleOrNull(IReadOnlyDictionary<string, JsonElement> bag, string key) =>
        bag.TryGetValue(key, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var d) ? d : null;

    /// <summary>An optional non-empty string-array config value — null when absent / not an array / empty.</summary>
    private static IReadOnlyList<string>? ReadStringArrayOrNull(IReadOnlyDictionary<string, JsonElement> bag, string key)
    {
        if (!bag.TryGetValue(key, out var value) || value.ValueKind != JsonValueKind.Array) return null;

        var list = value.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!).ToList();
        return list.Count > 0 ? list : null;
    }

    /// <summary>The optional sampling knobs — null when the author set NONE (so the request is byte-identical to the temperature-only path). Each field is independently optional. Internal so the config→options mapping is unit-pinned directly (InternalsVisibleTo).</summary>
    internal static LlmSamplingOptions? BuildSampling(IReadOnlyDictionary<string, JsonElement> config)
    {
        var topP = ReadDoubleOrNull(config, "topP");
        var frequencyPenalty = ReadDoubleOrNull(config, "frequencyPenalty");
        var presencePenalty = ReadDoubleOrNull(config, "presencePenalty");
        var stop = ReadStringArrayOrNull(config, "stop");

        if (topP is null && frequencyPenalty is null && presencePenalty is null && stop is null) return null;

        return new LlmSamplingOptions { TopP = topP, FrequencyPenalty = frequencyPenalty, PresencePenalty = presencePenalty, Stop = stop };
    }
}
