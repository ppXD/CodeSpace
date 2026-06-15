namespace CodeSpace.Messages.Tasks.Effort;

/// <summary>
/// The input to the effort router (Rule 18.1, a pure data noun) — the normalized <see cref="Seed"/> plus the
/// operator's optional OVERRIDES. A non-blank <see cref="RequestedEffort"/> (other than <c>"auto"</c>) is an
/// operator DECISION the router honours verbatim (no classifier runs); a null / <c>"auto"</c> requested effort
/// asks the classifier. <see cref="RequestedRecipe"/> / <see cref="RequestedProjection"/> are escape hatches
/// that pin the recipe / projection regardless of the classifier's suggestion, and <see cref="CapsOverride"/>
/// merges on top of the resolved preset's caps (an operator may tighten / override specific bounds).
///
/// <para>The operator's answer to a confirm card RE-ENTERS the router as a fresh request with
/// <see cref="RequestedEffort"/> set to the chosen tier — which short-circuits the classifier, so the second
/// route is deterministic.</para>
/// </summary>
public sealed record EffortRouteRequest
{
    /// <summary>The normalized task seed to route.</summary>
    public required TaskLaunchSeed Seed { get; init; }

    /// <summary>An operator-chosen effort tier (open <see cref="TaskEffortModes"/> string). Non-blank and not <c>"auto"</c> ⇒ the router honours it and skips the classifier. Null / <c>"auto"</c> ⇒ classify.</summary>
    public string? RequestedEffort { get; init; }

    /// <summary>An operator-pinned recipe (open <see cref="TaskRecipeKinds"/> string) that overrides the classifier's suggested recipe. Null ⇒ use the suggestion / the default recipe.</summary>
    public string? RequestedRecipe { get; init; }

    /// <summary>An operator-pinned projection kind that overrides the recipe's default projection. Null ⇒ use the recipe's default projection.</summary>
    public string? RequestedProjection { get; init; }

    /// <summary>Bounds the operator wants merged on top of the resolved preset's caps (set fields win). Null ⇒ the preset's caps unchanged.</summary>
    public RouteCaps? CapsOverride { get; init; }
}
