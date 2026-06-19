using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using CodeSpace.Messages.Agents;

namespace CodeSpace.Core.Services.Workflows.Llm.Anthropic;

/// <summary>
/// Anthropic Messages API client. Reads the API key from the
/// <c>CODESPACE_ANTHROPIC_API_KEY</c> environment variable so an operator can plug in a key
/// without code changes; per Rule 8 the constant name is pinned by a unit test so future
/// renames are visible at compile time.
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
    public const string ApiBaseUrlEnvVar = "CODESPACE_ANTHROPIC_API_BASE_URL";
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
        // Force the model to call ONE tool whose input_schema is the requested schema. The tool's
        // arguments (the tool_use block's `input`) are therefore schema-valid JSON.
        var body = new AnthropicMessageRequest
        {
            Model = request.Model,
            MaxTokens = request.MaxOutputTokens,
            Temperature = request.Temperature,
            System = request.SystemPrompt,
            Messages = new[] { new AnthropicMessage { Role = "user", Content = request.UserPrompt } },
            Tools = new[] { new AnthropicTool { Name = StructuredToolName, Description = "Return the result as structured JSON.", InputSchema = request.JsonSchema } },
            ToolChoice = new AnthropicToolChoice { Type = "tool", Name = StructuredToolName }
        };

        var parsed = await PostMessagesAsync(body, request.Credential, cancellationToken).ConfigureAwait(false);

        var toolUse = parsed.Content?.FirstOrDefault(c => c.Type == "tool_use" && c.Name == StructuredToolName);

        if (toolUse?.Input is not { } input)
            throw new InvalidOperationException("Anthropic structured completion returned no tool_use block — the model did not produce structured output.");

        return new StructuredLLMCompletion
        {
            Json = input.Clone(),
            Model = parsed.Model ?? request.Model,
            InputTokens = parsed.Usage?.InputTokens,
            OutputTokens = parsed.Usage?.OutputTokens
        };
    }

    private async Task<AnthropicMessageResponse> PostMessagesAsync(AnthropicMessageRequest body, ResolvedModelCredential? credential, CancellationToken cancellationToken)
    {
        // The resolved credential's key wins (so a TEAM's key authenticates the call); the operator-global env key is
        // the fallback for the single-tenant convenience + any caller not yet passing a credential (removed in S6b).
        var apiKey = NullIfBlank(credential?.ApiKey) ?? Environment.GetEnvironmentVariable(ApiKeyEnvVar);

        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException($"Anthropic API key not configured. Pass a model credential or set the {ApiKeyEnvVar} environment variable.");

        var baseUrl = NullIfBlank(credential?.BaseUrl) ?? Environment.GetEnvironmentVariable(ApiBaseUrlEnvVar) ?? DefaultApiBaseUrl;
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
