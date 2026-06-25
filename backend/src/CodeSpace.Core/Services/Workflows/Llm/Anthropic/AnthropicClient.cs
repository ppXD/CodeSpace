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
public sealed class AnthropicClient : ILLMClient, IStructuredLLMClient
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
        var body = new AnthropicMessageRequest
        {
            Model = request.Model,
            MaxTokens = request.MaxOutputTokens,
            Temperature = request.Temperature,
            TopP = request.Sampling?.TopP,
            StopSequences = request.Sampling?.Stop,
            System = request.SystemPrompt,
            Messages = new[] { new AnthropicMessage { Role = "user", Content = request.UserPrompt } }
        };

        var parsed = await PostMessagesAsync(body, request.Credential, cancellationToken).ConfigureAwait(false);

        var text = string.Join("\n", parsed.Content?.Where(c => c.Type == "text").Select(c => c.Text ?? "") ?? Array.Empty<string>());

        return new LLMCompletion
        {
            Text = text,
            Model = parsed.Model ?? request.Model,
            Usage = UsageFrom(parsed)
        };
    }

    public async Task<StructuredLLMCompletion> CompleteStructuredAsync(StructuredLLMCompletionRequest request, CancellationToken cancellationToken)
    {
        // Get the JSON via the progressive path, then VALIDATE it against the requested schema — a recovered object that
        // is missing a required field / has a wrong-typed value / an invalid enum is NOT success (the old path returned
        // it blindly, so {} or a "no kind" object slipped through). On a validation miss, RE-ASK ONCE with the exact
        // violations named, then re-validate. A second miss is a typed Malformed fault (the engine fails it fast).
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

        // Attempt 2 — prompt-only floor: no tools. The SAFEST request — never 400s on a gateway that rejects forced
        // tool-use, and forces nothing that could suppress the reply (some gateways return EMPTY content when a tool is
        // forced). The model answers in text; recover the JSON object from it. The returned usage TOTALS the (billed but
        // degraded) forced attempt + this floor, so the caller sees what the provider actually billed.
        var body = new AnthropicMessageRequest
        {
            Model = request.Model,
            MaxTokens = request.MaxOutputTokens,
            Temperature = request.Temperature,
            TopP = request.Sampling?.TopP,
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
        var body = new AnthropicMessageRequest
        {
            Model = request.Model,
            MaxTokens = request.MaxOutputTokens,
            Temperature = request.Temperature,
            TopP = request.Sampling?.TopP,
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
        // Pure pool-driven (S6b): the credential is the ONLY key source — the in-process plane resolves it from the
        // team's credentialed-model pool and passes it here. There is NO ambient env-key backstop: a caller without a
        // credential fails closed rather than silently borrowing an operator-global key. (The env var const survives
        // for the AGENT plane's operator-global fallback + the cassette record-mode gate, which read it independently.)
        var apiKey = NullIfBlank(credential?.ApiKey);

        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException("Anthropic API key not configured. The in-process plane must pass a model credential resolved from the team's pool.");

        var baseUrl = NullIfBlank(credential?.BaseUrl) ?? DefaultApiBaseUrl;
        var http = _httpClientFactory.CreateClient(nameof(AnthropicClient));

        // Per-call headers on the REQUEST message (never mutate the singleton-resolved client's DefaultRequestHeaders) so
        // concurrent flow.map branches can never bleed one team's key onto another's call. Buffered StringContent (not a
        // streaming JsonContent) so the resilience handler can re-send the body on a transient retry.
        using var message = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri(baseUrl), "/v1/messages"))
        {
            Content = LlmHttpTransport.JsonBody(body),
        };
        message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        message.Headers.Add("x-api-key", apiKey);
        message.Headers.Add("anthropic-version", AnthropicVersion);

        return await LlmHttpTransport.SendForJsonAsync<AnthropicMessageResponse>(http, message, Provider, options: null, cancellationToken).ConfigureAwait(false);
    }

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private sealed class AnthropicMessageRequest
    {
        [JsonPropertyName("model")] public required string Model { get; init; }
        [JsonPropertyName("max_tokens")] public required int MaxTokens { get; init; }
        [JsonPropertyName("temperature")] public required double Temperature { get; init; }
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
