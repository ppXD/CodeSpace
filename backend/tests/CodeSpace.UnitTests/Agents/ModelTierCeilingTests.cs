using CodeSpace.Core.Services.Agents.ModelCredentials;
using CodeSpace.Core.Services.Workflows.Llm;
using CodeSpace.Messages.Enums;
using Shouldly;

namespace CodeSpace.UnitTests.Agents;

/// <summary>
/// 🟢 Unit: pure-logic pins for <see cref="ModelTierCeiling"/> (D2) — the cheap-caller cost ceiling on the BRAIN plane.
/// The brain plane's unpinned ladder ranks the effective tier DESCENDING ("auto = the strongest available brain"), so
/// before this ceiling the plane's cheapest calls all landed on the team's Frontier model. The ceiling narrows the
/// candidate set under two invariants: an <c>IsDefault</c> row survives regardless of tier (operator authority is
/// absolute), and a pool where NOTHING satisfies the ceiling is used whole (anti-strand — a pricier model beats a
/// <c>NoModelStop</c>). The bound is <c>&lt;=</c>, so a Strong ceiling admits Basic when that is the only sub-ceiling
/// row — pinned below as the deliberate trade it is, not left implicit.
///
/// <para><see cref="Pick"/> composes the ceiling with the SAME two steps <c>ModelPoolSelector.SelectAsync</c>'s
/// unpinned path applies around it — the <c>Available</c> soft-filter first, then the IsDefault → effective-tier →
/// model-id → row-id ladder — so these cases pin the ORDER of composition, not just the filter in isolation.
/// <c>ModelPoolSelectorFlowTests</c> is the DB-tier truth for the real query; this tier pins the policy.</para>
/// </summary>
[Trait("Category", "Unit")]
public sealed class ModelTierCeilingTests
{
    private sealed record Row(string ModelId, bool IsDefault, ModelCapabilityTier? Tier, ModelCapabilityTier? ProbedTier, bool? Available = null, int Id = 0);

    [Fact]
    public void An_operator_default_wins_even_when_its_tier_is_over_the_ceiling()
    {
        var pool = new List<Row> { new("starred-frontier", IsDefault: true, Tier: ModelCapabilityTier.Frontier, ProbedTier: null, Id: 1) };

        Pick(pool, ModelCapabilityTier.Strong)!.ModelId
            .ShouldBe("starred-frontier", "operator authority is absolute — a caller's cost ceiling is a hint, the operator's star is a decision");
    }

    [Fact]
    public void An_operator_default_wins_over_a_cheaper_row_that_satisfies_the_ceiling()
    {
        var pool = new List<Row>
        {
            new("starred-frontier", IsDefault: true, Tier: ModelCapabilityTier.Frontier, ProbedTier: null, Id: 1),
            new("unstarred-strong", IsDefault: false, Tier: ModelCapabilityTier.Strong, ProbedTier: null, Id: 2),
        };

        Pick(pool, ModelCapabilityTier.Strong)!.ModelId
            .ShouldBe("starred-frontier", "the ceiling must not demote the starred row when a satisfying alternative exists — that would silently override the operator");
    }

    [Fact]
    public void The_ceiling_picks_the_cheaper_row_over_a_stronger_one()
    {
        var pool = new List<Row>
        {
            new("the-frontier-one", IsDefault: false, Tier: ModelCapabilityTier.Frontier, ProbedTier: null, Id: 1),
            new("the-strong-one", IsDefault: false, Tier: ModelCapabilityTier.Strong, ProbedTier: null, Id: 2),
        };

        Pick(pool, ModelCapabilityTier.Strong)!.ModelId
            .ShouldBe("the-strong-one", "the whole point of D2 — a cheap call stops automatically spending the team's Frontier model");
    }

    [Fact]
    public void A_Strong_ceiling_admits_Basic_when_that_is_the_only_row_under_it()
    {
        // The bound is <=, NOT "the tier the ceiling names". A team holding {Basic, Frontier} therefore runs every
        // ceilinged caller on Basic. ACCEPTED DELIBERATELY: the three ceilinged callers each have a fail-open floor for a
        // poor answer (the effort classifier falls to the deterministic heuristic, the distiller refuses an unusable
        // proposal, the spec preview degrades to no suggestion), so the cost is one degraded SUGGESTION a human reviews,
        // never a wrong action. A team that wants Frontier on these calls stars it — IsDefault beats any ceiling.
        var pool = new List<Row>
        {
            new("the-frontier-one", IsDefault: false, Tier: ModelCapabilityTier.Frontier, ProbedTier: null, Id: 1),
            new("the-basic-one", IsDefault: false, Tier: ModelCapabilityTier.Basic, ProbedTier: null, Id: 2),
        };

        Pick(pool, ModelCapabilityTier.Strong)!.ModelId
            .ShouldBe("the-basic-one", "a Strong ceiling admits every tier AT OR UNDER Strong — Basic included; it is a ceiling, not a floor");
    }

    [Fact]
    public void A_Strong_ceiling_still_prefers_Strong_over_Basic_when_both_are_present()
    {
        // The other half of the same contract: the ceiling NARROWS, the ladder still ranks strongest-first inside it,
        // so Basic is reached only when nothing better is under the ceiling.
        var pool = new List<Row>
        {
            new("aaa-basic", IsDefault: false, Tier: ModelCapabilityTier.Basic, ProbedTier: null, Id: 1),
            new("zzz-strong", IsDefault: false, Tier: ModelCapabilityTier.Strong, ProbedTier: null, Id: 2),
        };

        Pick(pool, ModelCapabilityTier.Strong)!.ModelId
            .ShouldBe("zzz-strong", "inside the ceiling the ordinary strongest-first ladder still decides — despite 'aaa' sorting first");
    }

    [Fact]
    public void A_pool_with_nothing_under_the_ceiling_still_answers_anti_strand()
    {
        var pool = new List<Row>
        {
            new("frontier-a", IsDefault: false, Tier: ModelCapabilityTier.Frontier, ProbedTier: null, Id: 1),
            new("frontier-b", IsDefault: false, Tier: ModelCapabilityTier.Frontier, ProbedTier: null, Id: 2),
        };

        Pick(pool, ModelCapabilityTier.Strong)!.Id
            .ShouldBe(1, "a Frontier-only pool must NEVER strand the call on cost — the unceilinged ladder decides instead");
    }

    [Fact]
    public void An_unknown_tier_satisfies_the_ceiling_because_it_is_not_proven_expensive()
    {
        var pool = new List<Row>
        {
            new("the-frontier-one", IsDefault: false, Tier: ModelCapabilityTier.Frontier, ProbedTier: null, Id: 1),
            new("the-opaque-one", IsDefault: false, Tier: null, ProbedTier: null, Id: 2),
        };

        Pick(pool, ModelCapabilityTier.Strong)!.ModelId
            .ShouldBe("the-opaque-one", "an un-tiered / opaque gateway id is not PROVEN expensive; treating it as over-ceiling would make an un-probed pool behave differently from a tiered one");
    }

    [Theory]
    [InlineData(ModelCapabilityTier.Strong)]
    [InlineData(ModelCapabilityTier.Basic)]
    public void A_probed_tier_decides_the_ceiling_not_the_declared_one(ModelCapabilityTier ceiling)
    {
        // The row DECLARES Unknown (an opaque id the brain could not read) but PROBED Frontier — the effective tier is
        // the probed one, so the ceiling must exclude it exactly as it would an openly-declared Frontier row.
        var pool = new List<Row>
        {
            new("opaque-but-probed-frontier", IsDefault: false, Tier: ModelCapabilityTier.Unknown, ProbedTier: ModelCapabilityTier.Frontier, Id: 1),
            new("declared-basic", IsDefault: false, Tier: ModelCapabilityTier.Basic, ProbedTier: null, Id: 2),
        };

        Pick(pool, ceiling)!.ModelId.ShouldBe("declared-basic", "the ceiling reads the EFFECTIVE tier (probed ?? declared), the same formula the ladder ranks by");
    }

    [Fact]
    public void A_null_ceiling_is_the_identity_the_strongest_row_still_wins()
    {
        var pool = new List<Row>
        {
            new("the-frontier-one", IsDefault: false, Tier: ModelCapabilityTier.Frontier, ProbedTier: null, Id: 1),
            new("the-strong-one", IsDefault: false, Tier: ModelCapabilityTier.Strong, ProbedTier: null, Id: 2),
        };

        Pick(pool, ceiling: null)!.ModelId
            .ShouldBe("the-frontier-one", "every caller but the four cheap ones passes no ceiling and must be byte-identical to before D2 — 'auto = the strongest available brain'");
    }

    [Fact]
    public void Availability_is_the_OUTER_bound_a_dead_row_under_the_ceiling_never_wins()
    {
        // The only ceiling-satisfying row is KNOWN-unavailable. Availability is filtered FIRST on purpose: a dead row
        // that happens to be cheap is still a NoModelStop, so anti-strand outranks cost.
        var pool = new List<Row>
        {
            new("reachable-frontier", IsDefault: false, Tier: ModelCapabilityTier.Frontier, ProbedTier: null, Available: true, Id: 1),
            new("dead-strong", IsDefault: false, Tier: ModelCapabilityTier.Strong, ProbedTier: null, Available: false, Id: 2),
        };

        Pick(pool, ModelCapabilityTier.Strong)!.ModelId
            .ShouldBe("reachable-frontier", "a known-dead cheap row must not be preferred over a reachable pricier one — availability bounds the ceiling, not the other way round");
    }

    [Fact]
    public void The_availability_soft_filter_still_applies_WITHIN_the_ceiling()
    {
        var pool = new List<Row>
        {
            new("dead-strong", IsDefault: false, Tier: ModelCapabilityTier.Strong, ProbedTier: null, Available: false, Id: 1),
            new("reachable-basic", IsDefault: false, Tier: ModelCapabilityTier.Basic, ProbedTier: null, Available: true, Id: 2),
        };

        Pick(pool, ModelCapabilityTier.Strong)!.ModelId
            .ShouldBe("reachable-basic", "both rows satisfy the ceiling, so the pre-existing availability preference decides — the ceiling adds a bound, it never removes one");
    }

    [Fact]
    public void The_cheap_brain_ceiling_constant_is_pinned_at_Strong()
    {
        // The ONE value three production callers (effort classifier, lesson distiller, spec-preview compiler) pass —
        // capability tiering is deliberately NOT one of them: it PRODUCES the tiers this ceiling reads, so a weak verdict
        // there would mis-rank the pool for every later auto pick including the unceilinged supervisor brain's.
        // Lowering this to Basic would route the plane's judgment onto the team's weakest model; raising
        // it to Frontier would silently undo D2 and put every cheap call back on the priciest tier. Hard-pin the value
        // so either move is a deliberate, review-visible decision rather than an invisible edit.
        InProcessStructuredModel.CheapBrainCeiling.ShouldBe(ModelCapabilityTier.Strong);
    }

    /// <summary>
    /// The unpinned brain-plane pick, composed exactly as <c>ModelPoolSelector.SelectAsync</c> composes it: the
    /// <c>Available</c> soft-filter (anti-strand: kept whole when every row is known-dead), THEN the ceiling, THEN the
    /// IsDefault → effective-tier-descending → model-id → row-id ladder.
    ///
    /// <para>CAVEAT (Rule 12.5): only <see cref="ModelTierCeiling.Apply"/> here is production code — the soft-filter and
    /// the ladder around it are a MIRROR of <c>ModelPoolSelector.SelectAsync</c>'s unpinned path, with no drift detector,
    /// because that path needs a live <c>DbContext</c> and cannot be driven at this tier. So these cases pin the POLICY
    /// (and the order the two steps compose in); the PRODUCTION order is pinned only at the integration tier, by
    /// <c>ModelPoolSelectorFlowTests</c>'s ceiling cases against real Postgres. If the selector's ladder changes, this
    /// helper must be updated with it — the integration tests, not this file, are what would catch the divergence.</para>
    /// </summary>
    private static Row? Pick(List<Row> candidates, ModelCapabilityTier? ceiling)
    {
        var reachable = candidates.Where(c => c.Available != false).ToList();
        var pool = reachable.Count > 0 ? reachable : candidates;

        pool = ModelTierCeiling.Apply(pool, ceiling, r => r.IsDefault, r => r.ProbedTier, r => r.Tier);

        return pool
            .OrderByDescending(r => r.IsDefault)
            .ThenByDescending(r => (int)AgentPlaneModelRanking.Effective(r.ProbedTier, r.Tier))
            .ThenBy(r => r.ModelId, StringComparer.Ordinal)
            .ThenBy(r => r.Id)
            .FirstOrDefault();
    }
}
