using CodeSpace.Core.Services.Review;
using CodeSpace.Messages.Review;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

/// <summary>
/// Convergence detection (P1b-2): a stable text fingerprint recognises "the same issue" across revise rounds despite a
/// weak model's trivial re-wording, so an oscillating loop (re-raising an unmoved problem) is told apart from a
/// converging one (each pass resolves something). The executor's early-stop reads <see cref="CriticConvergence.SameSignal"/>;
/// the supervisor escalation reads <see cref="CriticConvergence.Assess"/> to name what persisted.
/// </summary>
[Trait("Category", "Unit")]
public class CriticConvergenceTests
{
    private static CriticIssue Issue(string text, CriticSeverity severity = CriticSeverity.Blocker) => new() { Text = text, Evidence = "e", Severity = severity };

    [Theory]
    // The fingerprint is robust to case, spacing, and NOISE punctuation — the trivial re-wording a weak model introduces.
    [InlineData("Missing a rollback plan.", "missing a rollback plan", true)]
    [InlineData("Missing a rollback plan!", "  MISSING   a  rollback   plan  ", true)]
    [InlineData("The plan has no rollback step.", "the plan has no rollback step", true)]
    [InlineData("Missing a rollback plan", "Missing a ROLL-BACK plan", false)]   // rollback vs roll-back is a real text difference (conservative — a false negative just spends the budget)
    [InlineData("no tests", "no security", false)]
    // …but RELATIONAL operators are preserved, so OPPOSITE conditions never collapse to the same fingerprint.
    [InlineData("the guard uses x >= 0", "the guard uses x >= 0 !", true)]       // a stray '!' is noise
    [InlineData("the guard uses x >= 0", "the guard uses x <= 0", false)]        // >= vs <= is a real, opposite condition — must stay distinct
    [InlineData("len > 8", "len >= 8", false)]
    public void The_fingerprint_normalises_noise_but_keeps_relational_operators(string a, string b, bool sameFingerprint) =>
        (CriticConvergence.Fingerprint(a) == CriticConvergence.Fingerprint(b)).ShouldBe(sameFingerprint);

    [Fact]
    public void A_blank_signal_is_never_a_stall()
    {
        CriticConvergence.SameSignal(null, null).ShouldBeFalse();
        CriticConvergence.SameSignal("", "   ").ShouldBeFalse();
        CriticConvergence.SameSignal("x", "").ShouldBeFalse("a missing current signal never matches — fail toward continuing");
    }

    [Fact]
    public void Same_signal_detects_an_identical_reason_across_rounds() =>
        CriticConvergence.SameSignal(
            "An independent reviewer flagged the change: the change still carries a placeholder hack",
            "An independent reviewer flagged the change:  The change still carries a PLACEHOLDER hack!")
            .ShouldBeTrue("the same critic feedback re-worded trivially is the same unaddressed problem");

    [Fact]
    public void Assess_partitions_resolved_persisting_and_introduced()
    {
        var prior = new[] { Issue("no rollback plan"), Issue("no tests") };
        var current = new[] { Issue("no tests"), Issue("hardcoded secret") };   // rollback resolved, tests persist, secret new

        var report = CriticConvergence.Assess(prior, current);

        report.Resolved.ShouldHaveSingleItem().Text.ShouldBe("no rollback plan");
        report.Persisting.ShouldHaveSingleItem().Text.ShouldBe("no tests");
        report.Introduced.ShouldHaveSingleItem().Text.ShouldBe("hardcoded secret");
    }

    [Fact]
    public void An_identical_issue_set_persists_entirely_with_nothing_resolved_or_introduced()
    {
        var same = new[] { Issue("no rollback plan"), Issue("no tests") };
        var report = CriticConvergence.Assess(same, same);

        report.Resolved.ShouldBeEmpty("nothing was resolved — the revision moved nothing");
        report.Persisting.Count.ShouldBe(2, "both issues survived unchanged");
        report.Introduced.ShouldBeEmpty();
    }

    [Fact]
    public void A_self_repeating_model_is_not_listed_twice()
    {
        var report = CriticConvergence.Assess(
            new[] { Issue("no tests") },
            new[] { Issue("no tests"), Issue("no tests.") });   // the model repeated the same finding

        report.Persisting.ShouldHaveSingleItem().Text.ShouldBe("no tests", "one issue per distinct fingerprint — the human card never lists a duplicate");
    }

    [Fact]
    public void Persisting_carries_the_current_rounds_wording() =>
        CriticConvergence.Assess(new[] { Issue("missing a rollback plan") }, new[] { Issue("Missing a rollback plan.") })
            .Persisting.ShouldHaveSingleItem().Text.ShouldBe("Missing a rollback plan.", "the same issue by fingerprint carries the latest round's instance");
}
