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
    /// An absurd upper bound on a per-million price (USD). No real model is within four orders of magnitude of it.
    /// It is the ONE definition shared by every layer that accepts a price — the API edit (<see cref="FromNullable"/>),
    /// the env-override parser and the stored-row reader (<c>AgentCostPricing</c>) — so a value can never be accepted
    /// at one door and silently ignored at another, which would show the operator a price the engine treats as
    /// unpriced. Bounding it also keeps the cost arithmetic far below <c>decimal.MaxValue</c> at <c>int.MaxValue</c>
    /// tokens, which is what makes "pricing never throws" true. Pinned by a test.
    /// </summary>
    public const decimal MaxPerMillionUsd = 100_000m;

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

        // Reject the fat-finger AT THE EDIT. The pricer skips an out-of-bound stored row (it cannot compute with it
        // without overflowing), so accepting one here would leave the UI showing a price while every capped run
        // still refused the model as unpriced — the worst of both answers.
        if (input > MaxPerMillionUsd || output > MaxPerMillionUsd)
            throw new ArgumentException($"A model price cannot exceed ${MaxPerMillionUsd:N0} per million tokens (was {input}/{output}).");

        return new ModelPrice { InputPerMillionUsd = input, OutputPerMillionUsd = output };
    }
}
