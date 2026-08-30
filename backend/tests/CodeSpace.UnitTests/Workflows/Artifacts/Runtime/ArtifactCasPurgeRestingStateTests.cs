using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Workflows.Artifacts.Runtime;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows.Artifacts.Runtime;

/// <summary>
/// The rule that decides where a released purge claim puts the placement back.
///
/// <para><c>Deleting</c> is the claim marker itself, so no number of them says anything about where the row rests.
/// A claim taken from an orphan another worker left behind would otherwise be released into the marker it was found
/// in, and a row released to a marker can never leave it.</para>
/// </summary>
[Trait("Category", "Unit")]
public sealed class ArtifactCasPurgeRestingStateTests
{
    public static TheoryData<ArtifactLocationState[], ArtifactLocationState?> Histories => new()
    {
        { [ArtifactLocationState.Available], ArtifactLocationState.Available },
        { [ArtifactLocationState.Available, ArtifactLocationState.Deleting], ArtifactLocationState.Available },
        { [ArtifactLocationState.Available, ArtifactLocationState.Missing, ArtifactLocationState.Deleting, ArtifactLocationState.Deleting, ArtifactLocationState.Deleting], ArtifactLocationState.Missing },
        { [ArtifactLocationState.Available, ArtifactLocationState.Corrupt, ArtifactLocationState.Deleting], ArtifactLocationState.Corrupt },
        { [ArtifactLocationState.Missing, ArtifactLocationState.Available, ArtifactLocationState.Deleting], ArtifactLocationState.Available },
        { [ArtifactLocationState.Deleting], null },
        { [ArtifactLocationState.Deleting, ArtifactLocationState.Deleting], null },
    };

    [Theory]
    [MemberData(nameof(Histories))]
    public void The_resting_state_skips_every_deleting_event_and_takes_the_newest_state_below_them(ArtifactLocationState[] states, ArtifactLocationState? expected)
    {
        // Fed in a fixed scramble that is neither oldest-first nor newest-first, so the answer can only come from the
        // recorded revision. Newest-first is the ONE feed order under which a missing sort still returns the right
        // answer for every case here, which would make this theory pass over exactly the defect it exists to catch.
        var history = states.Select((state, index) => new ArtifactLocationEvent { Revision = index + 1, State = state })
            .OrderBy(entry => entry.Revision % 3).ThenBy(entry => entry.Revision).AsQueryable();

        ArtifactCasRuntimeCoordinator.RestingStates(history).FirstOrDefault().ShouldBe(expected);
    }
}
