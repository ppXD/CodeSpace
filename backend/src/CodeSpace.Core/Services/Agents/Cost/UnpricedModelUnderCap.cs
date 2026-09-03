using CodeSpace.Messages.Agents;

namespace CodeSpace.Core.Services.Agents.Cost;

/// <summary>
/// The ONE fail-closed policy every cost-cap admission point shares: <b>a run that declares a cost cap may not
/// spend on a model nobody can price.</b>
///
/// <para>The cap is enforced by comparing a SUMMED figure to <c>MaxCostUsd</c>, and an unpriceable model
/// contributes <c>?? 0m</c> to that sum. So before this policy, a $5-capped run on a pool model with no price
/// entry (every Codex / OpenAI / Custom-gateway model, unless an operator filled the env table) summed to $0
/// forever, sailed past the cap, and terminalized Success having spent an unbounded amount. That is the exact
/// refutation evidence this policy exists to make impossible.</para>
///
/// <para><b>Committed policy, not a toggle</b> (no env flag — Rule 8's escape hatch is for VALUES an air-gapped
/// operator must correct, and the correction here is to PRICE the model, which is a first-class field on the model
/// row). The remedy is always nameable, so <see cref="Detail"/> names it.</para>
///
/// <para><b>Scope</b>: only a run WITH a cap is affected. No cap → <see cref="Blocks"/> is false for every model,
/// so an uncapped run stays byte-identical (a null cost stays null and nothing blocks). A null/blank model is also
/// never blocked — that is "the harness default", a name this layer never knew, not an unpriced pool pick.</para>
/// </summary>
public static class UnpricedModelUnderCap
{
    /// <summary>Whether <paramref name="model"/> must be refused: the run declares <paramref name="capUsd"/> AND the model resolves to no price in ANY table (per-row → env → built-in). Pure — the row prices are handed in.</summary>
    public static bool Blocks(string? model, decimal? capUsd, IReadOnlyDictionary<string, ModelPrice>? rowPrices = null) =>
        capUsd is not null && !string.IsNullOrWhiteSpace(model) && AgentCostPricing.PriceFor(model, rowPrices) is null;

    /// <summary>The operator-facing explanation stamped on the refusal — NAMES the model and states the two remedies, so the stop is actionable without reading code.</summary>
    public static string Detail(string model, decimal capUsd) =>
        $"Model '{model}' has no price, so this run's ${capUsd:0.####} cost cap cannot be enforced — price it in the model manager (Settings → Models: $/M in, $/M out) or remove the cap.";
}
