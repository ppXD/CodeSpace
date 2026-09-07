using System.Numerics;
using CodeSpace.Core.Services.Workflows.Llm;
using CodeSpace.Messages.Agents;

namespace CodeSpace.Core.Services.Agents.Cost;

/// <summary>Prices a complete provider-reported usage total. Missing, invalid, partial or unrepresentable cost stays unknown.</summary>
public static class LlmUsageCost
{
    public static decimal? Usd(string? model, LlmUsage usage, IReadOnlyDictionary<string, ModelPrice>? prices = null)
    {
        if (!usage.HasCompleteTokenCounts || AgentCostPricing.PriceFor(model, prices) is not { } price) return null;
        var input = usage.InputTokens!.Value;
        var output = usage.OutputTokens!.Value;
        var value = AgentCostPricing.CostUsd(model, input, output, prices);
        if (value is not { } cost) return null;

        // PostgreSQL numeric preserves every .NET decimal value. The pricing division itself can still round
        // below decimal's representable precision; compare exact base-10 coefficients before claiming an actual.
        var inputPrice = Parts(price.InputPerMillionUsd);
        var outputPrice = Parts(price.OutputPerMillionUsd);
        var scale = Math.Max(inputPrice.Scale, outputPrice.Scale);
        var numerator = inputPrice.Coefficient * input * BigInteger.Pow(10, scale - inputPrice.Scale) + outputPrice.Coefficient * output * BigInteger.Pow(10, scale - outputPrice.Scale);
        var actual = Parts(cost);
        return numerator * BigInteger.Pow(10, actual.Scale) == actual.Coefficient * BigInteger.Pow(10, scale + 6) ? cost : null;
    }

    private static (BigInteger Coefficient, int Scale) Parts(decimal value)
    {
        var bits = decimal.GetBits(value);
        var coefficient = (BigInteger)(uint)bits[0] + ((BigInteger)(uint)bits[1] << 32) + ((BigInteger)(uint)bits[2] << 64);
        return (coefficient, (bits[3] >> 16) & 0xff);
    }
}
