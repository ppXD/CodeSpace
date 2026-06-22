using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using CodeSpace.Messages.Agents;

namespace CodeSpace.Core.Services.Workflows.Llm.OpenAi;

/// <summary>
/// OpenAI-compatible Chat Completions client — the sibling of <see cref="Anthropic.AnthropicClient"/> for any
/// gateway that speaks the OpenAI wire (<c>POST {BaseUrl}/chat/completions</c>, <c>Authorization: Bearer</c>):
/// OpenAI itself, LiteLLM, OpenRouter, vLLM, Together, a local gateway, … Selected by the credential's
/// <see cref="ResolvedModelCredential.Provider"/> == <c>"OpenAI"</c> (the registry resolves the client whose
/// Provider matches), so a team configures an OpenAI-wire endpoint purely as a model credential — no code change.
///
/// The API key + base URL come ENTIRELY from the per-call <see cref="ResolvedModelCredential"/> (the in-process
/// plane resolves it from the team's pool); there is NO ambient env-key backstop (mirrors the Anthropic client
/// post-S6b) — a call without a credential fails closed.
///
/// Implements <see cref="ILLMClient"/> (free-text) AND <see cref="IStructuredLLMClient"/> (schema-constrained
/// JSON). Structured output uses FORCED FUNCTION-CALLING — a single function whose <c>parameters</c> IS the
/// requested schema, with <c>tool_choice</c> pinned to it — rather than <c>response_format: json_schema</c>,
/// because function-calling is supported by far more OpenAI-compatible gateways than the newer structured-outputs
/// feature, and it mirrors the Anthropic client's forced-tool design exactly (one coercion mechanism to reason
/// about). The model's <c>tool_calls[0].function.arguments</c> is the schema-SHAPED JSON (classic function-calling
/// steers but does not strictly validate against the schema). It is accepted whether the gateway returns it as a
/// JSON string (OpenAI) or an inline object (some gateways).
///
/// <para><b>BaseUrl convention</b>: the OpenAI API base, INCLUDING any version segment the gateway needs (e.g.
/// <c>https://api.openai.com/v1</c> or <c>https://your-gateway/v1</c>); the client appends <c>/chat/completions</c>.
/// A trailing slash is tolerated. A base WITHOUT the version segment will 404 — the thrown error names the URL it
/// hit so a missing <c>/v1</c> is diagnosable. Keep ONLY wire-shape concerns here — prompt assembly / retry belong
/// in the node.</para>
///
/// <para><b>Parameter compatibility (v1)</b>: emits <c>max_tokens</c> + <c>temperature</c> — accepted by
/// <c>gpt-4o</c>-class models and the common gateways (LiteLLM/OpenRouter/vLLM, which translate). OpenAI's DIRECT
/// reasoning models (o1/o3/GPT-5-reasoning) reject <c>max_tokens</c> (want <c>max_completion_tokens</c>) and a
/// non-default <c>temperature</c>; a custom endpoint is almost always a translating gateway, so this is v1-safe —
/// a dedicated reasoning-model parameter slice follows if needed.</para>
/// </summary>
public sealed class OpenAiClient : ILLMClient, IStructuredLLMClient
{
    public const string DefaultApiBaseUrl = "https://api.openai.com/v1";

    /// <summary>The single forced function used to coerce structured output; the model returns its arguments as the JSON.</summary>
    private const string StructuredToolName = "respond";

    public string Provider => "OpenAI";

    private readonly IHttpClientFactory _httpClientFactory;

    public OpenAiClient(IHttpClientFactory httpClientFactory) { _httpClientFactory = httpClientFactory; }

    public async Task<LLMCompletion> CompleteAsync(LLMCompletionRequest request, CancellationToken cancellationToken)
    {
        var body = new OpenAiChatRequest
        {
            Model = request.Model,
            MaxTokens = request.MaxOutputTokens,
            Temperature = request.Temperature,
            TopP = request.Sampling?.TopP,
            FrequencyPenalty = request.Sampling?.FrequencyPenalty,
            PresencePenalty = request.Sampling?.PresencePenalty,
            Stop = request.Sampling?.Stop,
            Messages = BuildMessages(request.SystemPrompt, request.UserPrompt),
        };

        var parsed = await PostChatAsync(body, request.Credential, cancellationToken).ConfigureAwait(false);

        var message = parsed.Choices?.FirstOrDefault()?.Message;

        return new LLMCompletion
        {
            Text = message?.Content ?? "",
            Model = parsed.Model ?? request.Model,
            InputTokens = parsed.Usage?.PromptTokens,
            OutputTokens = parsed.Usage?.CompletionTokens,
        };
    }

    public async Task<StructuredLLMCompletion> CompleteStructuredAsync(StructuredLLMCompletionRequest request, CancellationToken cancellationToken)
    {
        // Get the JSON via the progressive path, then VALIDATE it against the requested schema — a recovered object that
        // is missing a required field / has a wrong-typed value / an invalid enum is NOT success. On a validation miss,
        // RE-ASK ONCE with the exact violations named, then re-validate; a second miss is a typed Malformed fault.
        var first = await CompleteStructuredOnceAsync(request, request.SystemPrompt, cancellationToken).ConfigureAwait(false);
        var errors = JsonSchemaValidator.Validate(first.Json, request.JsonSchema);
        if (errors.Count == 0) return first;

        var feedbackSystem = StructuredJsonText.WithValidationFeedback(request.SystemPrompt, errors, first.Json);
        var second = await CompleteStructuredOnceAsync(request, feedbackSystem, cancellationToken).ConfigureAwait(false);
        var errors2 = JsonSchemaValidator.Validate(second.Json, request.JsonSchema);
        if (errors2.Count == 0) return second;

        throw new LlmApiException(Provider, null, LlmErrorCategory.Malformed,
            $"structured output failed schema validation after a re-ask: {string.Join("; ", errors2)}");
    }

    /// <summary>One progressive structured attempt (forced function-calling → prompt-only floor) against the given base system prompt — the re-ask passes a feedback-augmented base so the SAME path retries with the validation errors named.</summary>
    private async Task<StructuredLLMCompletion> CompleteStructuredOnceAsync(StructuredLLMCompletionRequest request, string baseSystemPrompt, CancellationToken cancellationToken)
    {
        var system = StructuredJsonText.WithSchemaInstruction(baseSystemPrompt, request.JsonSchema);

        // Attempt 1 — forced function-calling. A gateway/model that rejects it (400) or returns neither a function
        // call nor a JSON content object yields null here, degrading to the prompt-only floor below.
        if (await TryStructuredViaFunctionAsync(request, system, cancellationToken).ConfigureAwait(false) is { } viaFunction)
            return viaFunction;

        // Attempt 2 — prompt-only floor: no tools, no response_format. This is the SAFEST request — it never 400s on a
        // gateway that rejects forced functions or json_object, and forces nothing that could suppress the reply. The
        // model answers in text; recover the JSON object from it.
        var body = new OpenAiChatRequest
        {
            Model = request.Model,
            MaxTokens = request.MaxOutputTokens,
            Temperature = request.Temperature,
            TopP = request.Sampling?.TopP,
            FrequencyPenalty = request.Sampling?.FrequencyPenalty,
            PresencePenalty = request.Sampling?.PresencePenalty,
            Stop = request.Sampling?.Stop,
            Messages = BuildMessages(system, request.UserPrompt),
        };

        var parsed = await PostChatAsync(body, request.Credential, cancellationToken).ConfigureAwait(false);
        var message = parsed.Choices?.FirstOrDefault()?.Message;

        if (StructuredJsonText.TryExtractObject(message?.Content) is not { } result)
        {
            var refusal = string.IsNullOrWhiteSpace(message?.Refusal) ? "" : $" (refusal: {message!.Refusal})";
            throw new InvalidOperationException($"OpenAI structured completion produced no JSON via forced function-calling OR the prompt-only fallback — the model did not produce structured output{refusal}. Content preview: {StructuredJsonText.Preview(message?.Content)}");
        }

        return BuildCompletion(result, parsed, request.Model);
    }

    /// <summary>
    /// Attempt 1: forced function-calling — a single function whose parameters IS the schema, tool_choice pinned to it.
    /// Returns null (degrade to the prompt-only floor) when the gateway REJECTS the request (e.g. 400 on an unsupported
    /// feature) or the model returns neither a function call nor a recoverable JSON content object.
    /// </summary>
    private async Task<StructuredLLMCompletion?> TryStructuredViaFunctionAsync(StructuredLLMCompletionRequest request, string system, CancellationToken cancellationToken)
    {
        var body = new OpenAiChatRequest
        {
            Model = request.Model,
            MaxTokens = request.MaxOutputTokens,
            Temperature = request.Temperature,
            TopP = request.Sampling?.TopP,
            FrequencyPenalty = request.Sampling?.FrequencyPenalty,
            PresencePenalty = request.Sampling?.PresencePenalty,
            Stop = request.Sampling?.Stop,
            Messages = BuildMessages(system, request.UserPrompt),
            Tools = new[] { new OpenAiTool { Function = new OpenAiFunction { Name = StructuredToolName, Description = "Return the result as structured JSON.", Parameters = request.JsonSchema } } },
            ToolChoice = new OpenAiToolChoice { Function = new OpenAiToolChoiceFunction { Name = StructuredToolName } },
        };

        OpenAiChatResponse parsed;
        try { parsed = await PostChatAsync(body, request.Credential, cancellationToken).ConfigureAwait(false); }
        catch (LlmApiException e) when (e.StatusCode is 400 or 422) { return null; }   // ANY request-shape rejection (400/422 — forced functions unsupported) degrades to the floor, regardless of the refined category (a body keyword must never disable the fallback). A 401/403/404/429/5xx PROPAGATES — never swallowed into a second billable call that mis-reports the real cause.

        var message = parsed.Choices?.FirstOrDefault()?.Message;
        var toolCall = message?.ToolCalls?.FirstOrDefault(t => t.Function?.Name == StructuredToolName);

        var json = toolCall?.Function?.Arguments is { } arguments
            ? ParseToolArguments(arguments)
            : StructuredJsonText.TryExtractObject(message?.Content);

        return json is { } result ? BuildCompletion(result, parsed, request.Model) : null;
    }

    private static StructuredLLMCompletion BuildCompletion(JsonElement json, OpenAiChatResponse parsed, string fallbackModel) => new()
    {
        Json = json,
        Model = parsed.Model ?? fallbackModel,
        InputTokens = parsed.Usage?.PromptTokens,
        OutputTokens = parsed.Usage?.CompletionTokens,
    };

    /// <summary>Accept the function-call arguments whether the gateway returns them as a JSON STRING (OpenAI) or an inline OBJECT (some gateways) — a real-endpoint shape the fake-HTTP tests would otherwise let through.</summary>
    private static JsonElement ParseToolArguments(JsonElement arguments) => arguments.ValueKind switch
    {
        JsonValueKind.Object => arguments.Clone(),
        JsonValueKind.String => ParseArgumentString(arguments.GetString()),
        _ => throw new InvalidOperationException($"OpenAI structured completion returned tool_call arguments of unexpected kind {arguments.ValueKind}."),
    };

    private static JsonElement ParseArgumentString(string? argumentJson)
    {
        try { return JsonDocument.Parse(argumentJson ?? "").RootElement.Clone(); }
        catch (JsonException ex) { throw new InvalidOperationException($"OpenAI structured completion returned non-JSON function arguments: {ex.Message}"); }
    }

    private static IReadOnlyList<OpenAiMessage> BuildMessages(string systemPrompt, string userPrompt) => new[]
    {
        new OpenAiMessage { Role = "system", Content = systemPrompt },
        new OpenAiMessage { Role = "user", Content = userPrompt },
    };

    private async Task<OpenAiChatResponse> PostChatAsync(OpenAiChatRequest body, ResolvedModelCredential? credential, CancellationToken cancellationToken)
    {
        // Pure pool-driven (mirrors AnthropicClient post-S6b): the credential is the ONLY key source — no ambient
        // env-key backstop. A caller without a credential fails closed rather than silently borrowing a global key.
        var apiKey = NullIfBlank(credential?.ApiKey);

        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException("OpenAI API key not configured. The in-process plane must pass a model credential resolved from the team's pool.");

        var baseUrl = NullIfBlank(credential?.BaseUrl) ?? DefaultApiBaseUrl;
        var url = baseUrl.TrimEnd('/') + "/chat/completions";

        var http = _httpClientFactory.CreateClient(nameof(OpenAiClient));

        // Per-call Authorization on the REQUEST message (never the singleton client's DefaultRequestHeaders) so two
        // concurrent flow.map branches can't bleed one team's Bearer onto another's call. Buffered body so the resilience
        // handler can re-send it on a transient retry.
        using var message = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = LlmHttpTransport.JsonBody(body),
        };
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        return await LlmHttpTransport.SendForJsonAsync<OpenAiChatResponse>(http, message, Provider, ResponseJsonOptions, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Case-insensitive so a non-conformant gateway that capitalises response keys (e.g. <c>Choices</c>) still deserializes.</summary>
    private static readonly JsonSerializerOptions ResponseJsonOptions = new() { PropertyNameCaseInsensitive = true };

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    // ── Wire DTOs (OpenAI Chat Completions) ──────────────────────────────────────────────────────────────────

    private sealed class OpenAiChatRequest
    {
        [JsonPropertyName("model")] public required string Model { get; init; }
        [JsonPropertyName("max_tokens")] public required int MaxTokens { get; init; }
        [JsonPropertyName("temperature")] public required double Temperature { get; init; }
        [JsonPropertyName("messages")] public required IReadOnlyList<OpenAiMessage> Messages { get; init; }
        [JsonPropertyName("top_p")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public double? TopP { get; init; }
        [JsonPropertyName("frequency_penalty")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public double? FrequencyPenalty { get; init; }
        [JsonPropertyName("presence_penalty")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public double? PresencePenalty { get; init; }
        [JsonPropertyName("stop")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public IReadOnlyList<string>? Stop { get; init; }
        [JsonPropertyName("tools")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public IReadOnlyList<OpenAiTool>? Tools { get; init; }
        [JsonPropertyName("tool_choice")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public OpenAiToolChoice? ToolChoice { get; init; }
    }

    private sealed class OpenAiMessage
    {
        [JsonPropertyName("role")] public required string Role { get; init; }
        [JsonPropertyName("content")] public string? Content { get; init; }
        [JsonPropertyName("tool_calls")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public IReadOnlyList<OpenAiToolCall>? ToolCalls { get; init; }
    }

    private sealed class OpenAiTool
    {
        [JsonPropertyName("type")] public string Type => "function";
        [JsonPropertyName("function")] public required OpenAiFunction Function { get; init; }
    }

    private sealed class OpenAiFunction
    {
        [JsonPropertyName("name")] public required string Name { get; init; }
        [JsonPropertyName("description")] public string? Description { get; init; }
        [JsonPropertyName("parameters")] public required JsonElement Parameters { get; init; }
    }

    private sealed class OpenAiToolChoice
    {
        [JsonPropertyName("type")] public string Type => "function";
        [JsonPropertyName("function")] public required OpenAiToolChoiceFunction Function { get; init; }
    }

    private sealed class OpenAiToolChoiceFunction
    {
        [JsonPropertyName("name")] public required string Name { get; init; }
    }

    private sealed class OpenAiChatResponse
    {
        [JsonPropertyName("model")] public string? Model { get; init; }
        [JsonPropertyName("choices")] public IReadOnlyList<OpenAiChoice>? Choices { get; init; }
        [JsonPropertyName("usage")] public OpenAiUsage? Usage { get; init; }
    }

    private sealed class OpenAiChoice
    {
        [JsonPropertyName("message")] public OpenAiResponseMessage? Message { get; init; }
    }

    private sealed class OpenAiResponseMessage
    {
        [JsonPropertyName("content")] public string? Content { get; init; }
        [JsonPropertyName("refusal")] public string? Refusal { get; init; }
        [JsonPropertyName("tool_calls")] public IReadOnlyList<OpenAiToolCall>? ToolCalls { get; init; }
    }

    private sealed class OpenAiToolCall
    {
        [JsonPropertyName("function")] public OpenAiToolCallFunction? Function { get; init; }
    }

    private sealed class OpenAiToolCallFunction
    {
        [JsonPropertyName("name")] public string? Name { get; init; }

        /// <summary>The schema-shaped JSON the model produced — a STRING on OpenAI, occasionally an inline OBJECT on some gateways. Typed as <see cref="JsonElement"/> so both wire shapes deserialize (see <c>ParseToolArguments</c>).</summary>
        [JsonPropertyName("arguments")] public JsonElement? Arguments { get; init; }
    }

    private sealed class OpenAiUsage
    {
        [JsonPropertyName("prompt_tokens")] public int? PromptTokens { get; init; }
        [JsonPropertyName("completion_tokens")] public int? CompletionTokens { get; init; }
    }
}
