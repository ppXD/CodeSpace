using CodeSpace.Messages.Enums;

namespace CodeSpace.Core.Services.Agents.ModelCredentials;

/// <summary>
/// D2 — the brain-plane COST CEILING: the pure narrowing a CHEAP in-process structured call applies to its candidate
/// pool before <see cref="ModelPoolSelector"/>'s ordinary "auto = the strongest available brain" ladder ranks it. The
/// cheapest calls on the plane (the launch effort classifier, capability tiering, the nightly lesson distiller, the
/// spec-preview compiler) are one short, schema-bounded question each — they do not need the team's Frontier model, and
/// spending it on them is the single largest avoidable cost on the in-process plane.
///
/// <para>The narrowing is a PREFERENCE, never a gate, and it is bounded by two invariants:</para>
/// <list type="bullet">
/// <item>OPERATOR AUTHORITY IS ABSOLUTE — an <c>IsDefault</c> row is kept in the pool no matter its tier, so a team
/// that starred its Frontier model still gets that model for every call (the same owner-default-authoritative posture
/// the whole selector already has). A caller's ceiling is a cost hint; the operator's star is a decision.</item>
/// <item>ANTI-STRAND — when NO row satisfies the ceiling the FULL pool is returned unchanged (a pricier model beats no
/// model: a strand here is a <c>NoModelStop</c>, mirroring the selector's own <c>Available</c> soft-filter and
/// <see cref="AgentPlaneModelRanking"/>'s Frontier soft-exclusion).</item>
/// </list>
///
/// <para><see cref="ModelCapabilityTier.Unknown"/> satisfies EVERY ceiling by construction of the enum's ascending
/// order (Unknown = 0): an un-tiered / opaque gateway id is not PROVEN expensive, and treating it as over-ceiling would
/// make an entirely un-probed pool behave differently from one with tiers — the ceiling must be byte-identical to
/// today's ordering for a pool it cannot rank. A <c>null</c> ceiling is the identity: no filter at all.</para>
/// </summary>
public static class ModelTierCeiling
{
    /// <summary>
    /// The candidate rows a ceilinged pick ranks: rows whose EFFECTIVE tier (probed ?? declared ?? Unknown) is at or
    /// below <paramref name="ceiling"/>, PLUS every <c>IsDefault</c> row regardless of tier. Returns
    /// <paramref name="pool"/> verbatim when <paramref name="ceiling"/> is null (the identity) or when nothing
    /// satisfies it (anti-strand). Pure; the caller still applies its own ordering ladder to the result.
    /// </summary>
    public static List<T> Apply<T>(List<T> pool, ModelCapabilityTier? ceiling, Func<T, bool> isDefault, Func<T, ModelCapabilityTier?> probedTier, Func<T, ModelCapabilityTier?> declaredTier)
    {
        if (ceiling is not { } cap) return pool;

        var within = pool.Where(m => isDefault(m) || (int)AgentPlaneModelRanking.Effective(probedTier(m), declaredTier(m)) <= (int)cap).ToList();

        return within.Count > 0 ? within : pool;
    }
}
