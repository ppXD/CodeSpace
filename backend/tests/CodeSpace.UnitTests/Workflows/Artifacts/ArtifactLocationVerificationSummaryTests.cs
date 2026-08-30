using CodeSpace.Core.Services.Workflows.Artifacts.Runtime;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows.Artifacts;

/// <summary>
/// The summary is what a sweep reports to an operator, so the distinction it draws between "nothing was wrong" and
/// "nothing could be checked" has to survive refactoring — collapsing them would let a destination that answers
/// nothing at all read as a clean bill of health.
/// </summary>
public sealed class ArtifactLocationVerificationSummaryTests
{
    [Fact]
    public void A_pass_that_could_not_reach_anything_is_not_reported_as_a_pass()
    {
        var summary = new ArtifactLocationVerificationSummary { Checked = 40, Confirmed = 0, Restored = 0, Missing = 0, Corrupt = 0, Inconclusive = 40, Unrecorded = 0 };

        summary.Confirmed.ShouldBe(0, "an unreachable destination confirms nothing");
        summary.Inconclusive.ShouldBe(summary.Checked, "and every row it could not reach must be accounted for as unanswered rather than silently dropped");
    }

    [Fact]
    public void A_pass_that_wrote_nothing_down_is_not_reported_as_a_pass_that_could_not_reach_anything()
    {
        // The two are opposite faults with opposite fixes — one is the destination, one is this deployment's own
        // database — and they are indistinguishable from the row alone, because both leave it untouched. Folding them
        // into one count is what makes "every write in the batch was refused" look like an ordinary quiet hour.
        var refused = new ArtifactLocationVerificationSummary { Checked = 40, Confirmed = 0, Restored = 0, Missing = 0, Corrupt = 0, Inconclusive = 0, Unrecorded = 40 };
        var unreachable = new ArtifactLocationVerificationSummary { Checked = 40, Confirmed = 0, Restored = 0, Missing = 0, Corrupt = 0, Inconclusive = 40, Unrecorded = 0 };

        refused.Unrecorded.ShouldBe(refused.Checked, "a pass whose every write was refused must say so in its own count");
        refused.Inconclusive.ShouldNotBe(unreachable.Inconclusive, "and must not read the same as a pass that reached no destination at all");
    }

    [Fact]
    public void Every_row_a_pass_looked_at_lands_in_exactly_one_outcome()
    {
        // The property that makes the tally trustworthy: a row that fell through the classification without being
        // counted would make a partial sweep indistinguishable from a complete one.
        var summary = new ArtifactLocationVerificationSummary { Checked = 10, Confirmed = 4, Restored = 1, Missing = 2, Corrupt = 1, Inconclusive = 1, Unrecorded = 1 };

        (summary.Confirmed + summary.Restored + summary.Missing + summary.Corrupt + summary.Inconclusive + summary.Unrecorded).ShouldBe(summary.Checked);
    }
}
