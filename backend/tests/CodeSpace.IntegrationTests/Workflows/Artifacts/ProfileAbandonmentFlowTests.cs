using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Workflows.Artifacts.Profiles;
using CodeSpace.Core.Services.Workflows.Artifacts.Profiles.Exceptions;
using CodeSpace.Core.Services.Workflows.Artifacts.Providers.Local;
using CodeSpace.Core.Services.Workflows.Artifacts.Runtime;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Commands.Storage;
using CodeSpace.Messages.Dtos.Storage;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using System.Text.Json;

namespace CodeSpace.IntegrationTests.Workflows.Artifacts;

/// <summary>
/// The operator's way out of a destination that is gone, end to end.
///
/// <para>The coordinator could already settle one placement on proof; without this nobody could reach it. A
/// capability with no caller is not a capability — the records stayed unreachable and the profile stayed
/// un-retirable, which is the dead end this whole arc exists to close.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class ProfileAbandonmentFlowTests : IDisposable
{
    private readonly PostgresFixture _fixture;
    private readonly List<string> _roots = [];

    public ProfileAbandonmentFlowTests(PostgresFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task An_operator_drains_a_vanished_destination_and_can_then_retire_the_profile()
    {
        // The journey the two 409s used to describe and no code implemented: the destination is gone, the records
        // are closed on the destination's own answer, and only then does the irreversible step become available.
        var world = await SeedRoutedProfileAsync(placements: 3);
        Directory.Delete(world.Root, recursive: true);

        var summary = await AbandonAsync(world, batchSize: 50);

        summary.Examined.ShouldBe(3);
        summary.Abandoned.ShouldBe(3);
        summary.StillServed.ShouldBe(0);
        summary.Remaining.ShouldBe(0, "draining to zero must mean draining to what actually unblocks retirement");

        await RetireAsync(world);
        (await StateAsync(world)).ShouldBe(StorageProfileState.Retired);
    }

    [Fact]
    public async Task A_destination_that_still_serves_its_objects_is_not_drained_and_the_profile_stays_blocked()
    {
        // The refusal that makes the operation safe to expose at all. An operator who runs this against a healthy
        // destination gets nothing closed and nothing lost — the answer comes from the destination, not from them.
        var world = await SeedRoutedProfileAsync(placements: 2);

        var summary = await AbandonAsync(world, batchSize: 50);

        summary.StillServed.ShouldBe(2);
        summary.Abandoned.ShouldBe(0);
        summary.Remaining.ShouldBe(2);
        await Should.ThrowAsync<StorageProfileConflictException>(() => RetireAsync(world));
    }

    [Fact]
    public async Task A_pass_is_bounded_and_says_how_much_is_left_so_it_can_be_resumed()
    {
        // Bounded and repeatable rather than one long job: a call that dies halfway leaves everything it did not
        // reach exactly as it was, so resumption is a property of the ledger rather than of a job row.
        var world = await SeedRoutedProfileAsync(placements: 3);
        Directory.Delete(world.Root, recursive: true);

        var first = await AbandonAsync(world, batchSize: 2);

        first.Abandoned.ShouldBe(2);
        first.Remaining.ShouldBe(1);

        var second = await AbandonAsync(world, batchSize: 2);

        second.Abandoned.ShouldBe(1);
        second.Remaining.ShouldBe(0);
    }

    [Fact]
    public async Task An_orphaned_claim_on_a_serving_destination_is_released_to_the_state_it_rests_in()
    {
        // A worker that dies between claiming and deleting leaves the marker on the row. The drain re-claims it, the
        // destination serves the object, and the release has to put the row back where it was BEFORE any claim:
        // writing the marker back records a live object as mid-delete forever, which is the one state it cannot leave.
        var world = await SeedRoutedProfileAsync(placements: 1);
        var orphaned = await OrphanOneClaimAsync(world);

        (await LocationStateAsync(orphaned)).ShouldBe(ArtifactLocationState.Deleting, "a fixture that did not start in the marker would prove nothing");

        var summary = await AbandonAsync(world, batchSize: 50);

        summary.StillServed.ShouldBe(1);
        summary.Abandoned.ShouldBe(0);
        (await LocationStateAsync(orphaned)).ShouldBe(ArtifactLocationState.Available,
            "a serving HEAD is the one thing that may restore Available, and it was taken under this claim");
    }

    [Fact]
    public async Task An_orphaned_claim_on_a_dead_destination_is_closed_rather_than_released()
    {
        // The same orphan on a destination that cannot answer for it. Abandonment is its exit, and it is the only
        // one: the claim was taken from the marker, so no release can establish anything to put back.
        var world = await SeedRoutedProfileAsync(placements: 1);
        var orphaned = await OrphanOneClaimAsync(world);
        Directory.Delete(world.Root, recursive: true);

        (await LocationStateAsync(orphaned)).ShouldBe(ArtifactLocationState.Deleting);

        var summary = await AbandonAsync(world, batchSize: 50);

        summary.Abandoned.ShouldBe(1);
        summary.Remaining.ShouldBe(0);
        (await LocationStateAsync(orphaned)).ShouldBe(ArtifactLocationState.Purged);
    }

    [Fact]
    public async Task A_worker_that_died_mid_purge_does_not_wedge_the_drain_across_repeated_passes()
    {
        // The operator's whole loop: drain, remove what the destination still serves, drain again. The first pass has
        // to hand the orphan back to a state the second pass can move it out of, or the record is stuck at the front
        // of every batch for good and the count it reports never moves.
        var world = await SeedRoutedProfileAsync(placements: 1);
        var orphaned = await OrphanOneClaimAsync(world);

        var first = await AbandonAsync(world, batchSize: 50);

        first.StillServed.ShouldBe(1);
        first.Remaining.ShouldBe(1, "a served placement is still held, and a drain that claimed otherwise would be the unsafe answer");
        (await LocationStateAsync(orphaned)).ShouldBe(ArtifactLocationState.Available);

        Directory.Delete(world.Root, recursive: true);

        var second = await AbandonAsync(world, batchSize: 50);

        second.Remaining.ShouldBeLessThan(first.Remaining);
        (await LocationStateAsync(orphaned)).ShouldBe(ArtifactLocationState.Purged);
    }

    // ─── World ───────────────────────────────────────────────────────────────

    private async Task<ProfileAbandonmentSummary> AbandonAsync(RoutedWorld world, int batchSize)
    {
        using var scope = _fixture.BeginScope();

        return await scope.Resolve<IProfileAbandonmentService>().AbandonAsync(world.TeamId, world.ActorId, world.ProfileId, batchSize, CancellationToken.None);
    }

    private async Task RetireAsync(RoutedWorld world)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var profile = await db.StorageProfile.AsNoTracking().Where(row => row.Id == world.ProfileId)
            .Select(row => new { row.Xmin, row.CurrentRevision }).SingleAsync();

        await scope.Resolve<IStorageProfileService>().SetStateAsync(world.TeamId, world.ActorId, new SetStorageProfileStateCommand
        {
            ProfileId = world.ProfileId, State = StorageProfileStateValue.Retired,
            ExpectedXmin = profile.Xmin, ExpectedCurrentRevision = profile.CurrentRevision,
        }, CancellationToken.None);
    }

    private async Task<StorageProfileState> StateAsync(RoutedWorld world)
    {
        using var scope = _fixture.BeginScope();

        return await scope.Resolve<CodeSpaceDbContext>().StorageProfile.AsNoTracking()
            .Where(profile => profile.Id == world.ProfileId).Select(profile => profile.State).SingleAsync();
    }

    /// <summary>A worker that claimed a placement and died before it could delete or release it: the marker is all it left.</summary>
    private async Task<Guid> OrphanOneClaimAsync(RoutedWorld world)
    {
        using var scope = _fixture.BeginScope();
        var placement = await scope.Resolve<CodeSpaceDbContext>().ArtifactLocation.AsNoTracking()
            .Where(location => location.TeamId == world.TeamId).OrderBy(location => location.Id)
            .Select(location => new { location.Id, location.ArtifactObjectId }).FirstAsync();

        (await scope.Resolve<IArtifactCasPurgeCoordinator>().ClaimAsync(new ArtifactCasPurgeRequest
        {
            TeamId = world.TeamId, ArtifactObjectId = placement.ArtifactObjectId, ActorId = world.ActorId, ArtifactLocationId = placement.Id,
        }, CancellationToken.None)).ShouldBeOfType<ArtifactCasPurgeClaimResult.Claimed>();

        return placement.Id;
    }

    private async Task<ArtifactLocationState> LocationStateAsync(Guid locationId)
    {
        using var scope = _fixture.BeginScope();

        return await scope.Resolve<CodeSpaceDbContext>().ArtifactLocation.AsNoTracking()
            .Where(location => location.Id == locationId).Select(location => location.State).SingleAsync();
    }

    /// <summary>A profile whose objects really exist on disk, so "the destination still serves it" is a fact rather than a fixture flag.</summary>
    private async Task<RoutedWorld> SeedRoutedProfileAsync(int placements)
    {
        var (teamId, actorId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var root = Path.Combine(Path.GetTempPath(), $"codespace-abandon-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        _roots.Add(root);

        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var now = DateTimeOffset.UtcNow;
        var profileId = Guid.NewGuid();
        var revisionId = Guid.NewGuid();
        var config = JsonSerializer.Serialize(new { rootPath = root });
        using var document = JsonDocument.Parse(config);

        db.StorageProfile.Add(new StorageProfile
        {
            Id = profileId, TeamId = teamId, StableName = $"abandon-{profileId:N}", CurrentRevision = 1,
            State = StorageProfileState.Disabled, CreatedDate = now, CreatedBy = actorId, LastModifiedDate = now, LastModifiedBy = actorId,
            Revisions =
            {
                new StorageProfileRevision
                {
                    Id = revisionId, TeamId = teamId, StorageProfileId = profileId, Revision = 1,
                    ProviderTypeKey = LocalRwxArtifactStorageDriverFactory.TypeKey, NonSecretConfigJson = config, CredentialRef = null,
                    NamespaceFingerprint = StorageProfileRules.NamespaceFingerprint(LocalRwxArtifactStorageDriverFactory.TypeKey, document.RootElement),
                    CreatedDate = now, CreatedBy = actorId,
                },
            },
        });
        await db.SaveChangesAsync();

        foreach (var index in Enumerable.Range(0, placements)) await PlaceAsync(db, teamId, actorId, revisionId, root, index);

        return new RoutedWorld(teamId, actorId, profileId, root);
    }

    private static async Task PlaceAsync(CodeSpaceDbContext db, Guid teamId, Guid actorId, Guid revisionId, string root, int index)
    {
        var now = DateTimeOffset.UtcNow;
        var objectId = Guid.NewGuid();
        var payload = System.Text.Encoding.UTF8.GetBytes($"object {index} {objectId:N}");
        var digest = System.Security.Cryptography.SHA256.HashData(payload);
        var objectKey = $"artifacts/{objectId:N}";
        // The local driver namespaces every key under an "objects" directory of its own, so a fixture that wrote
        // where it thought the key pointed would have the destination report Missing and abandon a live object.
        var path = Path.Combine(root, "objects", objectKey.Replace('/', Path.DirectorySeparatorChar));

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, payload);

        db.ArtifactObject.Add(new ArtifactObject { Id = objectId, TeamId = teamId, Digest = digest, SizeBytes = payload.Length, CreatedDate = now });

        var location = new ArtifactLocation
        {
            Id = Guid.NewGuid(), TeamId = teamId, ArtifactObjectId = objectId, StorageProfileRevisionId = revisionId,
            Locator = objectKey, ObjectKey = objectKey, State = ArtifactLocationState.Available, Revision = 1, VerifiedAt = now,
            ObservedSizeBytes = payload.Length, ProviderChecksumAlgorithm = "Sha256", ProviderChecksum = digest,
            CreatedDate = now, CreatedBy = actorId, LastModifiedDate = now, LastModifiedBy = actorId,
        };
        db.ArtifactLocation.Add(location);
        db.ArtifactLocationEvent.Add(new ArtifactLocationEvent
        {
            Id = Guid.NewGuid(), TeamId = teamId, ArtifactLocationId = location.Id, Revision = 1,
            EventType = ArtifactLocationEventType.Created, State = ArtifactLocationState.Available, ObservedAt = now,
            ProviderChecksumAlgorithm = "Sha256", ProviderChecksum = digest, ObservedSizeBytes = payload.Length,
            VerifiedAt = now, DetailsJson = "{}", CreatedBy = actorId,
        });

        await db.SaveChangesAsync();
    }

    public void Dispose()
    {
        foreach (var root in _roots.Where(Directory.Exists))
        {
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) { }
        }
    }

    private sealed record RoutedWorld(Guid TeamId, Guid ActorId, Guid ProfileId, string Root);
}
