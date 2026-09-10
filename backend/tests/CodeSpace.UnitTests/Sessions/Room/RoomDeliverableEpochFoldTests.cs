using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Sessions.Room;
using CodeSpace.Messages.Agents;
using Shouldly;

namespace CodeSpace.UnitTests.Sessions.Room;

/// <summary>
/// The Room's belt-and-braces fold over produced-file manifests (<see cref="RoomProjector.CurrentDeliverableManifests"/>):
/// current rows only, folded to the highest <c>FenceEpoch</c> per (<c>AgentRunId</c>, <c>LogicalPath</c>). A
/// reclaimed re-attach's fresh capture already supersedes the epoch it replaced at the store
/// (<see cref="Core.Services.Agents.Publish.ArtifactManifestStore"/>) — this fold exists only so the Room stays
/// correct even against a row a pre-fix capture left dangling as a second "current" copy at a lower epoch.
///
/// <para>Tier: Unit — pure over in-memory <see cref="ArtifactManifest"/> rows.</para>
/// </summary>
[Trait("Category", "Unit")]
public class RoomDeliverableEpochFoldTests
{
    private static readonly Guid AgentRunId = Guid.NewGuid();

    [Theory]
    [InlineData(1, 2)]
    [InlineData(2, 1)]
    public void Two_current_rows_at_different_epochs_for_the_same_identity_fold_to_the_higher_one(long firstEpoch, long secondEpoch)
    {
        var manifests = new[] { Manifest(firstEpoch, "report.md"), Manifest(secondEpoch, "report.md") };

        var current = RoomProjector.CurrentDeliverableManifests(manifests).ShouldHaveSingleItem();

        current.FenceEpoch.ShouldBe(Math.Max(firstEpoch, secondEpoch), "the reader must never show the stale epoch's row alongside the newer attempt's");
    }

    [Fact]
    public void Distinct_paths_at_the_same_epoch_both_survive_the_fold()
    {
        var manifests = new[] { Manifest(1, "a.md"), Manifest(1, "b.md") };

        RoomProjector.CurrentDeliverableManifests(manifests).Select(m => m.LogicalPath).ShouldBe(new[] { "a.md", "b.md" }, ignoreOrder: true);
    }

    [Fact]
    public void A_superseded_row_is_excluded_regardless_of_its_epoch()
    {
        var superseded = Manifest(1, "report.md");
        superseded.SupersededByManifestId = Guid.NewGuid();
        var current = Manifest(2, "report.md");

        RoomProjector.CurrentDeliverableManifests(new[] { superseded, current }).ShouldHaveSingleItem().Id.ShouldBe(current.Id);
    }

    private static ArtifactManifest Manifest(long fenceEpoch, string path) => new()
    {
        Id = Guid.NewGuid(), TeamId = Guid.NewGuid(), AgentRunId = AgentRunId, FenceEpoch = fenceEpoch,
        Kind = ArtifactManifestKind.Document, LogicalPath = path, ContentArtifactId = Guid.NewGuid(),
        Sha256 = "sha", SizeBytes = 1, ContentType = "text/markdown",
    };
}
