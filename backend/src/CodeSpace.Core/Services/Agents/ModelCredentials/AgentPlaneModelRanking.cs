using CodeSpace.Messages.Enums;

namespace CodeSpace.Core.Services.Agents.ModelCredentials;

/// <summary>
/// P3.4 — the shared "ordinary execution" (agent-plane) auto-pick ranking, deliberately DIFFERENT from the
/// brain-plane's own "auto = strongest available" convention (<see cref="ModelPoolSelector"/>'s brain/reviewer/judge
/// picks, which rank the EFFECTIVE tier descending with no cap): the operator's own per-credential
/// <c>IsDefault</c> star always wins first, same as the brain plane; absent that, the <see cref="ModelCapabilityTier.Frontier"/>
/// tier is avoided — ordinary agent execution is verified downstream by the task's own acceptance checks, so it
/// doesn't need to reach for the priciest tier automatically, only an explicit operator choice (a default, or a
/// credential/model pin) should spend Frontier.
///
/// <para>Frontier is a SOFT exclusion (anti-strand, mirrors <see cref="ModelPoolSelector"/>'s own <c>Available</c>
/// soft-filter): when avoiding it would leave ZERO candidates (the pool is Frontier-only), the full set is used
/// instead — a pricier model beats no model at all.</para>
///
/// <para>Shared by <see cref="ModelCredentialResolver"/> (the actual model + credential pick that drives the real
/// Claude Code / Codex CLI dispatch) and <see cref="ModelPoolSelector.ResolveTeamDefaultProviderAsync"/> (the
/// PROVIDER an un-pinned "auto" run will get, used by <c>HarnessModelReconciler</c> to pick a matching harness) —
/// these two MUST agree on which row wins, or the reconciled harness could target a different provider than the
/// model actually dispatched.</para>
/// </summary>
public static class AgentPlaneModelRanking
{
    /// <summary>
    /// Orders <paramref name="pool"/> for an UNPINNED agent-plane auto-pick: <c>IsDefault</c> first, then the
    /// EFFECTIVE capability tier descending WITH Frontier soft-excluded (falls back to the full pool only when
    /// Frontier is the sole tier present). Callers append their own final deterministic tie-break (e.g.
    /// <c>.ThenBy(m => m.Id)</c>) for genuinely-tied rows.
    /// </summary>
    public static IOrderedEnumerable<T> Rank<T>(IEnumerable<T> pool, Func<T, bool> isDefault, Func<T, ModelCapabilityTier?> probedTier, Func<T, ModelCapabilityTier?> declaredTier)
    {
        var list = pool as IReadOnlyCollection<T> ?? pool.ToList();

        var nonFrontier = list.Where(m => Effective(probedTier(m), declaredTier(m)) != ModelCapabilityTier.Frontier).ToList();
        var ranked = nonFrontier.Count > 0 ? (IEnumerable<T>)nonFrontier : list;

        return ranked
            .OrderByDescending(isDefault)
            .ThenByDescending(m => (int)Effective(probedTier(m), declaredTier(m)));
    }

    /// <summary>The EFFECTIVE capability tier: the objectively-PROBED tier wins, else the declared/brain-inferred tier, else Unknown — mirrors <see cref="ModelPoolSelector"/>'s own identical formula.</summary>
    public static ModelCapabilityTier Effective(ModelCapabilityTier? probed, ModelCapabilityTier? declared) => probed ?? declared ?? ModelCapabilityTier.Unknown;
}
