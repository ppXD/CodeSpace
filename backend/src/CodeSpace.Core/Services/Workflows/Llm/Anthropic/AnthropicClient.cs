using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using CodeSpace.Messages.Agents;

namespace CodeSpace.Core.Services.Workflows.Llm.Anthropic;

/// <summary>
/// Anthropic Messages API client. The API key + base url come ENTIRELY from the per-call
/// <see cref="ResolvedModelCredential"/> the in-process plane resolves from the team's model pool — there is no
/// ambient env-key backstop on the call path (removed in S6b). The <c>CODESPACE_ANTHROPIC_API_KEY</c> constant
/// survives only as the name the AGENT plane's operator-global fallback + the cassette record-mode gate read
/// INDEPENDENTLY; per Rule 8 it stays pinned by a unit test so a rename is visible at compile time.
///
/// Implements <see cref="ILLMClient"/> (free-text completion) AND the sibling
/// <see cref="IStructuredLLMClient"/> (schema-constrained JSON) — the latter via Anthropic's
/// forced tool-use: a single tool whose <c>input_schema</c> IS the requested schema, with
/// <c>tool_choice</c> pinned to it, so the model's <c>tool_use</c> block carries schema-valid JSON.
///
/// Keep ONLY the wire-shape concerns here. Anything node-facing (prompt assembly, output
/// trimming, retry policy) belongs in the llm.complete node or the LLM-side resilience
/// service, not in this transport.
/// </summary>
public sealed class AnthropicClient : ILLMClient, IStructuredLLMClient, IStreamingLLMClient
{
    public const string ApiKeyEnvVar = "CODESPACE_ANTHROPIC_API_KEY";
    public const string DefaultApiBaseUrl = "https://api.anthropic.com";
    public const string AnthropicVersion = "2023-06-01";

    /// <summary>The single forced tool used to coerce structured output; the model returns its arguments as the JSON.</summary>
    private const string StructuredToolName = "respond";

    public string Provider => "Anthropic";

    private readonly IHttpClientFactory _httpClientFactory;

    public AnthropicClient(IHttpClientFactory httpClientFactory) { _httpClientFactory = httpClientFactory; }

    public async Task<LLMCompletion> CompleteAsync(LLMCompletionRequest request, CancellationToken cancellationToken)
    {
        var (_, stream) = LlmModelCapabilities.ResolveOutputBudget(request.Model, request.MaxOutputTokens, requiresField: true);
        var body = BuildMessageBody(request, stream);

        return stream
            ? await CompleteStreamingAsync(body, request.Model, request.Credential, cancellationToken).ConfigureAwait(false)
            : await CompleteBufferedAsync(body, request.Model, request.Credential, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Stream the completion as provider-normalized events — FORCES <c>stream:true</c> (the caller wants deltas) regardless of the buffered path's large-output gate; fold with <see cref="LlmTextStreamFold"/> to recover the whole <see cref="LLMCompletion"/>.</summary>
    public IAsyncEnumerable<LlmStreamEvent> StreamAsync(LLMCompletionRequest request, CancellationToken cancellationToken)
        => StreamTextEventsAsync(BuildMessageBody(request, stream: true), request.Credential, cancellationToken);

    /// <summary>Build the message request body. Identical for the buffered and streaming paths except the <paramref name="stream"/> flag — extracted so <see cref="CompleteAsync"/> and <see cref="StreamAsync"/> can never drift on the wire shape.</summary>
    private static AnthropicMessageRequest BuildMessageBody(LLMCompletionRequest request, bool stream)
    {
        var accepts = LlmModelCapabilities.AcceptsSampling(request.Model);
        var (cap, _) = LlmModelCapabilities.ResolveOutputBudget(request.Model, request.MaxOutputTokens, requiresField: true);

        return new AnthropicMessageRequest
        {
            Model = request.Model,
            MaxTokens = cap ?? LlmModelCapabilities.DefaultMaxOutputTokens,   // requiresField ⇒ cap is non-null; the ?? is a defensive floor
            Temperature = accepts ? request.Temperature : null,
            TopP = accepts ? request.Sampling?.TopP : null,
            StopSequences = request.Sampling?.Stop,
            System = request.SystemPrompt,
            Messages = new[] { new AnthropicMessage { Role = "user", Content = request.UserPrompt } },
            Stream = stream ? true : null,   // omitted when false → the non-streaming body is BYTE-IDENTICAL to before (every small-cap caller unchanged)
        };
    }

    /// <summary>The non-streaming path (small / bounded output) — one buffered POST, unchanged from the pre-streaming client.</summary>
    private async Task<LLMCompletion> CompleteBufferedAsync(AnthropicMessageRequest body, string fallbackModel, ResolvedModelCredential? credential, CancellationToken cancellationToken)
    {
        var parsed = await PostMessagesAsync(body, credential, cancellationToken).ConfigureAwait(false);

        return new LLMCompletion { Text = TextContent(parsed), Model = parsed.Model ?? fallbackModel, Usage = UsageFrom(parsed) };
    }

    /// <summary>The streaming path (large / unbounded output — the model can emit up to its true ceiling without an idle-connection timeout): fold the provider-normalized event stream into the SAME <see cref="LLMCompletion"/> the buffered path returns.</summary>
    private async Task<LLMCompletion> CompleteStreamingAsync(AnthropicMessageRequest body, string fallbackModel, ResolvedModelCredential? credential, CancellationToken cancellationToken)
        => await LlmTextStreamFold.AccumulateAsync(StreamTextEventsAsync(body, credential, cancellationToken), fallbackModel, cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Anthropic's message SSE normalized to the provider-neutral <see cref="LlmStreamEvent"/> sequence: <c>message_start</c>
    /// (model + input tokens → <see cref="LlmStreamEvent.Meta"/>), <c>content_block_delta</c>/<c>text_delta</c> (a
    /// <see cref="LlmStreamEvent.TextDelta"/>), and <c>message_delta</c> (stop_reason + final output tokens → Meta).
    /// <see cref="LlmTextStreamFold"/> folds them into the byte-for-byte-same result the prior inline accumulation produced.
    /// </summary>
    private async IAsyncEnumerable<LlmStreamEvent> StreamTextEventsAsync(AnthropicMessageRequest body, ResolvedModelCredential? credential, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var (http, message) = BuildRequest(body, credential);

        using (message)
        {
            await foreach (var evt in LlmHttpTransport.StreamSseAsync(http, message, Provider, cancellationToken).ConfigureAwait(false))
            {
                switch (evt.TryGetProperty("type", out var t) ? t.GetString() : null)
                {
                    case "message_start" when evt.TryGetProperty("message", out var msg):
                        string? model = msg.TryGetProperty("model", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null;
                        int? inputTokens = msg.TryGetProperty("usage", out var u) && u.TryGetProperty("input_tokens", out var it) && it.TryGetInt32(out var iv) ? iv : null;
                        if (model is not null || inputTokens is not null)
                            yield return new LlmStreamEvent.Meta(Model: model, InputTokens: inputTokens);
                        break;

                    case "content_block_delta" when evt.TryGetProperty("delta", out var d) && d.TryGetProperty("type", out var dt) && dt.GetString() == "text_delta":
                        if (d.TryGetProperty("text", out var txt) && txt.ValueKind == JsonValueKind.String)
                            yield return new LlmStreamEvent.TextDelta(txt.GetString()!);
                        break;

                    case "message_delta":
                        string? stopReason = evt.TryGetProperty("delta", out var md) && md.TryGetProperty("stop_reason", out var sr) && sr.ValueKind == JsonValueKind.String ? sr.GetString() : null;
                        int? outputTokens = evt.TryGetProperty("usage", out var mu) && mu.TryGetProperty("output_tokens", out var ot) && ot.TryGetInt32(out var ov) ? ov : null;
                        if (stopReason is not null || outputTokens is not null)
                            yield return new LlmStreamEvent.Meta(OutputTokens: outputTokens, FinishReason: stopReason);
                        break;
                }
            }
        }
    }

    public async Task<StructuredLLMCompletion> CompleteStructuredAsync(StructuredLLMCompletionRequest request, CancellationToken cancellationToken)
    {
        // Get the JSON via the progressive path, then VALIDATE it against the requested schema — a recovered object that
        // is missing a required field / has a wrong-typed value / an invalid enum is NOT success (the old path returned
        // it blindly, so {} or a "no kind" object slipped through). On a validation miss, RE-ASK ONCE with the exact
        // violations named, then re-validate. A second miss is a typed Malformed fault (the engine fails it fast).
        var first = await FirstOrReaskOnParseFailureAsync(request, cancellationToken).ConfigureAwait(false);
        var errors = JsonSchemaValidator.Validate(first.Json, request.JsonSchema);
        if (errors.Count == 0) return first;

        var feedbackSystem = StructuredJsonText.WithValidationFeedback(request.SystemPrompt, errors, first.Json);
        var second = await CompleteStructuredOnceAsync(request, feedbackSystem, cancellationToken).ConfigureAwait(false);
        var errors2 = JsonSchemaValidator.Validate(second.Json, request.JsonSchema);
        if (errors2.Count == 0) return second with { Usage = first.Usage.Add(second.Usage) };   // total billed = the first (invalid) attempt + the re-ask

        throw new LlmApiException(Provider, null, LlmErrorCategory.Malformed,
            $"structured output failed schema validation after a re-ask: {string.Join("; ", errors2)}");
    }

    /// <summary>The first structured attempt, with ONE re-ask when it produces NO parseable JSON at all — a transient malformation the repair pass can't recover. A parse failure gets the same second chance a schema violation does, with explicit "your output was not valid JSON" feedback, before it becomes a hard Malformed fault; a SECOND parse failure propagates as Malformed (the re-ask is bounded to once).</summary>
    private async Task<StructuredLLMCompletion> FirstOrReaskOnParseFailureAsync(StructuredLLMCompletionRequest request, CancellationToken cancellationToken)
    {
        try
        {
            return await CompleteStructuredOnceAsync(request, request.SystemPrompt, cancellationToken).ConfigureAwait(false);
        }
        catch (LlmApiException ex) when (ex.Category == LlmErrorCategory.Malformed)
        {
            var feedbackSystem = StructuredJsonText.WithMalformedFeedback(request.SystemPrompt, ex.ProviderMessage);
            return await CompleteStructuredOnceAsync(request, feedbackSystem, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>One progressive structured attempt (forced tool-use → prompt-only floor) against the given base system prompt — the re-ask passes a feedback-augmented base so the SAME path retries with the validation errors named. The schema rides in the system prompt for both sub-attempts.</summary>
    private async Task<StructuredLLMCompletion> CompleteStructuredOnceAsync(StructuredLLMCompletionRequest request, string baseSystemPrompt, CancellationToken cancellationToken)
    {
        var system = StructuredJsonText.WithSchemaInstruction(baseSystemPrompt, request.JsonSchema);
        var messages = new[] { new AnthropicMessage { Role = "user", Content = request.UserPrompt } };

        // Attempt 1 — forced tool-use. A 400/422 reject (toolParsed null, nothing billed) or a 200 that yields no usable
        // JSON degrades to the prompt-only floor below; a 200 WITH a result returns here carrying its own usage.
        var (toolJson, toolParsed) = await TryStructuredViaToolAsync(request, system, messages, cancellationToken).ConfigureAwait(false);
        if (toolJson is { } viaTool)
            return BuildCompletion(viaTool, toolParsed!, request.Model);

        var accepts = LlmModelCapabilities.AcceptsSampling(request.Model);

        // Attempt 2 — prompt-only floor: no tools. The SAFEST request — never 400s on a gateway that rejects forced
        // tool-use, and forces nothing that could suppress the reply (some gateways return EMPTY content when a tool is
        // forced). The model answers in text; recover the JSON object from it. The returned usage TOTALS the (billed but
        // degraded) forced attempt + this floor, so the caller sees what the provider actually billed.
        var body = new AnthropicMessageRequest
        {
            Model = request.Model,
            MaxTokens = request.MaxOutputTokens ?? LlmModelCapabilities.DefaultMaxOutputTokens,   // Anthropic REQUIRES max_tokens — a null "let the model decide" resolves to the generous non-streaming-safe default (OpenAI can omit; this wire can't)
            Temperature = accepts ? request.Temperature : null,
            TopP = accepts ? request.Sampling?.TopP : null,
            StopSequences = request.Sampling?.Stop,
            System = system,
            Messages = messages
        };

        var parsed = await PostMessagesAsync(body, request.Credential, cancellationToken).ConfigureAwait(false);
        var text = TextContent(parsed);

        if (StructuredJsonText.TryExtractObject(text) is not { } result)
            throw new LlmApiException(Provider, null, LlmErrorCategory.Malformed, $"structured completion produced no JSON via forced tool-use OR the prompt-only fallback — the model did not produce structured output. Content preview: {StructuredJsonText.Preview(text)}");

        var totalUsage = (toolParsed is null ? LlmUsage.None : UsageFrom(toolParsed)).Add(UsageFrom(parsed));
        return BuildCompletion(result, parsed, request.Model) with { Usage = totalUsage };
    }

    /// <summary>
    /// Attempt 1: forced tool-use — a single tool whose input_schema IS the schema, tool_choice pinned to it. Returns
    /// the recovered JSON (or null to degrade to the prompt-only floor) PLUS the parsed response — so the caller can
    /// accumulate the BILLED usage even when the JSON is null (a 200 that produced no usable tool call still cost
    /// tokens). On a 400/422 reject the request was never generated, so both are null (nothing to bill).
    /// </summary>
    private async Task<(JsonElement? Json, AnthropicMessageResponse? Parsed)> TryStructuredViaToolAsync(StructuredLLMCompletionRequest request, string system, AnthropicMessage[] messages, CancellationToken cancellationToken)
    {
        var accepts = LlmModelCapabilities.AcceptsSampling(request.Model);

        var body = new AnthropicMessageRequest
        {
            Model = request.Model,
            MaxTokens = request.MaxOutputTokens ?? LlmModelCapabilities.DefaultMaxOutputTokens,   // Anthropic REQUIRES max_tokens — a null "let the model decide" resolves to the generous non-streaming-safe default (OpenAI can omit; this wire can't)
            Temperature = accepts ? request.Temperature : null,
            TopP = accepts ? request.Sampling?.TopP : null,
            StopSequences = request.Sampling?.Stop,
            System = system,
            Messages = messages,
            Tools = new[] { new AnthropicTool { Name = StructuredToolName, Description = "Return the result as structured JSON.", InputSchema = request.JsonSchema } },
            ToolChoice = new AnthropicToolChoice { Type = "tool", Name = StructuredToolName }
        };

        AnthropicMessageResponse parsed;
        try { parsed = await PostMessagesAsync(body, request.Credential, cancellationToken).ConfigureAwait(false); }
        catch (LlmApiException e) when (e.StatusCode is 400 or 422) { return (null, null); }   // ANY request-shape rejection (400/422 — forced tool-use unsupported) degrades to the floor, regardless of the refined category (a body keyword must never disable the fallback). A 401/403/404/429/5xx PROPAGATES — never swallowed into a second billable call that mis-reports the real cause.

        var toolUse = parsed.Content?.FirstOrDefault(c => c.Type == "tool_use" && c.Name == StructuredToolName);

        var json = toolUse?.Input is { } input
            ? input.Clone()
            : StructuredJsonText.TryExtractObject(TextContent(parsed));

        return (json, parsed);
    }

    private static StructuredLLMCompletion BuildCompletion(JsonElement json, AnthropicMessageResponse parsed, string fallbackModel) => new()
    {
        Json = json,
        Model = parsed.Model ?? fallbackModel,
        Usage = UsageFrom(parsed)
    };

    private static LlmUsage UsageFrom(AnthropicMessageResponse parsed) => new()
    {
        InputTokens = parsed.Usage?.InputTokens,
        OutputTokens = parsed.Usage?.OutputTokens,
        FinishReason = parsed.StopReason
    };

    /// <summary>Join the response's text content blocks (the structured fallback reads JSON out of these when the model answered in text instead of a tool_use block).</summary>
    private static string TextContent(AnthropicMessageResponse parsed) =>
        string.Join("\n", parsed.Content?.Where(c => c.Type == "text").Select(c => c.Text ?? "") ?? Array.Empty<string>());

    private async Task<AnthropicMessageResponse> PostMessagesAsync(AnthropicMessageRequest body, ResolvedModelCredential? credential, CancellationToken cancellationToken)
    {
        var (http, message) = BuildRequest(body, credential);

        using (message)
            return await LlmHttpTransport.SendForJsonAsync<AnthropicMessageResponse>(http, message, Provider, options: null, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Build the per-call POST to <c>/v1/messages</c> — the ONE place the credential + headers + body are assembled, so the
    /// buffered and streaming send paths can never drift on the wire setup. Pure pool-driven (S6b): the credential is the
    /// ONLY key source (no ambient env-key backstop — a caller without one fails closed). The key is OPTIONAL (a keyless
    /// Anthropic-compatible gateway sends no <c>x-api-key</c>). Per-call headers on the REQUEST message (never the
    /// singleton client's DefaultRequestHeaders) so concurrent branches can't bleed one team's key onto another's call;
    /// buffered StringContent so a transient retry can re-send the body. The CALLER owns the returned message's lifetime.
    /// </summary>
    private (HttpClient Http, HttpRequestMessage Message) BuildRequest(AnthropicMessageRequest body, ResolvedModelCredential? credential)
    {
        if (credential is null)
            throw new InvalidOperationException("Anthropic model credential not configured. The in-process plane must pass a model credential resolved from the team's pool.");

        var apiKey = NullIfBlank(credential.ApiKey);
        var baseUrl = NullIfBlank(credential.BaseUrl) ?? DefaultApiBaseUrl;
        var http = _httpClientFactory.CreateClient(nameof(AnthropicClient));

        var message = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri(baseUrl), "/v1/messages"))
        {
            Content = LlmHttpTransport.JsonBody(body),
        };
        message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (apiKey is not null) message.Headers.Add("x-api-key", apiKey);
        message.Headers.Add("anthropic-version", AnthropicVersion);

        return (http, message);
    }

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private sealed class AnthropicMessageRequest
    {
        [JsonPropertyName("model")] public required string Model { get; init; }
        [JsonPropertyName("max_tokens")] public required int MaxTokens { get; init; }
        [JsonPropertyName("stream")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public bool? Stream { get; init; }
        [JsonPropertyName("temperature")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public double? Temperature { get; init; }
        [JsonPropertyName("system")] public required string System { get; init; }
        [JsonPropertyName("messages")] public required IReadOnlyList<AnthropicMessage> Messages { get; init; }
        [JsonPropertyName("top_p")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public double? TopP { get; init; }
        [JsonPropertyName("stop_sequences")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public IReadOnlyList<string>? StopSequences { get; init; }
        [JsonPropertyName("tools")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public IReadOnlyList<AnthropicTool>? Tools { get; init; }
        [JsonPropertyName("tool_choice")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public AnthropicToolChoice? ToolChoice { get; init; }
    }

    private sealed class AnthropicMessage
    {
        [JsonPropertyName("role")] public required string Role { get; init; }
        [JsonPropertyName("content")] public required string Content { get; init; }
    }

    private sealed class AnthropicTool
    {
        [JsonPropertyName("name")] public required string Name { get; init; }
        [JsonPropertyName("description")] public required string Description { get; init; }
        [JsonPropertyName("input_schema")] public required JsonElement InputSchema { get; init; }
    }

    private sealed class AnthropicToolChoice
    {
        [JsonPropertyName("type")] public required string Type { get; init; }
        [JsonPropertyName("name")] public required string Name { get; init; }
    }

    private sealed class AnthropicMessageResponse
    {
        [JsonPropertyName("model")] public string? Model { get; init; }
        [JsonPropertyName("content")] public IReadOnlyList<AnthropicContentBlock>? Content { get; init; }
        [JsonPropertyName("usage")] public AnthropicUsage? Usage { get; init; }
        [JsonPropertyName("stop_reason")] public string? StopReason { get; init; }
    }

    private sealed class AnthropicContentBlock
    {
        [JsonPropertyName("type")] public string? Type { get; init; }
        [JsonPropertyName("text")] public string? Text { get; init; }
        [JsonPropertyName("name")] public string? Name { get; init; }
        [JsonPropertyName("input")] public JsonElement? Input { get; init; }
    }

    private sealed class AnthropicUsage
    {
        [JsonPropertyName("input_tokens")] public int InputTokens { get; init; }
        [JsonPropertyName("output_tokens")] public int OutputTokens { get; init; }
    }
}
