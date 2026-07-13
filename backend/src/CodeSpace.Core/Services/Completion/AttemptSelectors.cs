using CodeSpace.Messages.Contracts;

namespace CodeSpace.Core.Services.Completion;

/// <summary>
/// The plan-version-aware unit key both selectors group by (P1b): <c>s1@plan-v1</c> and <c>s1@plan-v2</c> are
/// DIFFERENT units — a superseded plan's attempt can never answer for the current plan's unit. Attempts without a
/// stamped <see cref="WorkUnitRef"/> (Legacy/Shadow tapes) key on the bare unit id with null plan coordinates —
/// distinct from every plan-bound key by construction.
/// </summary>
public readonly record struct UnitKey(Guid? WorkPlanId, int? PlanVersion, string UnitId)
{
    public static UnitKey For(AttemptProjection attempt) => new(attempt.WorkUnit?.WorkPlanId, attempt.WorkUnit?.PlanVersion, attempt.UnitId);
}

/// <summary>
/// THE two attempt selectors (P1b / Lock Clause 3) — the ONLY places "which attempt counts" is decided, shared by
/// the completion composer, CES, and the metric plane so no consumer can invent a third rule.
/// <para><b>Operational</b>: highest authorization ordinal per unit key, REGARDLESS of state — a newly
/// server-authorized attempt supersedes the old operational attempt the moment it is authorized; even while it is
/// unsettled, a consumer must never fall back to a superseded attempt's Passed receipt to terminalize (it waits or
/// parks). <b>Metric @1</b>: LOWEST authorization ordinal per unit key — the first server-authorized attempt, the
/// only attempt VDS@1 may ever read; never best-of-N, never a later fix, never a human-corrected re-run.</para>
/// </summary>
public static class AttemptSelectors
{
    public static IReadOnlyDictionary<UnitKey, AttemptProjection> SelectOperationalActive(IReadOnlyList<AttemptProjection> attempts) =>
        SelectBy(attempts, replaceWhen: (candidate, incumbent) => candidate.AttemptOrdinal > incumbent.AttemptOrdinal);

    public static IReadOnlyDictionary<UnitKey, AttemptProjection> SelectFirstAuthorized(IReadOnlyList<AttemptProjection> attempts) =>
        SelectBy(attempts, replaceWhen: (candidate, incumbent) => candidate.AttemptOrdinal < incumbent.AttemptOrdinal);

    private static IReadOnlyDictionary<UnitKey, AttemptProjection> SelectBy(IReadOnlyList<AttemptProjection> attempts, Func<AttemptProjection, AttemptProjection, bool> replaceWhen)
    {
        var selected = new Dictionary<UnitKey, AttemptProjection>();

        foreach (var attempt in attempts)
        {
            var key = UnitKey.For(attempt);

            if (!selected.TryGetValue(key, out var incumbent) || replaceWhen(attempt, incumbent))
                selected[key] = attempt;
        }

        return selected;
    }
}
