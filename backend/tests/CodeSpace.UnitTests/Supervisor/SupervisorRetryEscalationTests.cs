using CodeSpace.Core.Services.Supervisor;
using CodeSpace.Messages.Enums;
using Shouldly;

namespace CodeSpace.UnitTests.Supervisor;

/// <summary>
/// A2 (P4-2) — <see cref="SupervisorRetryEscalation"/>, pure over the trigger evidence + candidate pool. Two
/// independent surfaces: WHY to escalate (<see cref="SupervisorRetryEscalation.EscalationReason"/>) and WHICH model
/// to escalate to (<see cref="SupervisorRetryEscalation.PickStrongerModel{T}"/>).
/// </summary>
[Trait("Category", "Unit")]
public class SupervisorRetryEscalationTests
{
    // ─── EscalationReason ──────────────────────────────────────────────────────

    [Fact]
    public void A_contradiction_always_escalates_regardless_of_no_progress_state() =>
        SupervisorRetryEscalation.EscalationReason("over_claim", noProgressDecisions: 0, maxNoProgressDecisions: 8).ShouldNotBeNull();

    [Theory]
    [InlineData("over_claim")]
    [InlineData("under_claim")]
    public void The_reason_names_the_contradiction_label(string label) =>
        SupervisorRetryEscalation.EscalationReason(label, 0, 8)!.ShouldContain(label);

    [Theory]
    [InlineData(6, 8, false)]   // 2 away from the cap — not yet proximate
    [InlineData(7, 8, true)]    // 1 away — the next no-progress decision force-stops
    [InlineData(8, 8, true)]    // already at the cap (shouldn't normally be reachable — a force-stop fires first — but never crashes)
    [InlineData(0, 1, true)]    // a tight cap of 1 — any no-progress is "one away"
    public void No_progress_proximity_escalates_one_away_from_the_cap(int noProgress, int max, bool shouldEscalate) =>
        (SupervisorRetryEscalation.EscalationReason(null, noProgress, max) is not null).ShouldBe(shouldEscalate);

    [Fact]
    public void No_contradiction_and_not_proximate_never_escalates() =>
        SupervisorRetryEscalation.EscalationReason(null, noProgressDecisions: 2, maxNoProgressDecisions: 8).ShouldBeNull();

    // ─── PickStrongerModel ─────────────────────────────────────────────────────

    private sealed record Row(string ModelId, bool IsDefault, ModelCapabilityTier? ProbedTier, ModelCapabilityTier? DeclaredTier);

    private static Row? Pick(IEnumerable<Row> pool, string? priorModelName) =>
        SupervisorRetryEscalation.PickStrongerModel(pool, r => r.IsDefault, r => r.ProbedTier, r => r.DeclaredTier, r => r.ModelId, priorModelName);

    [Fact]
    public void Picks_the_strongest_candidate_above_the_prior_models_tier()
    {
        var pool = new[]
        {
            new Row("weak", false, ModelCapabilityTier.Basic, null),
            new Row("mid", false, ModelCapabilityTier.Strong, null),
            new Row("top", false, ModelCapabilityTier.Frontier, null),
        };

        Pick(pool, "weak")!.ModelId.ShouldBe("top", "escalation reaches for Frontier — unlike the ordinary auto-pick, it is never soft-excluded");
    }

    [Fact]
    public void Reaches_frontier_even_though_the_ordinary_ranking_would_avoid_it()
    {
        // The defining behavior: AgentPlaneModelRanking.Rank would soft-exclude Frontier by default. Escalation must not.
        var pool = new[] { new Row("basic", false, ModelCapabilityTier.Basic, null), new Row("frontier", false, ModelCapabilityTier.Frontier, null) };

        Pick(pool, "basic")!.ModelId.ShouldBe("frontier");
    }

    [Fact]
    public void IsDefault_wins_first_among_qualifying_candidates_even_over_a_higher_tier()
    {
        var pool = new[]
        {
            new Row("strong-default", true, ModelCapabilityTier.Strong, null),
            new Row("frontier", false, ModelCapabilityTier.Frontier, null),
        };

        Pick(pool, "basic")!.ModelId.ShouldBe("strong-default", "IsDefault outranks a HIGHER tier among qualifying candidates — the SAME precedence AgentPlaneModelRanking.Rank gives an unpinned auto-pick");
    }

    [Fact]
    public void Nothing_beats_an_already_frontier_prior_model()
    {
        var pool = new[] { new Row("frontier-a", false, ModelCapabilityTier.Frontier, null), new Row("frontier-b", false, ModelCapabilityTier.Frontier, null) };

        Pick(pool, "frontier-a").ShouldBeNull("no candidate's effective tier beats another Frontier row's — escalation has nowhere higher to go");
    }

    [Fact]
    public void An_unrecognized_prior_model_name_floors_at_unknown_so_any_tiered_candidate_qualifies()
    {
        var pool = new[] { new Row("untiered", false, null, null), new Row("basic", false, ModelCapabilityTier.Basic, null) };

        Pick(pool, "some-model-no-longer-in-the-pool")!.ModelId.ShouldBe("basic", "the prior model isn't in the pool — its floor is Unknown, so any tiered candidate qualifies");
    }

    [Fact]
    public void A_null_prior_model_name_also_floors_at_unknown() =>
        Pick(new[] { new Row("basic", false, ModelCapabilityTier.Basic, null) }, null)!.ModelId.ShouldBe("basic");

    [Fact]
    public void An_untiered_pool_never_qualifies_against_an_unknown_floor() =>
        Pick(new[] { new Row("untiered-a", false, null, null), new Row("untiered-b", true, null, null) }, null).ShouldBeNull("Unknown is not > Unknown — an all-untiered pool never escalates");

    [Fact]
    public void The_probed_tier_wins_over_the_declared_tier_for_the_prior_models_floor()
    {
        // The prior model is declared Frontier (brain-inferred) but objectively PROBED only Basic — Effective() must
        // use the probed tier, so a Strong candidate still qualifies (it beats the TRUE floor, Basic).
        var pool = new[]
        {
            new Row("prior", false, ModelCapabilityTier.Basic, ModelCapabilityTier.Frontier),
            new Row("candidate", false, ModelCapabilityTier.Strong, null),
        };

        Pick(pool, "prior")!.ModelId.ShouldBe("candidate");
    }

    [Fact]
    public void A_model_credentialed_twice_uses_the_highest_tier_row_for_its_floor()
    {
        // "prior" appears on two credentials with different tiers — the floor must be the row with the HIGHEST
        // effective tier (the model IS that capable, whichever credential ran it), never an arbitrary/first match.
        var pool = new[]
        {
            new Row("prior", false, ModelCapabilityTier.Basic, null),
            new Row("prior", false, ModelCapabilityTier.Strong, null),
            new Row("candidate", false, ModelCapabilityTier.Frontier, null),
        };

        Pick(pool, "prior")!.ModelId.ShouldBe("candidate", "the floor is the HIGHER of the two 'prior' rows (Strong) — a Frontier candidate still clears it");
    }

    [Fact]
    public void Tie_break_is_deterministic_by_model_id_when_tier_and_isdefault_both_tie()
    {
        var pool = new[] { new Row("zzz", false, ModelCapabilityTier.Frontier, null), new Row("aaa", false, ModelCapabilityTier.Frontier, null) };

        Pick(pool, "basic").ShouldBe(Pick(pool, "basic"), "repeated calls over the identical pool must be byte-identical — pure + deterministic");
        Pick(pool, null)!.ModelId.ShouldBe("aaa");
    }
}
