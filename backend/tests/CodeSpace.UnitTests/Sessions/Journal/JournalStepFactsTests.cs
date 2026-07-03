using CodeSpace.Core.Services.Sessions.Journal;
using Shouldly;

namespace CodeSpace.UnitTests.Sessions.Journal;

/// <summary>
/// 🟢 Unit: <see cref="JournalStepFacts.Merge"/> — the field-wise coalesce two facts sources compose through. Pins the
/// EXACT orientation so a flip (e.g. <c>Rationale ?? other.Rationale</c> instead of <c>other.Rationale ?? Rationale</c>)
/// fails LOUD: a right-hand SET field wins over the left, a right-hand UNSET field leaves the left intact, and an unset
/// left takes the right. Only a same-field / different-value collision distinguishes the correct merge from its inverse —
/// so it is tested explicitly (without it the merge seam is unfalsifiable, the genericity claim untested).
/// </summary>
[Trait("Category", "Unit")]
public class JournalStepFactsTests
{
    [Fact]
    public void A_right_hand_set_field_wins_over_the_left()
    {
        // The ONLY case that distinguishes the correct orientation from its inverse: both sides set the SAME field to
        // DIFFERENT values. The right operand (the later-merged source) wins — pin it so a flipped Merge can't hide.
        new JournalStepFacts { Rationale = "left" }.Merge(new JournalStepFacts { Rationale = "right" })
            .Rationale.ShouldBe("right", "the right operand (the later source) wins a same-field collision — deterministic last-writer");
    }

    [Fact]
    public void A_right_hand_unset_field_leaves_the_left_intact()
    {
        new JournalStepFacts { Rationale = "left" }.Merge(new JournalStepFacts { Rationale = null })
            .Rationale.ShouldBe("left", "an unset right field does NOT clobber the left — a later empty source never erases an earlier fact");
    }

    [Fact]
    public void An_unset_left_takes_the_right()
    {
        new JournalStepFacts { Rationale = null }.Merge(new JournalStepFacts { Rationale = "right" })
            .Rationale.ShouldBe("right", "the left gap is filled by the right — a source fills a field an earlier one left empty");
    }
}
