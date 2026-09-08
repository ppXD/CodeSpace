using System.Text;

namespace CodeSpace.Core.Services.Workflows.Llm;

/// <summary>
/// Provider-neutral, conservative preflight for a structured model request. Capacity comes from the selected
/// credentialed-model row; this class never guesses from a provider or model name. Unknown capacity keeps the
/// provider's authoritative overflow response as the fallback.
/// </summary>
public static class LlmContextWindowBudget
{
    internal const int FramingTokenReserve = 96;
    internal const int UsableCapacityNumerator = 7;
    internal const int UsableCapacityDenominator = 8;

    public static bool RequiresCompaction(StructuredLLMCompletionRequest request, int? contextWindowTokens)
    {
        if (contextWindowTokens is not > 0) return false;

        var usable = (long)contextWindowTokens.Value * UsableCapacityNumerator / UsableCapacityDenominator;
        var output = request.MaxOutputTokens is > 0 ? request.MaxOutputTokens.Value : LlmBudgetGuard.DefaultMaxOutputTokensEstimate;
        return (long)EstimateInputTokens(request) + output > usable;
    }

    public static int EstimateInputTokens(StructuredLLMCompletionRequest request)
    {
        var bytes = Utf8Bytes(request.SystemPrompt) + Utf8Bytes(request.UserPrompt) + Utf8Bytes(request.JsonSchema.GetRawText());
        return (int)Math.Min(bytes / 3 + FramingTokenReserve, int.MaxValue);
    }

    private static long Utf8Bytes(string? value) => string.IsNullOrEmpty(value) ? 0 : Encoding.UTF8.GetByteCount(value);
}
