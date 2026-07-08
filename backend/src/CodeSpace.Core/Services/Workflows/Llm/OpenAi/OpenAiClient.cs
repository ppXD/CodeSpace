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
/// <para><b>Parameter compatibility</b>: the body is RECONCILED against the target model via <see cref="LlmModelCapabilities"/>
/// (the in-process <c>get_supported_openai_params</c>/<c>drop_params</c> analogue): the output cap rides as
/// <c>max_completion_tokens</c> for a reasoning model (o1/o3/o4/gpt-5, which 400 on the deprecated <c>max_tokens</c>) and as
/// the classic, universally-understood <c>max_tokens</c> otherwise; <c>temperature</c> + <c>top_p</c> + penalties are DROPPED
/// for a reasoning model that only accepts the defaults. A null output cap is omitted entirely (the model runs to its
/// context limit). So a pinned param never 400s a reasoning endpoint, and a plain gateway model is sent the widely-supported
/// shape.</para>
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
        var accepts = LlmModelCapabilities.AcceptsSampling(request.Model);
        var useCompletionTokens = LlmModelCapabilities.UsesMaxCompletionTokens(request.Model);
        var (cap, stream) = LlmModelCapabilities.ResolveOutputBudget(request.Model, request.MaxOutputTokens, requiresField: false);

        var body = new OpenAiChatRequest
        {
            Model = request.Model,
            // A set cap rides as `max_completion_tokens` for a reasoning model (which 400s on the deprecated `max_tokens`)
            // and as the classic, universally-understood `max_tokens` otherwise; a NULL cap OMITS both (the model runs to
            // its context limit — OpenAI allows this). Streaming is chosen when the output is large / unbounded so a slow
            // generation can't idle-timeout; a small explicit cap stays non-streaming, byte-identical to before.
            MaxTokens = useCompletionTokens ? null : cap,
            MaxCompletionTokens = useCompletionTokens ? cap : null,
            Temperature = accepts ? request.Temperature : null,
            TopP = accepts ? request.Sampling?.TopP : null,
            FrequencyPenalty = accepts ? request.Sampling?.FrequencyPenalty : null,
            PresencePenalty = accepts ? request.Sampling?.PresencePenalty : null,
            Stop = request.Sampling?.Stop,
            Messages = BuildMessages(request.SystemPrompt, request.UserPrompt),
            Stream = stream ? true : null,                                                    // omitted when false → unchanged non-streaming body
            StreamOptions = stream ? new OpenAiStreamOptions { IncludeUsage = true } : null,  // ask the wire to emit a final usage chunk (LiteLLM/OpenRouter/vLLM honour it; a gateway that ignores it just leaves usage null)
        };

        return stream
            ? await CompleteStreamingAsync(body, request.Model, request.Credential, cancellationToken).ConfigureAwait(false)
            : await CompleteBufferedAsync(body, request.Model, request.Credential, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The non-streaming path (small / bounded output) — one buffered POST, unchanged from the pre-streaming client.</summary>
    private async Task<LLMCompletion> CompleteBufferedAsync(OpenAiChatRequest body, string fallbackModel, ResolvedModelCredential? credential, CancellationToken cancellationToken)
    {
        var parsed = await PostChatAsync(body, credential, cancellationToken).ConfigureAwait(false);
        var message = parsed.Choices?.FirstOrDefault()?.Message;

        return new LLMCompletion { Text = message?.Content ?? "", Model = parsed.Model ?? fallbackModel, Usage = UsageFrom(parsed) };
    }

    /// <summary>
    /// The streaming path (large / unbounded output): accumulate the SSE chunk stream into the SAME <see cref="LLMCompletion"/>.
    /// OpenAI's chunks carry incremental <c>choices[0].delta.content</c> (the text), a terminal <c>finish_reason</c>, and —
    /// with <c>stream_options.include_usage</c> — a final usage chunk (<c>prompt_tokens</c>/<c>completion_tokens</c>); all are
    /// folded here so the caller sees no difference from the buffered path except the output is no longer capped for fear of a timeout.
    /// </summary>
    private async Task<LLMCompletion> CompleteStreamingAsync(OpenAiChatRequest body, string fallbackModel, ResolvedModelCredential? credential, CancellationToken cancellationToken)
    {
        var (http, message) = BuildRequest(body, credential);

        using (message)
        {
            var text = new System.Text.StringBuilder();
            string? model = null, finishReason = null;
            int? inputTokens = null, outputTokens = null;

            await foreach (var evt in LlmHttpTransport.StreamSseAsync(http, message, Provider, cancellationToken).ConfigureAwait(false))
            {
                if (evt.TryGetProperty("model", out var m) && m.ValueKind == JsonValueKind.String) model = m.GetString();

                if (evt.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array && choices.GetArrayLength() > 0)
                {
                    var choice = choices[0];
                    if (choice.TryGetProperty("delta", out var delta) && delta.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String) text.Append(c.GetString());
                    if (choice.TryGetProperty("finish_reason", out var fr) && fr.ValueKind == JsonValueKind.String) finishReason = fr.GetString();
                }

                if (evt.TryGetProperty("usage", out var u) && u.ValueKind == JsonValueKind.Object)
                {
                    if (u.TryGetProperty("prompt_tokens", out var pt) && pt.TryGetInt32(out var pv)) inputTokens = pv;
                    if (u.TryGetProperty("completion_tokens", out var ct) && ct.TryGetInt32(out var cv)) outputTokens = cv;
                }
            }

            return new LLMCompletion
            {
                Text = text.ToString(),
                Model = model ?? fallbackModel,
                Usage = new LlmUsage { InputTokens = inputTokens, OutputTokens = outputTokens, FinishReason = finishReason },
            };
        }
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
        if (errors2.Count == 0) return second with { Usage = first.Usage.Add(second.Usage) };   // total billed = the first (invalid) attempt + the re-ask

        throw new LlmApiException(Provider, null, LlmErrorCategory.Malformed,
            $"structured output failed schema validation after a re-ask: {string.Join("; ", errors2)}");
    }

    /// <summary>One progressive structured attempt (forced function-calling → prompt-only floor) against the given base system prompt — the re-ask passes a feedback-augmented base so the SAME path retries with the validation errors named.</summary>
    private async Task<StructuredLLMCompletion> CompleteStructuredOnceAsync(StructuredLLMCompletionRequest request, string baseSystemPrompt, CancellationToken cancellationToken)
    {
        var system = StructuredJsonText.WithSchemaInstruction(baseSystemPrompt, request.JsonSchema);

        // Attempt 1 — forced function-calling. A 400/422 reject (funcParsed null, nothing billed) or a 200 that yields no
        // usable JSON degrades to the prompt-only floor below; a 200 WITH a result returns here carrying its own usage.
        var (funcJson, funcParsed) = await TryStructuredViaFunctionAsync(request, system, cancellationToken).ConfigureAwait(false);
        if (funcJson is { } viaFunction)
            return BuildCompletion(viaFunction, funcParsed!, request.Model);

        var accepts = LlmModelCapabilities.AcceptsSampling(request.Model);
        var useCompletionTokens = LlmModelCapabilities.UsesMaxCompletionTokens(request.Model);

        // Attempt 2 — prompt-only floor: no tools, no response_format. The SAFEST request — never 400s on a gateway that
        // rejects forced functions or json_object. The model answers in text; recover the JSON from it. The returned
        // usage TOTALS the (billed but degraded) forced attempt + this floor, so the caller sees what was actually billed.
        var body = new OpenAiChatRequest
        {
            Model = request.Model,
            // Null cap ⇒ OMIT both (the model runs to its context limit — OpenAI allows this, unlike Anthropic). A set cap
            // rides as `max_completion_tokens` for a reasoning model (which 400s on the deprecated `max_tokens`) and as the
            // classic, universally-understood `max_tokens` otherwise (an older OpenAI-compatible gateway may not know the new name).
            MaxTokens = useCompletionTokens ? null : request.MaxOutputTokens,
            MaxCompletionTokens = useCompletionTokens ? request.MaxOutputTokens : null,
            Temperature = accepts ? request.Temperature : null,
            TopP = accepts ? request.Sampling?.TopP : null,
            FrequencyPenalty = accepts ? request.Sampling?.FrequencyPenalty : null,
            PresencePenalty = accepts ? request.Sampling?.PresencePenalty : null,
            Stop = request.Sampling?.Stop,
            Messages = BuildMessages(system, request.UserPrompt),
        };

        var parsed = await PostChatAsync(body, request.Credential, cancellationToken).ConfigureAwait(false);
        var message = parsed.Choices?.FirstOrDefault()?.Message;

        if (StructuredJsonText.TryExtractObject(message?.Content) is not { } result)
        {
            var refusal = string.IsNullOrWhiteSpace(message?.Refusal) ? "" : $" (refusal: {message!.Refusal})";
            throw new LlmApiException(Provider, null, LlmErrorCategory.Malformed, $"structured completion produced no JSON via forced function-calling OR the prompt-only fallback — the model did not produce structured output{refusal}. Content preview: {StructuredJsonText.Preview(message?.Content)}");
        }

        var totalUsage = (funcParsed is null ? LlmUsage.None : UsageFrom(funcParsed)).Add(UsageFrom(parsed));
        return BuildCompletion(result, parsed, request.Model) with { Usage = totalUsage };
    }

    /// <summary>
    /// Attempt 1: forced function-calling — a single function whose parameters IS the schema, tool_choice pinned to it.
    /// Returns the recovered JSON (or null to degrade to the prompt-only floor) PLUS the parsed response — so the caller
    /// can accumulate the BILLED usage even when the JSON is null (a 200 that produced no usable function call still
    /// cost tokens). On a 400/422 reject the request was never generated, so both are null (nothing to bill).
    /// </summary>
    private async Task<(JsonElement? Json, OpenAiChatResponse? Parsed)> TryStructuredViaFunctionAsync(StructuredLLMCompletionRequest request, string system, CancellationToken cancellationToken)
    {
        var accepts = LlmModelCapabilities.AcceptsSampling(request.Model);
        var useCompletionTokens = LlmModelCapabilities.UsesMaxCompletionTokens(request.Model);

        var body = new OpenAiChatRequest
        {
            Model = request.Model,
            // Null cap ⇒ OMIT both (the model runs to its context limit — OpenAI allows this, unlike Anthropic). A set cap
            // rides as `max_completion_tokens` for a reasoning model (which 400s on the deprecated `max_tokens`) and as the
            // classic, universally-understood `max_tokens` otherwise (an older OpenAI-compatible gateway may not know the new name).
            MaxTokens = useCompletionTokens ? null : request.MaxOutputTokens,
            MaxCompletionTokens = useCompletionTokens ? request.MaxOutputTokens : null,
            Temperature = accepts ? request.Temperature : null,
            TopP = accepts ? request.Sampling?.TopP : null,
            FrequencyPenalty = accepts ? request.Sampling?.FrequencyPenalty : null,
            PresencePenalty = accepts ? request.Sampling?.PresencePenalty : null,
            Stop = request.Sampling?.Stop,
            Messages = BuildMessages(system, request.UserPrompt),
            Tools = new[] { new OpenAiTool { Function = new OpenAiFunction { Name = StructuredToolName, Description = "Return the result as structured JSON.", Parameters = request.JsonSchema } } },
            ToolChoice = new OpenAiToolChoice { Function = new OpenAiToolChoiceFunction { Name = StructuredToolName } },
        };

        OpenAiChatResponse parsed;
        try { parsed = await PostChatAsync(body, request.Credential, cancellationToken).ConfigureAwait(false); }
        catch (LlmApiException e) when (e.StatusCode is 400 or 422) { return (null, null); }   // ANY request-shape rejection (400/422 — forced functions unsupported) degrades to the floor, regardless of the refined category (a body keyword must never disable the fallback). A 401/403/404/429/5xx PROPAGATES — never swallowed into a second billable call that mis-reports the real cause.

        var message = parsed.Choices?.FirstOrDefault()?.Message;
        var toolCall = message?.ToolCalls?.FirstOrDefault(t => t.Function?.Name == StructuredToolName);

        var json = toolCall?.Function?.Arguments is { } arguments
            ? ParseToolArguments(arguments)
            : StructuredJsonText.TryExtractObject(message?.Content);

        return (json, parsed);
    }

    private static StructuredLLMCompletion BuildCompletion(JsonElement json, OpenAiChatResponse parsed, string fallbackModel) => new()
    {
        Json = json,
        Model = parsed.Model ?? fallbackModel,
        Usage = UsageFrom(parsed)
    };

    private static LlmUsage UsageFrom(OpenAiChatResponse parsed) => new()
    {
        InputTokens = parsed.Usage?.PromptTokens,
        OutputTokens = parsed.Usage?.CompletionTokens,
        FinishReason = parsed.Choices?.FirstOrDefault()?.FinishReason
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
        var (http, message) = BuildRequest(body, credential);

        using (message)
            return await LlmHttpTransport.SendForJsonAsync<OpenAiChatResponse>(http, message, Provider, ResponseJsonOptions, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Build the per-call POST to <c>{base}/chat/completions</c> — the ONE place credential + auth + body are assembled, so
    /// the buffered and streaming send paths never drift. Pure pool-driven (the credential is the ONLY key source; a caller
    /// without one fails closed). The key is OPTIONAL (a keyless local gateway — vLLM / a self-hosted relay — sends no
    /// Authorization). Per-call auth on the REQUEST message (never the singleton client's DefaultRequestHeaders) so
    /// concurrent branches can't bleed one team's Bearer onto another's call. The CALLER owns the returned message's lifetime.
    /// </summary>
    private (HttpClient Http, HttpRequestMessage Message) BuildRequest(OpenAiChatRequest body, ResolvedModelCredential? credential)
    {
        if (credential is null)
            throw new InvalidOperationException("OpenAI model credential not configured. The in-process plane must pass a model credential resolved from the team's pool.");

        var apiKey = NullIfBlank(credential.ApiKey);
        var baseUrl = NullIfBlank(credential.BaseUrl) ?? DefaultApiBaseUrl;
        var url = baseUrl.TrimEnd('/') + "/chat/completions";
        var http = _httpClientFactory.CreateClient(nameof(OpenAiClient));

        var message = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = LlmHttpTransport.JsonBody(body),
        };
        if (apiKey is not null) message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        return (http, message);
    }

    /// <summary>Case-insensitive so a non-conformant gateway that capitalises response keys (e.g. <c>Choices</c>) still deserializes.</summary>
    private static readonly JsonSerializerOptions ResponseJsonOptions = new() { PropertyNameCaseInsensitive = true };

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    // ── Wire DTOs (OpenAI Chat Completions) ──────────────────────────────────────────────────────────────────

    private sealed class OpenAiChatRequest
    {
        [JsonPropertyName("model")] public required string Model { get; init; }
        [JsonPropertyName("max_tokens")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public int? MaxTokens { get; init; }
        [JsonPropertyName("max_completion_tokens")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public int? MaxCompletionTokens { get; init; }
        [JsonPropertyName("temperature")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public double? Temperature { get; init; }
        [JsonPropertyName("messages")] public required IReadOnlyList<OpenAiMessage> Messages { get; init; }
        [JsonPropertyName("top_p")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public double? TopP { get; init; }
        [JsonPropertyName("frequency_penalty")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public double? FrequencyPenalty { get; init; }
        [JsonPropertyName("presence_penalty")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public double? PresencePenalty { get; init; }
        [JsonPropertyName("stop")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public IReadOnlyList<string>? Stop { get; init; }
        [JsonPropertyName("tools")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public IReadOnlyList<OpenAiTool>? Tools { get; init; }
        [JsonPropertyName("tool_choice")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public OpenAiToolChoice? ToolChoice { get; init; }
        [JsonPropertyName("stream")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public bool? Stream { get; init; }
        [JsonPropertyName("stream_options")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public OpenAiStreamOptions? StreamOptions { get; init; }
    }

    private sealed class OpenAiStreamOptions
    {
        [JsonPropertyName("include_usage")] public required bool IncludeUsage { get; init; }
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
        [JsonPropertyName("finish_reason")] public string? FinishReason { get; init; }
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
