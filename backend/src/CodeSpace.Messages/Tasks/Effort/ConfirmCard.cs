namespace CodeSpace.Messages.Tasks.Effort;

/// <summary>
/// The confirm card the launch surface shows when the router auto-classified at a confidence BELOW
/// <c>EffortPolicy.ConfirmConfidenceFloor</c> (Rule 18.1, a pure data noun). The heuristic classifier is
/// deliberately always-below-floor, so an <c>"auto"</c> task ALWAYS surfaces this card — the cheap guess is the
/// <see cref="SuggestedMode"/>, the <see cref="Rationale"/> explains why, and <see cref="Options"/> are the
/// effort tiers the operator may pick from (one per available bounds preset — derived, never hardcoded). The
/// operator's choice re-enters the router as a <c>RequestedEffort</c>, which then routes deterministically.
/// </summary>
public sealed record ConfirmCard
{
    /// <summary>The tier the classifier guessed — the card's pre-selected option.</summary>
    public required string SuggestedMode { get; init; }

    /// <summary>The classifier's human-readable why-this-tier.</summary>
    public required string Rationale { get; init; }

    /// <summary>The effort tiers the operator may choose — one per available bounds preset, derived from the bounds registry (no hardcoded button list).</summary>
    public required IReadOnlyList<ConfirmCardOption> Options { get; init; }
}

/// <summary>One selectable effort tier on a <see cref="ConfirmCard"/> (Rule 18.1, a pure data noun).</summary>
public sealed record ConfirmCardOption
{
    /// <summary>The effort mode this option selects (open <see cref="TaskEffortModes"/> string) — sent back as the request's <c>RequestedEffort</c>.</summary>
    public required string Mode { get; init; }

    /// <summary>The operator-facing label for the option.</summary>
    public required string Label { get; init; }

    /// <summary>An optional one-line hint describing the tier's bounds. Null ⇒ no hint.</summary>
    public string? Hint { get; init; }
}
