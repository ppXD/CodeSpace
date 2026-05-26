using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodeSpace.Core.Services.Workflows.Llm.Anthropic;

/// <summary>
/// Anthropic Messages API client. Reads the API key from the
/// <c>CODESPACE_ANTHROPIC_API_KEY</c> environment variable so an operator can plug in a key
/// without code changes; per Rule 8 the constant name is pinned by a unit test so future
/// renames are visible at compile time.
///
/// Keep ONLY the wire-shape concerns here. Anything node-facing (prompt assembly, output
/// trimming, retry policy) belongs in the llm.complete node or the LLM-side resilience
/// service, not in this transport.
/// </summary>
public sealed class AnthropicClient : ILLMClient
{
    public const string ApiKeyEnvVar = "CODESPACE_ANTHROPIC_API_KEY";
    public const string ApiBaseUrlEnvVar = "CODESPACE_ANTHROPIC_API_BASE_URL";
    public const string DefaultApiBaseUrl = "https://api.anthropic.com";
    public const string AnthropicVersion = "2023-06-01";

    public string Provider => "Anthropic";

    private readonly IHttpClientFactory _httpClientFactory;

    public AnthropicClient(IHttpClientFactory httpClientFactory) { _httpClientFactory = httpClientFactory; }

    public async Task<LLMCompletion> CompleteAsync(LLMCompletionRequest request, CancellationToken cancellationToken)
    {
        var apiKey = Environment.GetEnvironmentVariable(ApiKeyEnvVar);

        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException($"Anthropic API key not configured. Set the {ApiKeyEnvVar} environment variable.");

        var baseUrl = Environment.GetEnvironmentVariable(ApiBaseUrlEnvVar) ?? DefaultApiBaseUrl;
        var http = _httpClientFactory.CreateClient(nameof(AnthropicClient));
        http.BaseAddress = new Uri(baseUrl);
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        http.DefaultRequestHeaders.Add("x-api-key", apiKey);
        http.DefaultRequestHeaders.Add("anthropic-version", AnthropicVersion);

        var body = new AnthropicMessageRequest
        {
            Model = request.Model,
            MaxTokens = request.MaxOutputTokens,
            Temperature = request.Temperature,
            System = request.SystemPrompt,
            Messages = new[] { new AnthropicMessage { Role = "user", Content = request.UserPrompt } }
        };

        var response = await http.PostAsJsonAsync("/v1/messages", body, cancellationToken).ConfigureAwait(false);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Anthropic API returned {(int)response.StatusCode}: {responseBody}");

        var parsed = JsonSerializer.Deserialize<AnthropicMessageResponse>(responseBody)
            ?? throw new InvalidOperationException("Anthropic API returned empty body.");

        var text = string.Join("\n", parsed.Content?.Where(c => c.Type == "text").Select(c => c.Text ?? "") ?? Array.Empty<string>());

        return new LLMCompletion
        {
            Text = text,
            Model = parsed.Model ?? request.Model,
            InputTokens = parsed.Usage?.InputTokens,
            OutputTokens = parsed.Usage?.OutputTokens
        };
    }

    private sealed class AnthropicMessageRequest
    {
        [JsonPropertyName("model")] public required string Model { get; init; }
        [JsonPropertyName("max_tokens")] public required int MaxTokens { get; init; }
        [JsonPropertyName("temperature")] public required double Temperature { get; init; }
        [JsonPropertyName("system")] public required string System { get; init; }
        [JsonPropertyName("messages")] public required IReadOnlyList<AnthropicMessage> Messages { get; init; }
    }

    private sealed class AnthropicMessage
    {
        [JsonPropertyName("role")] public required string Role { get; init; }
        [JsonPropertyName("content")] public required string Content { get; init; }
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
    }

    private sealed class AnthropicUsage
    {
        [JsonPropertyName("input_tokens")] public int InputTokens { get; init; }
        [JsonPropertyName("output_tokens")] public int OutputTokens { get; init; }
    }
}
