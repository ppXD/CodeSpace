using System.Globalization;
using CodeSpace.Messages.Agents;

namespace CodeSpace.Core.Services.Agents.Cost;

/// <summary>
/// Turns a captured <see cref="AgentTokenUsage"/> into a USD cost by pricing the run's model (SOTA #4). Pure +
/// static — no DB, no I/O — so it is unit-pinned and safe to call from a read query or the enforcement fold.
///
/// <para><b>Resolution order</b>: the caller-supplied PER-ROW price table (what the operator typed next to the model
/// in the model manager) → the <see cref="PriceTableEnvVar"/> override → the built-in table → unknown. The row table
/// is handed IN (a plain dictionary a caller loaded from <c>IModelPriceResolver</c>) so this class stays pure and
/// unit-pinned — it never touches the DB.</para>
///
/// <para><b>Fail-open on unknown</b> (the locked policy HERE): a null/blank model, or a model with no price entry,
/// returns <c>null</c> cost — NOT zero, NOT a throw. The caller treats null as "cost-unknown". A run with NO cost cap
/// is unaffected by that unknown. A run WITH a cap is a different matter: an unpriced model makes the cap
/// unenforceable, so the admission points fail CLOSED via <see cref="UnpricedModelUnderCap"/> rather than spending
/// blind — see that class for the policy.</para>
///
/// <para><b>Prices DRIFT and the provider API does not expose them</b>, so the seeded default table is
/// operator-correctable via the <see cref="PriceTableEnvVar"/> env override (Rule 8 — the const name is pinned by a
/// test; the override is a lenient CSV so a malformed entry is skipped, never crashes pricing). A deployment that
/// runs Codex/OpenAI agents on a cost cap adds their prices via the env with zero code change.</para>
/// </summary>
public static class AgentCostPricing
{
    /// <summary>
    /// Operators correct/extend model prices here (drift / new models / Codex) WITHOUT a code change (Rule 8 escape
    /// hatch). Format: a semicolon-separated list of <c>model=inputPerM/outputPerM</c> (USD per 1M tokens), e.g.
    /// <c>"gpt-5.4-codex=2.5/10;claude-opus-4-8=5/25"</c>. An entry overrides the seeded default for that model;
    /// malformed entries are skipped (lenient — pricing never throws). Pinned by a test.
    /// </summary>
    public const string PriceTableEnvVar = "CODESPACE_AGENT_MODEL_PRICES";

    /// <summary>An absurd upper bound on a per-million price (USD). No real model is within four orders of magnitude of this; an env entry above it is a fat-finger and is SKIPPED (lenient). Bounding the price keeps <see cref="CostUsd"/> arithmetic far below <c>decimal.MaxValue</c> even at <c>int.MaxValue</c> tokens — so a malformed override can never overflow the pricing math into a throw (the "pricing never throws" contract).</summary>
    internal const decimal MaxPricePerMillionUsd = 100_000m;

    /// <summary>The seeded default prices (USD per 1,000,000 tokens, input/output), cache-dated to the claude-api skill (2026-05-26). Codex/OpenAI models are intentionally ABSENT → UNKNOWN until an operator adds them via the env override. Prices drift — the env override is the correction path.</summary>
    private static readonly IReadOnlyDictionary<string, ModelPrice> DefaultPrices = new Dictionary<string, ModelPrice>(StringComparer.OrdinalIgnoreCase)
    {
        ["claude-opus-4-8"] = new() { InputPerMillionUsd = 5m, OutputPerMillionUsd = 25m },
        ["claude-opus-4-7"] = new() { InputPerMillionUsd = 5m, OutputPerMillionUsd = 25m },
        ["claude-opus-4-6"] = new() { InputPerMillionUsd = 5m, OutputPerMillionUsd = 25m },
        ["claude-sonnet-4-6"] = new() { InputPerMillionUsd = 3m, OutputPerMillionUsd = 15m },
        ["claude-haiku-4-5"] = new() { InputPerMillionUsd = 1m, OutputPerMillionUsd = 5m },
        ["claude-fable-5"] = new() { InputPerMillionUsd = 10m, OutputPerMillionUsd = 50m },
    };

    /// <summary>
    /// The USD cost of <paramref name="inputTokens"/> + <paramref name="outputTokens"/> on <paramref name="model"/>,
    /// or <c>null</c> when the model is null/blank/unpriced (fail-open). Pure. <paramref name="rowPrices"/> is the
    /// operator's per-model-row price table (from <c>IModelPriceResolver</c>) and WINS over the env + built-in tables;
    /// omitting it is byte-identical to the pre-row behaviour.
    /// </summary>
    public static decimal? CostUsd(string? model, int inputTokens, int outputTokens, IReadOnlyDictionary<string, ModelPrice>? rowPrices = null)
    {
        if (PriceFor(model, rowPrices) is not { } price) return null;

        // Clamp negative token counts to 0: a harness/corrupt result reporting a negative count must NEVER yield a
        // negative cost — that would SUBTRACT from the summed RunSpendUsd the cost cap reads, masking real spend.
        var input = Math.Max(0, inputTokens);
        var output = Math.Max(0, outputTokens);

        return input * price.InputPerMillionUsd / 1_000_000m + output * price.OutputPerMillionUsd / 1_000_000m;
    }

    /// <summary>The effective price for <paramref name="model"/> in resolution order — the operator's per-row price, then the env override, then the built-in default — or null when the model is null/blank/unpriced everywhere. PUBLIC because the fail-closed admission points (<see cref="UnpricedModelUnderCap"/>) ask "is this model priceable?" without pricing a call.</summary>
    public static ModelPrice? PriceFor(string? model, IReadOnlyDictionary<string, ModelPrice>? rowPrices = null)
    {
        if (string.IsNullOrWhiteSpace(model)) return null;

        var name = model.Trim();

        // A row price rides the SAME magnitude bound the env override does. The column is an unconstrained
        // `numeric`, so a fat-fingered 1e30 would overflow the multiplication below into a throw — and "pricing
        // never throws" is a contract the enforcement fold depends on. An out-of-bound row is SKIPPED (it falls
        // through to the env/built-in tables, and reads as unpriced if neither has it), never fatal.
        if (rowPrices is not null && rowPrices.TryGetValue(name, out var rowPrice) && IsUsable(rowPrice)) return rowPrice;

        return ResolveTable().TryGetValue(name, out var price) ? price : null;
    }

    /// <summary>Whether a price can be used in <see cref="CostUsd"/>'s arithmetic without risking an overflow throw — the same bound <see cref="TryParseEntry"/> enforces on an env entry, applied to an operator's DB row.</summary>
    private static bool IsUsable(ModelPrice price) =>
        price.InputPerMillionUsd >= 0 && price.OutputPerMillionUsd >= 0
        && price.InputPerMillionUsd <= MaxPricePerMillionUsd && price.OutputPerMillionUsd <= MaxPricePerMillionUsd;

    /// <summary>The seeded defaults overlaid by the lenient env CSV. Internal so a test can drive the env override + the malformed-entry tolerance.</summary>
    internal static IReadOnlyDictionary<string, ModelPrice> ResolveTable()
    {
        var raw = Environment.GetEnvironmentVariable(PriceTableEnvVar);

        if (string.IsNullOrWhiteSpace(raw)) return DefaultPrices;

        var table = new Dictionary<string, ModelPrice>(DefaultPrices, StringComparer.OrdinalIgnoreCase);

        foreach (var entry in raw.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (TryParseEntry(entry, out var model, out var price)) table[model] = price;
        }

        return table;
    }

    /// <summary>Parse one <c>model=in/out</c> entry. Lenient: any shape error → false (the entry is skipped, never throws).</summary>
    private static bool TryParseEntry(string entry, out string model, out ModelPrice price)
    {
        model = "";
        price = null!;

        var eq = entry.IndexOf('=');
        if (eq <= 0 || eq == entry.Length - 1) return false;

        var name = entry[..eq].Trim();
        var parts = entry[(eq + 1)..].Split('/', StringSplitOptions.TrimEntries);

        if (name.Length == 0 || parts.Length != 2) return false;

        // AllowDecimalPoint only (no thousands separators / sign): "2,5" is AMBIGUOUS across cultures, so reject it
        // rather than silently misparse a comma as a 1000s-group (NumberStyles.Number would turn "2,5" into 25).
        // Out-of-range values (negative, or absurdly large enough to overflow CostUsd) are SKIPPED, not fatal.
        if (!decimal.TryParse(parts[0], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var input) ||
            !decimal.TryParse(parts[1], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var output) ||
            input < 0 || output < 0 || input > MaxPricePerMillionUsd || output > MaxPricePerMillionUsd)
            return false;

        model = name;
        price = new ModelPrice { InputPerMillionUsd = input, OutputPerMillionUsd = output };
        return true;
    }
}
