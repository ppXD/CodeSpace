using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Workflows.Artifacts.Profiles;
using CodeSpace.Core.Services.Workflows.Artifacts.Profiles.Exceptions;
using CodeSpace.Core.Services.Workflows.Artifacts.Providers.Local;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Commands.Storage;
using CodeSpace.Messages.Dtos.Storage;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using System.Text.Json;

namespace CodeSpace.IntegrationTests.Workflows.Artifacts;

/// <summary>
/// What the retirement guard is willing to call "released".
///
/// <para>It counted only <c>Available</c> placements, so a profile holding nothing but lost and corrupted records
/// looked empty and could be retired. Retirement is irreversible and forbids any further revision, which also removes
/// the only repair those records had — so the guard was granting the one permanent decision on the strength of the
/// healthy placements alone.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class RetireGuardCountsEveryPlacementTests
{
    private readonly PostgresFixture _fixture;

    public RetireGuardCountsEveryPlacementTests(PostgresFixture fixture) => _fixture = fixture;

    [Theory]
    [InlineData(ArtifactLocationState.Missing)]   // the bytes are gone, and that is still a record of this destination
    [InlineData(ArtifactLocationState.Corrupt)]   // the destination holds something else, which is not nothing
    public async Task A_placement_that_is_lost_rather_than_healthy_still_blocks_retirement(ArtifactLocationState state)
    {
        var world = await SeedProfileAsync();
        await PlaceAsync(world, "objects/unsettled", state);

        var refusal = await Should.ThrowAsync<StorageProfileConflictException>(() => RetireAsync(world));

        refusal.Message.ShouldContain("1 stored artifact location");
    }

    [Fact]
    public async Task A_placement_that_has_been_settled_does_not_block_retirement()
    {
        // The counterpart, and the reason the drain arc had to land first: with no way to reach a settled state, a
        // stricter guard would only have turned a soft dead end into a hard one. Purged is reached the way production
        // reaches it — through a Deleting claim — because the schema refuses to let a row be created there.
        var world = await SeedProfileAsync();
        var locationId = await PlaceAsync(world, "objects/settled", ArtifactLocationState.Available);
        await SettleAsync(world, locationId, ArtifactLocationState.Deleting);
        await SettleAsync(world, locationId, ArtifactLocationState.Purged);

        await RetireAsync(world);

        (await StateAsync(world)).ShouldBe(StorageProfileState.Retired);
    }

    /// <summary>Advances a placement one state, with the ledger entry the schema requires alongside it.</summary>
    private async Task SettleAsync(ProfileWorld world, Guid locationId, ArtifactLocationState state)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var location = await db.ArtifactLocation.SingleAsync(row => row.Id == locationId);
        var now = DateTimeOffset.UtcNow;
        location.State = state;
        location.Revision++;
        location.LastModifiedDate = now;
        db.ArtifactLocationEvent.Add(new ArtifactLocationEvent
        {
            Id = Guid.NewGuid(), TeamId = world.TeamId, ArtifactLocationId = location.Id, Revision = location.Revision,
            EventType = ArtifactLocationEventType.StateChanged, State = state, ObservedAt = now,
            ProviderObjectVersion = location.ProviderObjectVersion, ProviderETag = location.ProviderETag,
            ProviderChecksumAlgorithm = location.ProviderChecksumAlgorithm, ProviderChecksum = location.ProviderChecksum,
            ObservedSizeBytes = location.ObservedSizeBytes, VerifiedAt = location.VerifiedAt,
            ErrorCode = location.LastErrorCode, ErrorMessage = location.LastErrorMessage, DetailsJson = "{}", CreatedBy = world.ActorId,
        });

        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task A_profile_holding_nothing_at_all_retires()
    {
        var world = await SeedProfileAsync();

        await RetireAsync(world);

        (await StateAsync(world)).ShouldBe(StorageProfileState.Retired);
    }

    // ─── World ───────────────────────────────────────────────────────────────

    private async Task RetireAsync(ProfileWorld world)
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

    private async Task<StorageProfileState> StateAsync(ProfileWorld world)
    {
        using var scope = _fixture.BeginScope();

        return await scope.Resolve<CodeSpaceDbContext>().StorageProfile.AsNoTracking()
            .Where(profile => profile.Id == world.ProfileId).Select(profile => profile.State).SingleAsync();
    }

    private async Task<ProfileWorld> SeedProfileAsync()
    {
        var (teamId, actorId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var now = DateTimeOffset.UtcNow;
        var profileId = Guid.NewGuid();
        var revisionId = Guid.NewGuid();
        var config = $"{{\"rootPath\":\"/tmp/codespace-guard-{profileId:N}\"}}";
        using var document = JsonDocument.Parse(config);

        db.StorageProfile.Add(new StorageProfile
        {
            Id = profileId, TeamId = teamId, StableName = $"guard-{profileId:N}", CurrentRevision = 1,
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

        return new ProfileWorld(teamId, actorId, profileId, revisionId);
    }

    private async Task<Guid> PlaceAsync(ProfileWorld world, string objectKey, ArtifactLocationState state)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var now = DateTimeOffset.UtcNow;
        var objectId = Guid.NewGuid();
        var checksum = System.Security.Cryptography.SHA256.HashData(objectId.ToByteArray());
        var available = state == ArtifactLocationState.Available;

        db.ArtifactObject.Add(new ArtifactObject { Id = objectId, TeamId = world.TeamId, Digest = checksum, SizeBytes = 12, CreatedDate = now });

        var location = new ArtifactLocation
        {
            Id = Guid.NewGuid(), TeamId = world.TeamId, ArtifactObjectId = objectId, StorageProfileRevisionId = world.RevisionId,
            Locator = objectKey, ObjectKey = objectKey, State = state, Revision = 1, VerifiedAt = now,
            ObservedSizeBytes = available ? 12 : null,
            ProviderChecksumAlgorithm = available ? "Sha256" : null, ProviderChecksum = available ? checksum : null,
            CreatedDate = now, CreatedBy = world.ActorId, LastModifiedDate = now, LastModifiedBy = world.ActorId,
        };
        db.ArtifactLocation.Add(location);
        db.ArtifactLocationEvent.Add(new ArtifactLocationEvent
        {
            Id = Guid.NewGuid(), TeamId = world.TeamId, ArtifactLocationId = location.Id, Revision = 1,
            EventType = ArtifactLocationEventType.Created, State = state, ObservedAt = now,
            ProviderChecksumAlgorithm = location.ProviderChecksumAlgorithm, ProviderChecksum = location.ProviderChecksum,
            ObservedSizeBytes = location.ObservedSizeBytes, VerifiedAt = location.VerifiedAt, DetailsJson = "{}", CreatedBy = world.ActorId,
        });

        await db.SaveChangesAsync();

        return location.Id;
    }

    private sealed record ProfileWorld(Guid TeamId, Guid ActorId, Guid ProfileId, Guid RevisionId);
}
