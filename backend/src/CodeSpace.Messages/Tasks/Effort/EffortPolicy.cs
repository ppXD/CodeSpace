namespace CodeSpace.Messages.Tasks.Effort;

/// <summary>
/// The PURE, DATA-DRIVEN policy that maps generic <see cref="EffortSignals"/> to an effort tier (Rule 18.1 — a
/// pure policy in Messages, NO DI / NO I/O). It is deliberately NOT a switch on a task type: it is a small
/// ORDERED rule table (<see cref="Rows"/>) of <c>predicate(signals) → tier</c>, evaluated first-match, with a
/// final <c>_ => true</c> catch-all that GUARANTEES totality (every signal combination resolves to a tier — the
/// method can never return null / throw on an unmatched input). A new tier or boundary is a new ROW in the
/// table (a predicate over the generic signals), never an edit to the existing rows or a wider enum (Rule 7).
///
/// <para><see cref="Decide"/> first short-circuits an explicit non-<c>"auto"</c> operator request (an operator's
/// chosen tier is honoured verbatim — the table only runs for an <c>"auto"</c> / unspecified request). Each row
/// is unit-pinned in <c>EffortPolicyTests</c> so a reordering or a predicate change is a test-visible decision.</para>
/// </summary>
public static class EffortPolicy
{
    /// <summary>The confidence below which the router shows a confirm card. The heuristic classifier caps its confidence below this on purpose, so an <c>"auto"</c> task always asks the operator (it guesses, it never silently decides).</summary>
    public const double ConfirmConfidenceFloor = 0.6;

    /// <summary>
    /// The effort tier for <paramref name="signals"/>. A non-blank <paramref name="requestedEffort"/> other than
    /// <c>"auto"</c> short-circuits — the operator's chosen tier wins (the table is for the auto path only).
    /// Otherwise the first matching <see cref="Rows"/> entry decides; the catch-all guarantees a result.
    /// </summary>
    public static string Decide(EffortSignals signals, string? requestedEffort)
    {
        if (IsExplicitOperatorTier(requestedEffort)) return requestedEffort!.Trim();

        return Rows.First(row => row.Matches(signals)).Mode;
    }

    /// <summary>An operator tier is explicit when it is non-blank and not the <c>"auto"</c> sentinel — those are honoured verbatim, the rest classify.</summary>
    private static bool IsExplicitOperatorTier(string? requestedEffort) =>
        !string.IsNullOrWhiteSpace(requestedEffort) && !string.Equals(requestedEffort.Trim(), TaskEffortModes.Auto, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The ORDERED rule table — first match wins. Read top-to-bottom as escalating caution: risky / expensive
    /// work earns the most generous tier; code that fans across files or needs tests earns the moderate tier;
    /// everything else (a localized code edit, an analysis-only task, an empty seed) falls through to the cheap
    /// tier. The last row's <c>_ => true</c> predicate makes the table TOTAL.
    /// </summary>
    private static readonly IReadOnlyList<PolicyRow> Rows = new[]
    {
        new PolicyRow(s => s.RiskySideEffects || string.Equals(s.EstimatedCostTier, "high", StringComparison.OrdinalIgnoreCase), TaskEffortModes.Deep),
        new PolicyRow(s => s.NeedsCodeChange && (s.CrossFile || s.NeedsTestsOrCi), TaskEffortModes.Standard),
        new PolicyRow(_ => true, TaskEffortModes.Quick),
    };

    /// <summary>One row of the policy table — a predicate over the generic signals and the tier it maps to (Rule 18.1 — a pure data tuple, no behaviour beyond the predicate).</summary>
    private sealed record PolicyRow(Func<EffortSignals, bool> Matches, string Mode);
}
