using CodeSpace.Core.Services.Agents;
using Shouldly;

namespace CodeSpace.UnitTests.Agents;

/// <summary>
/// 🟢 Unit: the one predicate every side effect in an agent's completion path consults before firing.
///
/// <para>It is pinned separately because the rule existed twice before this and the two copies disagreed about
/// WHERE it applied — the branch push asked it, the delivery-ledger row asserting that the push had happened did
/// not. A rule stated in two places drifts on its own coverage, which is how a zombie worker came to be denied the
/// reversible remote effect and allowed the permanent claim about it.</para>
/// </summary>
[Trait("Category", "Unit")]
public class AgentRunFenceTests
{
    [Theory]
    [InlineData(3, 3, true)]    // the attempt still holds the run
    [InlineData(4, 3, false)]   // a reconciler reclaimed it — the claimed epoch is stale
    [InlineData(9, 3, false)]   // several reclaims later; still not ours
    public void An_attempt_owns_its_run_only_while_the_epoch_it_claimed_is_still_current(long current, long claimed, bool owns)
    {
        AgentRunFence.StillOwns(current, claimed).ShouldBe(owns);
    }

    [Fact]
    public void An_epoch_from_the_future_is_not_ownership_either()
    {
        // Impossible by construction (an epoch only ever advances), so it fails CLOSED rather than reading as a
        // match: a caller holding an epoch the run never reached has no business firing an effect for it.
        AgentRunFence.StillOwns(currentEpoch: 2, claimedEpoch: 5).ShouldBeFalse();
    }

    [Fact]
    public void The_refusal_note_names_the_effect_and_both_epochs()
    {
        // One sentence wherever a zombie is stopped: an operator reading logs should recognise the same refusal
        // whether it came from the push, the manifest, or whatever fires next — and be told which effect it was.
        var note = AgentRunFence.RefusalNote("branch push", currentEpoch: 4, claimedEpoch: 3);

        note.ShouldContain("branch push");
        note.ShouldContain("4");
        note.ShouldContain("3");
        note.ShouldContain("reclaimed", Case.Insensitive);
    }
}
