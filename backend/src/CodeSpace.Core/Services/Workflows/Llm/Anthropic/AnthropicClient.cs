using System.Net.Http.Headers;
using System.Net.Http.Json;
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
            System = request.SystemPrompt,
            Messages = new[] { new AnthropicMessage { Role = "user", Content = request.UserPrompt } }
        };

        var parsed = await PostMessagesAsync(body, request.Credential, cancellationToken).ConfigureAwait(false);

        var text = string.Join("\n", parsed.Content?.Where(c => c.Type == "text").Select(c => c.Text ?? "") ?? Array.Empty<string>());

        return new LLMCompletion
        {
            Text = text,
            Model = parsed.Model ?? request.Model,
            InputTokens = parsed.Usage?.InputTokens,
            OutputTokens = parsed.Usage?.OutputTokens
        };
    }

    public async Task<StructuredLLMCompletion> CompleteStructuredAsync(StructuredLLMCompletionRequest request, CancellationToken cancellationToken)
    {
        // Progressive structured output (generic across models AND gateways): try the constrained path first for the
        // best fidelity on capable models, then degrade to a prompt-only floor for models/gateways that cannot honour
        // it. The schema rides in the system prompt for BOTH attempts so any model knows the exact shape to emit.
        var system = StructuredJsonText.WithSchemaInstruction(request.SystemPrompt, request.JsonSchema);
        var messages = new[] { new AnthropicMessage { Role = "user", Content = request.UserPrompt } };

        // Attempt 1 — forced tool-use. A gateway/model that rejects it (400) or returns neither a tool_use block nor a
        // JSON content object yields null here, degrading to the prompt-only floor below.
        if (await TryStructuredViaToolAsync(request, system, messages, cancellationToken).ConfigureAwait(false) is { } viaTool)
            return viaTool;

        // Attempt 2 — prompt-only floor: no tools. This is the SAFEST request — it never 400s on a gateway that rejects
        // forced tool-use, and forces nothing that could suppress the reply (some gateways return an EMPTY content when
        // a tool is forced). The model answers in text; recover the JSON object from it.
        var body = new AnthropicMessageRequest
        {
            Model = request.Model,
            MaxTokens = request.MaxOutputTokens,
            Temperature = request.Temperature,
            System = system,
            Messages = messages
        };

        var parsed = await PostMessagesAsync(body, request.Credential, cancellationToken).ConfigureAwait(false);
        var text = TextContent(parsed);

        if (StructuredJsonText.TryExtractObject(text) is not { } result)
            throw new InvalidOperationException($"Anthropic structured completion produced no JSON via forced tool-use OR the prompt-only fallback — the model did not produce structured output. Content preview: {StructuredJsonText.Preview(text)}");

        return BuildCompletion(result, parsed, request.Model);
    }

    /// <summary>
    /// Attempt 1: forced tool-use — a single tool whose input_schema IS the schema, tool_choice pinned to it. Returns
    /// null (degrade to the prompt-only floor) when the gateway REJECTS the request (e.g. 400 on an unsupported
    /// feature) or the model returns neither a tool_use block nor a recoverable JSON content object.
    /// </summary>
    private async Task<StructuredLLMCompletion?> TryStructuredViaToolAsync(StructuredLLMCompletionRequest request, string system, AnthropicMessage[] messages, CancellationToken cancellationToken)
    {
        var body = new AnthropicMessageRequest
        {
            Model = request.Model,
            MaxTokens = request.MaxOutputTokens,
            Temperature = request.Temperature,
            System = system,
            Messages = messages,
            Tools = new[] { new AnthropicTool { Name = StructuredToolName, Description = "Return the result as structured JSON.", InputSchema = request.JsonSchema } },
            ToolChoice = new AnthropicToolChoice { Type = "tool", Name = StructuredToolName }
        };

        AnthropicMessageResponse parsed;
        try { parsed = await PostMessagesAsync(body, request.Credential, cancellationToken).ConfigureAwait(false); }
        catch (InvalidOperationException) { return null; }   // gateway rejects forced tool-use (e.g. 400) → degrade. Any PERSISTENT error re-surfaces from the floor attempt.

        var toolUse = parsed.Content?.FirstOrDefault(c => c.Type == "tool_use" && c.Name == StructuredToolName);

        var json = toolUse?.Input is { } input
            ? input.Clone()
            : StructuredJsonText.TryExtractObject(TextContent(parsed));

        return json is { } result ? BuildCompletion(result, parsed, request.Model) : null;
    }

    private static StructuredLLMCompletion BuildCompletion(JsonElement json, AnthropicMessageResponse parsed, string fallbackModel) => new()
    {
        Json = json,
        Model = parsed.Model ?? fallbackModel,
        InputTokens = parsed.Usage?.InputTokens,
        OutputTokens = parsed.Usage?.OutputTokens
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
        http.BaseAddress = new Uri(baseUrl);
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        http.DefaultRequestHeaders.Add("x-api-key", apiKey);
        http.DefaultRequestHeaders.Add("anthropic-version", AnthropicVersion);

        var response = await http.PostAsJsonAsync("/v1/messages", body, cancellationToken).ConfigureAwait(false);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Anthropic API returned {(int)response.StatusCode}: {responseBody}");

        return JsonSerializer.Deserialize<AnthropicMessageResponse>(responseBody)
            ?? throw new InvalidOperationException("Anthropic API returned empty body.");
    }

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private sealed class AnthropicMessageRequest
    {
        [JsonPropertyName("model")] public required string Model { get; init; }
        [JsonPropertyName("max_tokens")] public required int MaxTokens { get; init; }
        [JsonPropertyName("temperature")] public required double Temperature { get; init; }
        [JsonPropertyName("system")] public required string System { get; init; }
        [JsonPropertyName("messages")] public required IReadOnlyList<AnthropicMessage> Messages { get; init; }
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
