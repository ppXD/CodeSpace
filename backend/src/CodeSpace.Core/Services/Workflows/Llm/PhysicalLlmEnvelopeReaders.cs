using System.Text.Json;

namespace CodeSpace.Core.Services.Workflows.Llm;

/// <summary>Protocol envelope fields only, observed before parsing the model's structured answer.</summary>
internal static class PhysicalLlmEnvelopeReaders
{
    internal static PhysicalLlmCallContext.ProviderEnvelope Anthropic(JsonElement root) => new(Text(root, "model", 500), new LlmUsage
    {
        InputTokens = Tokens(Property(root, "usage"), "input_tokens"), OutputTokens = Tokens(Property(root, "usage"), "output_tokens"),
        FinishReason = Text(root, "stop_reason", 100),
    });

    internal static PhysicalLlmCallContext.ProviderEnvelope OpenAi(JsonElement root)
    {
        var choices = Property(root, "choices");
        return new(Text(root, "model", 500), new LlmUsage
        {
            InputTokens = Tokens(Property(root, "usage"), "prompt_tokens"), OutputTokens = Tokens(Property(root, "usage"), "completion_tokens"),
            FinishReason = choices.ValueKind == JsonValueKind.Array && choices.GetArrayLength() > 0 ? Text(choices[0], "finish_reason", 100) : null,
        });
    }

    private static JsonElement Property(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var property) ? property : default;
    private static int? Tokens(JsonElement value, string name) => Property(value, name) is { ValueKind: JsonValueKind.Number } property && property.TryGetInt32(out var count) && count >= 0 ? count : null;
    private static string? Text(JsonElement value, string name, int limit) => Property(value, name) is { ValueKind: JsonValueKind.String } property && property.GetString() is { } text && text.Length <= limit ? text : null;
}
