namespace CodeSpace.Messages.Agents;

/// <summary>
/// The per-model token price used to turn a captured <see cref="AgentTokenUsage"/> into a USD cost (SOTA #4). A pure
/// data noun (Rule 18.1): input + output price PER MILLION tokens, in USD. Decimal (not double) to match
/// <c>RouteCaps.MaxCostUsd</c> and avoid float drift on a summed bill. Prices DRIFT and the provider API does not
/// expose them, so the table that maps a model id to a <see cref="ModelPrice"/> is operator-correctable via an env
/// override (see <c>AgentCostPricing</c>) rather than a permanent constant.
/// </summary>
public sealed record ModelPrice
{
    /// <summary>USD charged per 1,000,000 input (prompt) tokens.</summary>
    public required decimal InputPerMillionUsd { get; init; }

    /// <summary>USD charged per 1,000,000 output (completion) tokens.</summary>
    public required decimal OutputPerMillionUsd { get; init; }
}
