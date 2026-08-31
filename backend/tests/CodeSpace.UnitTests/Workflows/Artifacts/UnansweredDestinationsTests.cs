using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Workflows.Artifacts.Runtime;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows.Artifacts;

/// <summary>
/// The one thing a verification pass is allowed to remember about a destination, and the one thing it is not.
///
/// <para>A destination that could not answer for ITSELF cannot answer for any of its rows, and re-asking spends the
/// whole hourly batch on it at a round trip per row while every healthy destination goes unchecked. So that NEGATIVE
/// verdict is remembered for the length of one pass, and the rows behind it are dropped rather than asked. Which rows
/// a given verdict may be remembered for is the verifier's decision and is asserted where it is made — an answer about
/// one OBJECT never reaches here, because a refused key says nothing about a key the destination serves.</para>
///
/// <para>The opposite direction is never safe and is deliberately not expressible here: a destination that DID answer
/// is what licenses a demotion, and a mount that disappears mid-pass has to be able to stop demoting immediately. That
/// corroboration therefore stays a per-row local in the verifier, and the behavioural proof that it does lives in
/// <c>ArtifactLocationVerifierDestinationFairnessTests</c>.</para>
/// </summary>
[Trait("Category", "Unit")]
public sealed class UnansweredDestinationsTests
{
    [Fact]
    public void A_destination_that_did_not_answer_holds_every_row_behind_it()
    {
        var teamId = Guid.NewGuid();
        var revisionId = Guid.NewGuid();
        var asked = Placement(teamId, revisionId);
        var behind = Placement(teamId, revisionId);
        var unanswered = new UnansweredDestinations();

        unanswered.Contains(behind).ShouldBeFalse("nothing may be dropped before a destination has failed to answer, or the drop is not this pass's doing");

        unanswered.Add(asked);

        unanswered.Contains(behind).ShouldBeTrue("one refusal answers for every row at the same destination — asking each of them separately is what lets a single dead bucket consume the entire batch");
    }

    [Fact]
    public void A_destination_that_did_not_answer_says_nothing_about_a_different_one()
    {
        // The whole point of dropping rows is to spend the batch on destinations that CAN answer, so over-reaching by
        // one pin would reintroduce the fault it fixes, pointed the other way: healthy placements dropped unchecked
        // because an unrelated bucket was down.
        var teamId = Guid.NewGuid();
        var unanswered = new UnansweredDestinations();
        var dead = Placement(teamId, Guid.NewGuid());
        var healthy = Placement(teamId, Guid.NewGuid());

        unanswered.Add(dead);

        unanswered.Contains(healthy).ShouldBeFalse("a destination is the profile revision a placement was written under, and a different one is a different bucket that has said nothing at all");
    }

    [Fact]
    public void A_second_team_on_one_profile_revision_id_is_still_a_different_destination()
    {
        // The pin is the team AND the revision, because the broker resolves both: a revision is read within its team,
        // so an id alone does not name a destination anyone can open.
        var revisionId = Guid.NewGuid();
        var unanswered = new UnansweredDestinations();
        var dead = Placement(Guid.NewGuid(), revisionId);
        var other = Placement(Guid.NewGuid(), revisionId);

        unanswered.Add(dead);

        unanswered.Contains(other).ShouldBeFalse("dropping another team's rows on the strength of a bare revision id would leave them unchecked for a destination that was never asked");
    }

    private static ArtifactLocation Placement(Guid teamId, Guid revisionId) => new() { TeamId = teamId, StorageProfileRevisionId = revisionId };
}
