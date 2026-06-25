namespace CodeSpace.Core.Services.Workflows.Llm;

/// <summary>
/// The usage/accounting envelope for one LLM completion — the provider-reported token counts and stop reason grouped
/// into one consumable noun, replacing the loose Input/OutputTokens scalars that used to ride on the completion
/// records. Gives every caller a single <c>Usage</c> object to read + surface, and gives later additions
/// (reasoning / cache tokens) a home that doesn't re-sprawl scalars across the completion records. USD cost is
/// deliberately NOT here: it is derived at the surfacing layer from (model, in, out) via <c>AgentCostPricing</c>, so
/// this stays a pure provider-reported value with no pricing-table coupling.
/// </summary>
public sealed record LlmUsage
{
    /// <summary>Prompt / input tokens the provider billed. Null when the provider reported no usage.</summary>
    public int? InputTokens { get; init; }

    /// <summary>Completion / output tokens the provider billed. Null when the provider reported no usage.</summary>
    public int? OutputTokens { get; init; }

    /// <summary>The provider's stop reason — Anthropic <c>stop_reason</c> ("end_turn" / "max_tokens" / "tool_use" / "stop_sequence"), OpenAI <c>finish_reason</c> ("stop" / "length" / "tool_calls" / "content_filter"). Null when the provider didn't report one.</summary>
    public string? FinishReason { get; init; }

    /// <summary>The empty usage — the default on every completion so <c>Usage</c> is never null (a provider response with no usage block yields this).</summary>
    public static readonly LlmUsage None = new();
}
