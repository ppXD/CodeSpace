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

/// <summary>
/// The run cannot proceed under its cost cap because a model it would spend on has no price
/// (<see cref="UnpricedModelUnderCap"/>). Thrown by all three admission points — the brain-plane budget guard, the
/// post-decision bound, and the agent-wave staging — so ONE catch in <c>AgentSupervisorNode</c> handles them alike.
///
/// <para>The remedy is a STORED FACT the operator can change while the run is parked (price the model), so this is
/// deliberately NOT a terminal condition: the node parks the run on the existing wake ladder and the next wake
/// re-evaluates the price. That is why it is a distinct type from <c>LlmBudgetExceededException</c> — a spent budget
/// heals only by changing the run's own configuration, so it stops; a missing price heals by an edit elsewhere, so
/// it waits. Only when the whole park window elapses unpriced does the run end honestly.</para>
/// </summary>
public sealed class UnpricedModelUnderCapException(string model, decimal capUsd, string where)
    : InvalidOperationException($"Refused {where}: {UnpricedModelUnderCap.Detail(model, capUsd)}"), Messages.Failures.IFailure
{
    /// <summary>The model nobody could price — what the park's reason names so the operator knows which row to edit.</summary>
    public string Model { get; } = model;

    public decimal CapUsd { get; } = capUsd;

    /// <summary>Which admission point refused, for the log line only — never for control flow.</summary>
    public string Where { get; } = where;

    /// <summary>The operator-facing sentence on its own, without the "Refused …:" frame — what the park/stop carries as its detail.</summary>
    public string Detail { get; } = UnpricedModelUnderCap.Detail(model, capUsd);

    // The failure taxonomy (#1353): PreconditionRequired — price this model (or drop the cap) and the very same
    // request works. NOT Exhausted: no budget is spent, and retrying the identical call unchanged never succeeds.
    Messages.Failures.FailureKind Messages.Failures.IFailure.Kind => Messages.Failures.FailureKind.PreconditionRequired;
    string Messages.Failures.IFailure.Code => Messages.Failures.FailureCodes.ModelPriceRequired;
    string? Messages.Failures.IFailure.ClientMessage => "This run has a cost cap, but one of its models has no price.";
}
