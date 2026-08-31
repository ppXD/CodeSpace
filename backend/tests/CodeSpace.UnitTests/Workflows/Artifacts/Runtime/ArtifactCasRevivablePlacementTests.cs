using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Workflows.Artifacts.Runtime;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows.Artifacts.Runtime;

/// <summary>
/// Which placement a verified readback may be written onto, answered for every state the enum has.
///
/// <para>The table is exhaustive on purpose, because the one dangerous way to write this rule is by negation. "Any
/// state that is not Available" reads identically on the three states that belong here and silently admits
/// <c>Deleting</c> — a purge's own claim, taken over bytes it is about to remove — whose fence a writer arriving
/// after the claim reads as untouched.</para>
///
/// <para>One list, two readers: the same states also decide whether a producer re-presenting this content is refused
/// by the intent ledger or allowed to re-drive its committed intent through a fresh transfer. Pinning the list here
/// pins both, which is why every state gets a row rather than only the interesting ones.</para>
/// </summary>
[Trait("Category", "Unit")]
public sealed class ArtifactCasRevivablePlacementTests
{
    private const long FencedRevision = 7;

    /// <summary>Every location state, crossed with a fence the row has not moved past and one it has.</summary>
    public static TheoryData<ArtifactLocationState, bool, bool> Placements => new()
    {
        { ArtifactLocationState.Pending, true, false },
        { ArtifactLocationState.Pending, false, false },
        { ArtifactLocationState.Available, true, false },
        { ArtifactLocationState.Available, false, false },
        { ArtifactLocationState.Missing, true, true },
        { ArtifactLocationState.Missing, false, false },
        { ArtifactLocationState.Corrupt, true, true },
        { ArtifactLocationState.Corrupt, false, false },
        { ArtifactLocationState.Deleting, true, false },
        { ArtifactLocationState.Deleting, false, false },
        { ArtifactLocationState.Deleted, true, false },
        { ArtifactLocationState.Deleted, false, false },
        { ArtifactLocationState.Failed, true, false },
        { ArtifactLocationState.Failed, false, false },
        { ArtifactLocationState.Purged, true, true },
        { ArtifactLocationState.Purged, false, false },
    };

    [Theory]
    [MemberData(nameof(Placements))]
    public void Only_a_lost_placement_behind_an_untouched_fence_may_take_a_verified_readback(ArtifactLocationState state, bool fenceUntouched, bool revivable)
    {
        var location = new ArtifactLocation { State = state, Revision = FencedRevision };
        var fence = new ArtifactCasRuntimeCoordinator.LocationFence(state, fenceUntouched ? FencedRevision : FencedRevision - 1);

        ArtifactCasRuntimeCoordinator.Revivable(location, fence).ShouldBe(revivable);
    }

    [Fact]
    public void Every_placement_state_is_answered_one_way_or_the_other()
    {
        // A state added to the enum without a row here would be decided by whatever the predicate happens to do with
        // it, which is exactly how Deleting gets admitted by accident.
        Placements.Select(row => (ArtifactLocationState)row[0]!).Distinct()
            .ShouldBe(Enum.GetValues<ArtifactLocationState>(), ignoreOrder: true);
        Repairs.Select(row => (ArtifactLocationState)row[0]!).Distinct()
            .ShouldBe(Enum.GetValues<ArtifactLocationState>(), ignoreOrder: true);
    }

    /// <summary>Every location state, against whether its record says the destination is holding something else.</summary>
    public static TheoryData<ArtifactLocationState, bool> Repairs => new()
    {
        { ArtifactLocationState.Pending, false },
        { ArtifactLocationState.Available, false },
        { ArtifactLocationState.Missing, false },
        { ArtifactLocationState.Corrupt, true },
        { ArtifactLocationState.Deleting, false },
        { ArtifactLocationState.Deleted, false },
        { ArtifactLocationState.Failed, false },
        { ArtifactLocationState.Purged, false },
    };

    /// <summary>
    /// Which repair a revival of each state is allowed to perform. Passing the whitelist above answers only whether a
    /// placement may be put back at all; this answers HOW, and the two are genuinely different questions.
    ///
    /// <para><c>Corrupt</c> is the one state recorded against a destination caught serving another object, and only
    /// an overwrite repairs that — the impostor answers a HEAD, so a create-only revival skips its upload and then
    /// fails its own readback on the very bytes it declined to replace, forever. <c>Missing</c> and <c>Purged</c>
    /// record the opposite, so an object found at their key is a surprise to be refused rather than destroyed, and
    /// every state that may not be revived at all is here to keep this exhaustive rather than a negation.</para>
    /// </summary>
    [Theory]
    [MemberData(nameof(Repairs))]
    public void Only_a_placement_recorded_against_a_foreign_object_may_be_repaired_by_overwriting_it(ArtifactLocationState state, bool overwrites)
    {
        ArtifactCasRuntimeCoordinator.HoldsAnotherObject(state).ShouldBe(overwrites);
    }

    [Fact]
    public void A_placement_that_did_not_exist_when_this_attempt_read_the_fence_may_never_take_the_readback()
    {
        // No fence means no row was there before the upload, so somebody else created this one and nothing this
        // attempt observed says anything about it.
        var location = new ArtifactLocation { State = ArtifactLocationState.Purged, Revision = FencedRevision };

        ArtifactCasRuntimeCoordinator.Revivable(location, fence: null).ShouldBeFalse();
    }
}
