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

    /// <summary>True when this envelope contains only a subtotal: a billed sub-call omitted usage, failed before its usage could be recovered, or the aggregate token count overflowed. Known token counts remain useful evidence but cannot price the complete call.</summary>
    public bool IsPartial { get; init; }

    [System.Text.Json.Serialization.JsonIgnore]
    public bool HasCompleteTokenCounts => !IsPartial && InputTokens is >= 0 && OutputTokens is >= 0;

    /// <summary>The empty usage — the default on every completion so <c>Usage</c> is never null (a provider response with no usage block yields this).</summary>
    public static readonly LlmUsage None = new();

    /// <summary>
    /// Sum this usage with a LATER one — used to total the SEVERAL billed sub-calls one structured completion makes
    /// (a forced tool/function attempt that degrades to a prompt-only floor, each re-asked once on a schema miss) so
    /// the returned usage reflects what the provider actually BILLED, not just the final POST. Token counts add (two
    /// nulls stay null; a null on one side preserves the known subtotal and marks it partial). The finish reason is the
    /// LATER one UNCONDITIONALLY — the final/accepted sub-call's, even when that's null: the degraded/rejected earlier
    /// attempts' reasons (a "tool_use" that produced no usable JSON, a schema-invalid round) are NOT the answer's and
    /// must never surface in place of the accepted call's. Null honestly means "the accepted call reported no reason".
    /// </summary>
    public LlmUsage Add(LlmUsage later, bool sameModel = true) => new()
    {
        InputTokens = AddTokens(InputTokens, later.InputTokens),
        OutputTokens = AddTokens(OutputTokens, later.OutputTokens),
        FinishReason = later.FinishReason,
        IsPartial = !sameModel || !HasCompleteTokenCounts || !later.HasCompleteTokenCounts || (long)(InputTokens ?? 0) + (later.InputTokens ?? 0) > int.MaxValue || (long)(OutputTokens ?? 0) + (later.OutputTokens ?? 0) > int.MaxValue,
    };

    private static int? AddTokens(int? a, int? b)
    {
        if (a is null && b is null) return null;
        var sum = (long)(a ?? 0) + (b ?? 0);
        return sum is >= 0 and <= int.MaxValue ? (int)sum : null;
    }
}
