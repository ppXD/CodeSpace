namespace CodeSpace.Messages.Tasks.Effort;

/// <summary>
/// The OPEN-STRING effort modes the router routes a task at — the wire value a <c>RoutePlan.EffortMode</c>
/// carries and (by the effort-mode ≡ bounds-preset convention) a bounds preset is resolved by. Consts (NOT an
/// enum, Rule 18.1) so a new effort tier is a new const + a new bounds-preset folder, never a core-enum edit.
///
/// <para><see cref="Auto"/> is a SENTINEL, not a real tier: it requests the classifier and NEVER reaches L3 — by
/// the time a <c>RoutePlan</c> is produced the router has replaced it with a concrete tier (the heuristic always
/// asks the operator to confirm, so a human's chosen tier short-circuits the next route). The other three are
/// the shipped tiers, each with a bounds preset of the same kind string.</para>
/// </summary>
public static class TaskEffortModes
{
    /// <summary>SENTINEL — triggers the classifier; never the EffortMode of a produced RoutePlan (the router resolves it to a concrete tier first).</summary>
    public const string Auto = "auto";

    /// <summary>The cheapest tier — a single tight pass (the empty/code-only catch-all in <c>EffortPolicy</c>).</summary>
    public const string Quick = "quick";

    /// <summary>The default moderate tier — code + cross-file / tests work.</summary>
    public const string Standard = "standard";

    /// <summary>The most generous tier — risky side effects or a high cost estimate.</summary>
    public const string Deep = "deep";
}
