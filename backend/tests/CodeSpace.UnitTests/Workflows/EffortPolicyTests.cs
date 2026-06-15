using CodeSpace.Messages.Tasks.Effort;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

/// <summary>
/// Pins the PURE, DATA-DRIVEN effort policy — the ordered rule table that maps generic <see cref="EffortSignals"/>
/// to a tier. Every ORDERED ROW is pinned by an explicit theory case (risky / high-cost → deep; code + cross-file
/// or code + tests → standard; code-only / empty → quick catch-all), the non-auto operator short-circuit is
/// pinned, and a property-style totality check proves NO signal combination throws / returns blank — the
/// <c>_ => true</c> catch-all makes the table total. There is NO task-type in any of this: the rules are over
/// generic signal axes only.
/// </summary>
[Trait("Category", "Unit")]
public class EffortPolicyTests
{
    [Theory]
    // Row 1 — risky side effects OR a high cost estimate ⇒ deep (the most cautious tier, first match).
    [InlineData(false, false, false, true, "low", TaskEffortModes.Deep)]    // risky side effects alone
    [InlineData(true, true, true, false, "high", TaskEffortModes.Deep)]     // high cost estimate alone (code+crossfile+tests would otherwise be standard)
    // Row 2 — code change that fans across files OR needs tests/CI ⇒ standard.
    [InlineData(true, true, false, false, "low", TaskEffortModes.Standard)] // code + cross-file
    [InlineData(true, false, true, false, "low", TaskEffortModes.Standard)] // code + tests/CI
    // Row 3 (catch-all) — a localized code-only edit, or no signals at all ⇒ quick.
    [InlineData(true, false, false, false, "low", TaskEffortModes.Quick)]   // code-only, single file, no tests
    [InlineData(false, false, false, false, "low", TaskEffortModes.Quick)]  // empty seed — the catch-all
    [InlineData(false, true, true, false, "medium", TaskEffortModes.Quick)] // cross-file + tests but NO code change ⇒ still quick (row 2 needs NeedsCodeChange)
    public void Decide_maps_each_ordered_row(bool code, bool crossFile, bool tests, bool risky, string costTier, string expected)
    {
        var signals = new EffortSignals
        {
            NeedsCodeChange = code,
            CrossFile = crossFile,
            NeedsTestsOrCi = tests,
            RiskySideEffects = risky,
            EstimatedCostTier = costTier,
        };

        EffortPolicy.Decide(signals, requestedEffort: null).ShouldBe(expected);
    }

    [Theory]
    [InlineData(TaskEffortModes.Deep)]
    [InlineData(TaskEffortModes.Quick)]
    [InlineData(TaskEffortModes.Standard)]
    [InlineData("some-custom-tier")]   // an open string an operator chose — honoured verbatim
    public void Decide_short_circuits_an_explicit_non_auto_operator_tier(string requested)
    {
        // Empty signals would normally classify to quick; the explicit operator tier wins regardless.
        EffortPolicy.Decide(new EffortSignals(), requestedEffort: requested).ShouldBe(requested);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(TaskEffortModes.Auto)]
    [InlineData("AUTO")]   // the auto sentinel is case-insensitive
    public void Decide_classifies_when_no_explicit_tier_is_requested(string? requested)
    {
        // No explicit tier ⇒ the table runs. These risky signals classify to deep.
        var risky = new EffortSignals { RiskySideEffects = true };

        EffortPolicy.Decide(risky, requestedEffort: requested).ShouldBe(TaskEffortModes.Deep);
    }

    [Fact]
    public void Decide_is_total_over_every_signal_combination()
    {
        var tiers = new[] { "low", "medium", "high", "unknown-tier" };
        var valid = new[] { TaskEffortModes.Quick, TaskEffortModes.Standard, TaskEffortModes.Deep };

        foreach (var code in Bools)
        foreach (var crossFile in Bools)
        foreach (var tests in Bools)
        foreach (var ambiguous in Bools)
        foreach (var risky in Bools)
        foreach (var tier in tiers)
        {
            var signals = new EffortSignals
            {
                NeedsCodeChange = code,
                CrossFile = crossFile,
                NeedsTestsOrCi = tests,
                Ambiguous = ambiguous,
                RiskySideEffects = risky,
                EstimatedCostTier = tier,
            };

            var mode = EffortPolicy.Decide(signals, requestedEffort: null);

            mode.ShouldBeOneOf(valid);   // never null / blank / throws — the catch-all guarantees a tier
        }
    }

    [Fact]
    public void ConfirmConfidenceFloor_is_pinned()
    {
        // Pinned literal: the heuristic caps its confidence below this, and the router shows a confirm card below it.
        EffortPolicy.ConfirmConfidenceFloor.ShouldBe(0.6);
    }

    private static readonly bool[] Bools = { false, true };
}
