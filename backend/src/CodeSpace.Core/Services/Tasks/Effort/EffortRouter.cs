using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Services.Tasks.Bounds;
using CodeSpace.Core.Services.Tasks.Recipes;
using CodeSpace.Messages.Tasks;
using CodeSpace.Messages.Tasks.Effort;

namespace CodeSpace.Core.Services.Tasks.Effort;

/// <summary>
/// Default <see cref="IEffortRouter"/> — a FLAT pipeline of named steps (Rule 4/5) over the three open-string
/// registries + the pure <c>EffortPolicy</c>. It names NO concrete classifier / recipe / preset type: every
/// branch point is a registry lookup, so a new classification / recipe / bounds strategy needs zero edit here
/// (the fake-classifier contract test proves it). The pipeline: resolve the decision (operator short-circuit vs
/// the default classifier) → policy-decide the effort mode → resolve the recipe (fail-open) → resolve the
/// projection → resolve the bounds preset + merge any caps override → assemble the RoutePlan + a derived confirm
/// card.
/// </summary>
public sealed class EffortRouter : IEffortRouter, IScopedDependency
{
    private readonly IEffortClassifierRegistry _classifiers;
    private readonly ITaskRecipeRegistry _recipes;
    private readonly IBoundsPresetRegistry _bounds;

    public EffortRouter(IEffortClassifierRegistry classifiers, ITaskRecipeRegistry recipes, IBoundsPresetRegistry bounds)
    {
        _classifiers = classifiers;
        _recipes = recipes;
        _bounds = bounds;
    }

    public async Task<RoutePlan> RouteAsync(EffortRouteRequest request, CancellationToken ct)
    {
        var (decision, wasAutoClassified) = await ResolveDecisionAsync(request, ct).ConfigureAwait(false);

        var effortMode = EffortPolicy.Decide(decision.Signals, request.RequestedEffort);

        var recipe = ResolveRecipe(request, decision);

        var projectionKind = request.RequestedProjection ?? recipe.DefaultProjectionKind;

        var (preset, caps) = ResolveCaps(request, effortMode, recipe);

        var needsConfirmCard = wasAutoClassified && decision.Confidence < EffortPolicy.ConfirmConfidenceFloor;

        var confirm = needsConfirmCard ? BuildConfirmCard(decision) : null;

        return BuildPlan(decision, wasAutoClassified, effortMode, recipe, projectionKind, preset, caps, needsConfirmCard, confirm);
    }

    /// <summary>An explicit non-auto operator effort is a DECISION (no classifier runs, confidence 1.0); a null / "auto" effort asks the default classifier.</summary>
    private async Task<(EffortDecision Decision, bool WasAutoClassified)> ResolveDecisionAsync(EffortRouteRequest request, CancellationToken ct)
    {
        if (IsExplicitOperatorEffort(request.RequestedEffort))
            return (OperatorDecision(request), WasAutoClassified: false);

        return (await _classifiers.Default.ClassifyAsync(request, ct).ConfigureAwait(false), WasAutoClassified: true);
    }

    /// <summary>An effort is an explicit operator decision when it is non-blank and not the "auto" sentinel.</summary>
    private static bool IsExplicitOperatorEffort(string? requestedEffort) =>
        !string.IsNullOrWhiteSpace(requestedEffort) && !string.Equals(requestedEffort.Trim(), TaskEffortModes.Auto, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The decision when the operator chose the effort verbatim — no signals, full confidence. The recipe is the
    /// operator's pin if set, else the DEFAULT SHAPE for the explicit tier (<c>RecipeForEffort</c>, data-driven —
    /// no hardcoded switch): explicit <c>standard</c> / <c>deep</c> ⇒ map-fanout, explicit <c>quick</c> ⇒
    /// single-agent. (The AUTO path is unchanged — the heuristic still suggests single-agent + always confirms;
    /// the operator escalates by picking standard/deep in the confirm card, which re-enters HERE as an explicit
    /// tier. A later structured_llm classifier may suggest map-fanout directly.)
    /// </summary>
    private EffortDecision OperatorDecision(EffortRouteRequest request) => new()
    {
        Signals = new EffortSignals(),
        SuggestedEffort = request.RequestedEffort!.Trim(),
        SuggestedRecipe = request.RequestedRecipe ?? _recipes.RecipeForEffort(request.RequestedEffort!.Trim()).RecipeKind,
        Confidence = 1.0,
        ClassifierKind = "operator",
    };

    /// <summary>Resolve the recipe — the request's pin wins, else the classifier's suggestion, else the default; an unknown kind fails OPEN to the default (never throws).</summary>
    private ITaskRecipe ResolveRecipe(EffortRouteRequest request, EffortDecision decision)
    {
        var recipeKind = request.RequestedRecipe ?? decision.SuggestedRecipe ?? _recipes.Default.RecipeKind;

        return _recipes.TryResolve(recipeKind, out var recipe) ? recipe : _recipes.Default;
    }

    /// <summary>Resolve the bounds preset by the effort mode (the effort-mode ≡ preset-kind convention), else the recipe's preset, else none; then merge any caps override on top.</summary>
    private (IBoundsPreset? Preset, RouteCaps Caps) ResolveCaps(EffortRouteRequest request, string effortMode, ITaskRecipe recipe)
    {
        var preset = _bounds.TryResolve(effortMode, out var byMode) ? byMode
            : _bounds.TryResolve(recipe.BoundsPreset, out var byRecipe) ? byRecipe
            : null;

        var caps = MergeCaps(preset?.ToCaps() ?? new RouteCaps(), request.CapsOverride);

        return (preset, caps);
    }

    /// <summary>Merge the operator's override onto the preset's caps — each SET override field wins (tightening / overriding); an unset override field keeps the preset's value.</summary>
    private static RouteCaps MergeCaps(RouteCaps baseCaps, RouteCaps? @override)
    {
        if (@override == null) return baseCaps;

        return baseCaps with
        {
            MaxParallelism = @override.MaxParallelism ?? baseCaps.MaxParallelism,
            MaxRounds = @override.MaxRounds ?? baseCaps.MaxRounds,
            MaxTotalSpawns = @override.MaxTotalSpawns ?? baseCaps.MaxTotalSpawns,
            MaxCostUsd = @override.MaxCostUsd ?? baseCaps.MaxCostUsd,
            AutonomyCeiling = string.IsNullOrWhiteSpace(@override.AutonomyCeiling) ? baseCaps.AutonomyCeiling : @override.AutonomyCeiling,
            RequiresApproval = @override.RequiresApproval || baseCaps.RequiresApproval,
            Extra = @override.Extra.Count > 0 ? @override.Extra : baseCaps.Extra,
        };
    }

    /// <summary>The confirm card — one option per AVAILABLE bounds preset (= available effort tier), DERIVED from the registry (no hardcoded button list). The operator's answer re-enters RouteAsync as RequestedEffort.</summary>
    private ConfirmCard BuildConfirmCard(EffortDecision decision) => new()
    {
        SuggestedMode = decision.SuggestedEffort,
        Rationale = decision.Rationale,
        Options = _bounds.All.Select(ToOption).ToList(),
    };

    private static ConfirmCardOption ToOption(IBoundsPreset preset) => new()
    {
        Mode = preset.PresetKind,
        Label = Capitalize(preset.PresetKind),
        Hint = BuildHint(preset.ToCaps()),
    };

    /// <summary>Title-case the preset kind for display; empty-safe (a malformed empty-kind preset renders blank rather than crashing the confirm card).</summary>
    private static string Capitalize(string value) =>
        string.IsNullOrEmpty(value) ? value : char.ToUpperInvariant(value[0]) + value[1..];

    private static string BuildHint(RouteCaps caps) =>
        $"parallelism {caps.MaxParallelism?.ToString() ?? "default"}, rounds {caps.MaxRounds?.ToString() ?? "default"}, spawns {caps.MaxTotalSpawns?.ToString() ?? "default"}";

    private static RoutePlan BuildPlan(EffortDecision decision, bool wasAutoClassified, string effortMode, ITaskRecipe recipe, string projectionKind, IBoundsPreset? preset, RouteCaps caps, bool needsConfirmCard, ConfirmCard? confirm) => new()
    {
        EffortMode = effortMode,
        RecipeKind = recipe.RecipeKind,
        ProjectionKind = projectionKind,
        BoundsPreset = preset?.PresetKind ?? effortMode,
        Caps = caps,
        RecommendedAutonomy = recipe.RecommendedAutonomy,
        NeedsConfirmCard = needsConfirmCard,
        NeedsPlanReview = recipe.RequiresPlanReview,
        WasAutoClassified = wasAutoClassified,
        ClassifierConfidence = decision.Confidence,
        DegradedReason = null,                       // reserved for the deferred lane-availability degrade phase (always null until then)
        Decision = decision,
        Confirm = confirm,
    };
}
