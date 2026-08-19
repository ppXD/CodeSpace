using CodeSpace.Messages.Dtos.Workflows;

namespace CodeSpace.Core.Services.Workflows.Engine;

/// <summary>
/// The engine's resolved view of a <c>flow.map</c>'s <see cref="MapConfig"/> — the clamped /
/// defaulted values it actually fans out with. Mirrors <c>LoopPlan</c>: a pure value with all
/// normalisation in one place so the engine never trusts a raw config field. <see cref="MaxParallelism"/>
/// is carried RAW (null = inherit the engine-wide setting); the engine resolves + clamps it per map via
/// <c>ResolveBodyParallelism</c>, exactly as it does for a loop body.
/// </summary>
public readonly record struct MapPlan(MapErrorHandling ErrorHandling, string ResultKey, int? MaxParallelism, decimal? MaxCostUsd, int? PromptBudgetChars)
{
    /// <summary>Max element-branches across one map regardless of config — the fan-out runaway guard.</summary>
    public const int MaxBranchesCeiling = 10_000;

    /// <summary>The floor a SET <c>promptBudgetChars</c> is raised to. It buys the projection room for its excerpt notice plus several <c>MapResultsPrompt.MinBranchChars</c> slices, so an opted-in map can never be handed a budget too narrow to say anything useful about the results it is bounding.</summary>
    public const int MinPromptBudgetChars = 2_000;

    /// <summary>Max map-in-map nesting depth (stack + runaway guard; reuses the loop nesting cap).</summary>
    public const int MaxNestingDepth = LoopPlan.MaxNestingDepth;

    /// <summary>The fallback result key when the author leaves it blank.</summary>
    public const string DefaultResultKey = "results";

    /// <summary>Normalise the author's config into a safe plan: lenient error-handling parse + a non-blank result key + a floored prompt budget.</summary>
    public static MapPlan From(MapConfig config) =>
        new(ParseErrorHandling(config.ErrorHandling), NormalizeResultKey(config.ResultKey), config.MaxParallelism, config.MaxCostUsd is > 0 ? config.MaxCostUsd : null, NormalizePromptBudget(config.PromptBudgetChars));

    /// <summary>A non-positive (or absent) budget means NO projection — the pre-existing behaviour for every map. A positive one is raised to <see cref="MinPromptBudgetChars"/> so the projection always has room for its notice plus a readable slice.</summary>
    private static int? NormalizePromptBudget(int? raw) =>
        raw is { } chars && chars > 0 ? Math.Max(chars, MinPromptBudgetChars) : null;

    /// <summary>Lenient parse — only an explicit "continue" opts into continue-on-error; anything else (null, empty, typo) is the safe default Terminate.</summary>
    private static MapErrorHandling ParseErrorHandling(string? raw) =>
        string.Equals(raw, "continue", StringComparison.OrdinalIgnoreCase) ? MapErrorHandling.Continue : MapErrorHandling.Terminate;

    /// <summary>A blank/whitespace key falls back to the default so the reduced array always lands under a usable name.</summary>
    private static string NormalizeResultKey(string? raw) =>
        string.IsNullOrWhiteSpace(raw) ? DefaultResultKey : raw.Trim();
}
