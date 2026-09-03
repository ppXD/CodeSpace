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

    /// <summary>
    /// D1 — build a price from the two nullable wire fields the model-manager API carries. BOTH null = no price
    /// (the model falls back to the env override / built-in table). BOTH set = a price. Exactly ONE set THROWS:
    /// half a price prices nothing, and storing it would make a model look priced while a capped run still cannot
    /// spend on it — the operator must see the mistake at the edit, not as a mysterious forced stop later.
    /// A negative price is likewise rejected: it would SUBTRACT from a summed bill.
    /// </summary>
    public static ModelPrice? FromNullable(decimal? inputPerMillionUsd, decimal? outputPerMillionUsd)
    {
        if (inputPerMillionUsd is null && outputPerMillionUsd is null) return null;

        if (inputPerMillionUsd is not { } input || outputPerMillionUsd is not { } output)
            throw new ArgumentException("A model price needs BOTH the input and the output per-million rate — set both, or clear both.");

        if (input < 0 || output < 0)
            throw new ArgumentException($"A model price cannot be negative (was {input}/{output} per million).");

        return new ModelPrice { InputPerMillionUsd = input, OutputPerMillionUsd = output };
    }
}
