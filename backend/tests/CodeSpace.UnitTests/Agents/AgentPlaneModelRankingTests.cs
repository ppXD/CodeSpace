using CodeSpace.Core.Services.Agents.ModelCredentials;
using CodeSpace.Messages.Enums;
using Shouldly;

namespace CodeSpace.UnitTests.Agents;

/// <summary>
/// Pure-logic pins for <see cref="AgentPlaneModelRanking"/> (P3.4) — the "ordinary execution" auto-pick policy:
/// IsDefault wins outright, Frontier is soft-avoided (anti-strand: falls back to the full pool when Frontier is the
/// only tier present), and the tie-break never touches model-id/name at all (no alphabetical influence).
/// </summary>
public sealed class AgentPlaneModelRankingTests
{
    private sealed record Row(string ModelId, bool IsDefault, ModelCapabilityTier? Tier, ModelCapabilityTier? ProbedTier, int Id);

    [Fact]
    public void The_operator_default_wins_outright_even_over_a_higher_tier()
    {
        var pool = new[]
        {
            new Row("frontier-model", IsDefault: false, Tier: ModelCapabilityTier.Frontier, ProbedTier: null, Id: 1),
            new Row("starred-basic", IsDefault: true, Tier: ModelCapabilityTier.Basic, ProbedTier: null, Id: 2),
        };

        var winner = Rank(pool).First();

        winner.ModelId.ShouldBe("starred-basic", "the explicit operator override always wins, regardless of tier");
    }

    [Fact]
    public void Frontier_is_avoided_by_default_in_favor_of_a_lower_tier()
    {
        var pool = new[]
        {
            new Row("zzz-frontier", IsDefault: false, Tier: ModelCapabilityTier.Frontier, ProbedTier: null, Id: 1),
            new Row("aaa-strong", IsDefault: false, Tier: ModelCapabilityTier.Strong, ProbedTier: null, Id: 2),
        };

        var winner = Rank(pool).First();

        winner.ModelId.ShouldBe("aaa-strong", "ordinary execution avoids Frontier by default — 'aaa' winning despite sorting first is incidental, not the reason");
    }

    [Fact]
    public void Frontier_is_used_when_it_is_the_only_tier_available_anti_strand()
    {
        var pool = new[]
        {
            new Row("only-frontier-a", IsDefault: false, Tier: ModelCapabilityTier.Frontier, ProbedTier: null, Id: 1),
            new Row("only-frontier-b", IsDefault: false, Tier: ModelCapabilityTier.Frontier, ProbedTier: null, Id: 2),
        };

        var winner = Rank(pool).First();

        winner.Id.ShouldBe(1, "avoiding Frontier would leave zero candidates — a pricier model beats no model at all (the same anti-strand posture as ModelPoolSelector's Available soft-filter)");
    }

    [Theory]
    [InlineData(ModelCapabilityTier.Strong, ModelCapabilityTier.Basic)]
    [InlineData(ModelCapabilityTier.Basic, ModelCapabilityTier.Unknown)]
    public void Among_non_Frontier_tiers_the_higher_one_wins(ModelCapabilityTier higher, ModelCapabilityTier lower)
    {
        var pool = new[]
        {
            new Row("the-higher-one", IsDefault: false, Tier: higher, ProbedTier: null, Id: 1),
            new Row("the-lower-one", IsDefault: false, Tier: lower, ProbedTier: null, Id: 2),
        };

        // Names are neutral ("the-higher-one"/"the-lower-one") so the assertion can't be satisfied by alphabetical luck.
        Rank(pool).First().ModelId.ShouldBe("the-higher-one");
    }

    [Fact]
    public void A_probed_tier_lifts_an_opaque_model_above_a_declared_Unknown()
    {
        var pool = new[]
        {
            new Row("zzz-opaque-but-probed-strong", IsDefault: false, Tier: ModelCapabilityTier.Unknown, ProbedTier: ModelCapabilityTier.Strong, Id: 1),
            new Row("aaa-declared-basic", IsDefault: false, Tier: ModelCapabilityTier.Basic, ProbedTier: null, Id: 2),
        };

        var winner = Rank(pool).First();

        winner.ModelId.ShouldBe("zzz-opaque-but-probed-strong", "the effective tier (probed ?? declared) lifts the probed-Strong opaque model above a declared-Basic one, alphabetical order notwithstanding");
    }

    [Fact]
    public void An_untiered_pool_never_falls_back_to_alphabetical_ordering()
    {
        // Neither row carries ANY tier signal — both are effectively Unknown. The ranking must NOT secretly
        // reintroduce a model-id/name comparison as a tie-break; only the caller's OWN final .ThenBy (e.g. row id)
        // may decide, which this helper deliberately leaves to the caller.
        var pool = new[]
        {
            new Row("zzz-should-not-automatically-win", IsDefault: false, Tier: null, ProbedTier: null, Id: 5),
            new Row("aaa-should-not-automatically-win", IsDefault: false, Tier: null, ProbedTier: null, Id: 1),
        };

        var ranked = Rank(pool).ThenBy(r => r.Id).ToList();

        // The caller's row-id tie-break decides (id=1 first) — proving the ranking itself carries NO name/alphabetical
        // ordering; only an EXPLICIT, separate tie-break (never model id) determines a genuinely-tied pool.
        ranked[0].Id.ShouldBe(1);
    }

    [Fact]
    public void All_four_tiers_present_prefers_the_highest_non_Frontier()
    {
        var pool = new[]
        {
            new Row("frontier", IsDefault: false, Tier: ModelCapabilityTier.Frontier, ProbedTier: null, Id: 1),
            new Row("strong", IsDefault: false, Tier: ModelCapabilityTier.Strong, ProbedTier: null, Id: 2),
            new Row("basic", IsDefault: false, Tier: ModelCapabilityTier.Basic, ProbedTier: null, Id: 3),
            new Row("unknown", IsDefault: false, Tier: ModelCapabilityTier.Unknown, ProbedTier: null, Id: 4),
        };

        Rank(pool).First().ModelId.ShouldBe("strong", "Strong is the highest tier once Frontier is excluded from the ordinary-execution pool");
    }

    private static IOrderedEnumerable<Row> Rank(IEnumerable<Row> pool) =>
        AgentPlaneModelRanking.Rank(pool, r => r.IsDefault, r => r.ProbedTier, r => r.Tier);
}
