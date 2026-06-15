using CodeSpace.Core.DependencyInjection;

namespace CodeSpace.Core.Services.Tasks.Recipes;

/// <summary>
/// One task recipe — the polymorphic seam that says HOW a task is shaped (which projection builds it, what
/// bounds preset it defaults to, whether it needs a plan review) for one recipe kind (Rule 18.3, each impl
/// beside its variant folder under <c>Recipes/&lt;Kind&gt;/</c>). A recipe is the bridge from the classifier's
/// suggested recipe to the projection layer: the router reads <see cref="DefaultProjectionKind"/> to pick a
/// builder and <see cref="BoundsPreset"/> as the bounds fallback. Self-registers via the
/// <see cref="ISingletonDependency"/> marker, so a new recipe is a sibling folder with ZERO edit to the registry
/// / router (Rule 7 — a new recipe is a sibling impl, never a wider interface).
///
/// <para><see cref="GoalFrame"/> and <see cref="RecommendedPhaseLabels"/> are provenance / UI hints; only
/// <see cref="DefaultProjectionKind"/>, <see cref="BoundsPreset"/>, <see cref="RecommendedAutonomy"/> and
/// <see cref="RequiresPlanReview"/> drive the RoutePlan. Single-agent is the only recipe shipped this PR.</para>
/// </summary>
public interface ITaskRecipe
{
    /// <summary>The recipe kind this impl handles — the open string the registry indexes + resolves it by (e.g. <c>"single-agent"</c>). Mirrors <c>IAgentHarness.Kind</c>.</summary>
    string RecipeKind { get; }

    /// <summary>
    /// The OPEN-STRING effort tiers this recipe is the DEFAULT SHAPE for — the data that drives effort→recipe
    /// routing WITHOUT a hardcoded switch (the registry resolves the recipe whose <c>ServesEfforts</c> contains
    /// the explicit tier). A recipe DECLARES its own purpose here (Rule 7 — cohesive to the recipe, not a wider
    /// router concern); each tier is claimed by AT MOST one recipe (the registry asserts no overlap). Empty ⇒
    /// the recipe is opt-in only (never the default for any tier — reached only by an explicit RequestedRecipe).
    /// </summary>
    IReadOnlyList<string> ServesEfforts { get; }

    /// <summary>A one-line prose frame describing what this recipe does — provenance / a UI hint.</summary>
    string GoalFrame { get; }

    /// <summary>The bounds preset kind this recipe falls back to when the effort mode names no preset of its own — an open <c>IBoundsPreset.PresetKind</c> string.</summary>
    string BoundsPreset { get; }

    /// <summary>The autonomy tier this recipe recommends, as an open tier-name string (e.g. <c>"Standard"</c>).</summary>
    string RecommendedAutonomy { get; }

    /// <summary>The projection kind this recipe builds the run with when the request pins none — an open <c>TaskProjectionKinds</c> string the projection registry resolves a builder by.</summary>
    string DefaultProjectionKind { get; }

    /// <summary>Whether a run from this recipe should pause for a plan review before executing.</summary>
    bool RequiresPlanReview { get; }

    /// <summary>The phase labels this recipe recommends — a UI hint for the run's phase tree.</summary>
    IReadOnlyList<string> RecommendedPhaseLabels { get; }

    /// <summary>
    /// The OPEN-STRING deployment capability this recipe's projection needs at EXECUTION (a
    /// <c>TaskCapabilities</c> value an <c>ICapabilityProbe</c> answers for); null = none needed. A recipe
    /// DECLARES its own precondition here (Rule 7 — cohesive recipe metadata, not a wider router concern): the
    /// router checks the probe registry and, when the capability is unavailable, degrades to
    /// <see cref="DegradesToRecipe"/> rather than projecting a run that would fail its own execution-time gate.
    /// </summary>
    string? RequiresCapability { get; }

    /// <summary>
    /// The OPEN-STRING recipe kind to fall back to when <see cref="RequiresCapability"/> is unavailable; null =
    /// fall to the registry <c>Default</c>. The data the router's degrade step reads (no hardcoded fallback map).
    /// </summary>
    string? DegradesToRecipe { get; }
}
