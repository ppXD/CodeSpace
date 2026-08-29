using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Workflows.Artifacts.Profiles;
using CodeSpace.Core.Services.Workflows.Artifacts.Providers.Local;
using CodeSpace.Core.Services.Workflows.Artifacts.Runtime;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Dtos.Storage;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using System.Text.Json;

namespace CodeSpace.IntegrationTests.Workflows.Artifacts.Runtime;

/// <summary>
/// What a storage profile still holds, for an operator deciding whether to retire it.
///
/// <para>The refusal an operator hits gives a count and nothing else, and retirement is irreversible — a retired
/// profile can never take another revision, so its credential can never be rotated. Under-reporting here is the
/// dangerous direction: it is the number the decision is made on.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class ProfilePlacementReaderTests
{
    private readonly PostgresFixture _fixture;

    public ProfilePlacementReaderTests(PostgresFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Placements_left_behind_by_an_older_revision_are_reported_too()
    {
        // A placement's profile revision is immutable for its life, so a profile that was ever re-pointed holds rows
        // under several. Reporting only the current revision would tell an operator a re-pointed profile was empty —
        // which is exactly the profile someone is trying to decommission.
        var world = await SeedProfileAsync();
        await PlaceAsync(world, world.FirstRevisionId, "objects/old", ArtifactLocationState.Available);
        var secondRevisionId = await AppendRevisionAsync(world);
        await PlaceAsync(world, secondRevisionId, "objects/new", ArtifactLocationState.Available);

        var page = await ListAsync(world);

        page.Items.Count.ShouldBe(2, "a profile's history is part of what it still holds");
        page.Items.Select(item => item.ProfileRevision).OrderBy(value => value).ShouldBe([1, 2]);
    }

    [Fact]
    public async Task Every_state_is_reported_separately_rather_than_summed_into_one_number()
    {
        // Available, Missing and Purged mean three different things to someone deciding what to do next: bytes that
        // are there, bytes that are gone, and a record already settled. A single total hides which one they face.
        var world = await SeedProfileAsync();
        await PlaceAsync(world, world.FirstRevisionId, "objects/live", ArtifactLocationState.Available);
        await PlaceAsync(world, world.FirstRevisionId, "objects/lost-one", ArtifactLocationState.Missing);
        await PlaceAsync(world, world.FirstRevisionId, "objects/lost-two", ArtifactLocationState.Missing);

        var totals = await TotalsAsync(world);

        totals.Single(total => total.State == ArtifactLocationStateValue.Available).Count.ShouldBe(1);
        totals.Single(total => total.State == ArtifactLocationStateValue.Missing).Count.ShouldBe(2);
        totals.Single(total => total.State == ArtifactLocationStateValue.Missing).SizeBytes.ShouldBe(24, "an operator budgeting a move needs the bytes, not just the rows");
    }

    [Fact]
    public async Task One_team_never_sees_another_teams_placements()
    {
        var world = await SeedProfileAsync();
        var other = await SeedProfileAsync();
        await PlaceAsync(other, other.FirstRevisionId, "objects/theirs", ArtifactLocationState.Available);

        (await ListAsync(world)).Items.ShouldBeEmpty();
        (await TotalsAsync(world)).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_page_hands_back_a_cursor_that_continues_where_it_stopped()
    {
        var world = await SeedProfileAsync();
        foreach (var index in Enumerable.Range(0, 3)) await PlaceAsync(world, world.FirstRevisionId, $"objects/page-{index}", ArtifactLocationState.Available);

        var first = await ListAsync(world, limit: 2);
        var second = await ListAsync(world, limit: 2, cursor: first.NextCursor);

        first.Items.Count.ShouldBe(2);
        first.NextCursor.ShouldNotBeNull();
        second.Items.Count.ShouldBe(1);
        second.Items.ShouldAllBe(item => !first.Items.Select(seen => seen.LocationId).Contains(item.LocationId), "a cursor that repeats a row would make an operator double-count what they are about to lose");
        second.NextCursor.ShouldBeNull();
    }

    // ─── World ───────────────────────────────────────────────────────────────

    private async Task<ProfilePlacementPage> ListAsync(ProfileWorld world, int limit = 50, string? cursor = null)
    {
        using var scope = _fixture.BeginScope();

        return await scope.Resolve<IProfilePlacementReader>().ListAsync(world.TeamId, world.ProfileId, cursor, limit, CancellationToken.None);
    }

    private async Task<IReadOnlyList<ProfilePlacementTotal>> TotalsAsync(ProfileWorld world)
    {
        using var scope = _fixture.BeginScope();

        return await scope.Resolve<IProfilePlacementReader>().TotalsAsync(world.TeamId, world.ProfileId, CancellationToken.None);
    }

    private async Task<ProfileWorld> SeedProfileAsync()
    {
        var (teamId, actorId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var now = DateTimeOffset.UtcNow;
        var profileId = Guid.NewGuid();
        var revisionId = Guid.NewGuid();
        var config = $"{{\"rootPath\":\"/tmp/codespace-placements-{profileId:N}\"}}";
        using var document = JsonDocument.Parse(config);

        db.StorageProfile.Add(new StorageProfile
        {
            Id = profileId, TeamId = teamId, StableName = $"placements-{profileId:N}", CurrentRevision = 1,
            State = StorageProfileState.Active, CreatedDate = now, CreatedBy = actorId, LastModifiedDate = now, LastModifiedBy = actorId,
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

    /// <summary>Appends a second revision and advances the profile onto it — what re-pointing a profile does, and what leaves rows behind on the first.</summary>
    private async Task<Guid> AppendRevisionAsync(ProfileWorld world)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var now = DateTimeOffset.UtcNow;
        var revisionId = Guid.NewGuid();
        var config = $"{{\"rootPath\":\"/tmp/codespace-placements-moved-{revisionId:N}\"}}";
        using var document = JsonDocument.Parse(config);

        db.StorageProfileRevision.Add(new StorageProfileRevision
        {
            Id = revisionId, TeamId = world.TeamId, StorageProfileId = world.ProfileId, Revision = 2,
            ProviderTypeKey = LocalRwxArtifactStorageDriverFactory.TypeKey, NonSecretConfigJson = config, CredentialRef = null,
            NamespaceFingerprint = StorageProfileRules.NamespaceFingerprint(LocalRwxArtifactStorageDriverFactory.TypeKey, document.RootElement),
            CreatedDate = now, CreatedBy = world.ActorId,
        });
        // The revision must exist before current_revision may name it — fk_storage_profile_current_revision — so the
        // insert commits first and the pointer moves after.
        await db.SaveChangesAsync();
        await db.StorageProfile.Where(profile => profile.Id == world.ProfileId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(profile => profile.CurrentRevision, 2));

        return revisionId;
    }

    private async Task PlaceAsync(ProfileWorld world, Guid revisionId, string objectKey, ArtifactLocationState state)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var now = DateTimeOffset.UtcNow;
        var objectId = Guid.NewGuid();
        var checksum = System.Security.Cryptography.SHA256.HashData(objectId.ToByteArray());

        db.ArtifactObject.Add(new ArtifactObject { Id = objectId, TeamId = world.TeamId, Digest = checksum, SizeBytes = 12, CreatedDate = now });

        var location = new ArtifactLocation
        {
            Id = Guid.NewGuid(), TeamId = world.TeamId, ArtifactObjectId = objectId, StorageProfileRevisionId = revisionId,
            Locator = objectKey, ObjectKey = objectKey, State = state, Revision = 1, VerifiedAt = now,
            ObservedSizeBytes = 12, ProviderChecksumAlgorithm = "Sha256", ProviderChecksum = checksum,
            CreatedDate = now, CreatedBy = world.ActorId, LastModifiedDate = now, LastModifiedBy = world.ActorId,
        };
        db.ArtifactLocation.Add(location);
        db.ArtifactLocationEvent.Add(new ArtifactLocationEvent
        {
            Id = Guid.NewGuid(), TeamId = world.TeamId, ArtifactLocationId = location.Id, Revision = 1,
            EventType = ArtifactLocationEventType.Created, State = state, ObservedAt = now,
            ProviderChecksumAlgorithm = "Sha256", ProviderChecksum = checksum, ObservedSizeBytes = 12, VerifiedAt = now,
            DetailsJson = "{}", CreatedBy = world.ActorId,
        });

        await db.SaveChangesAsync();
    }

    private sealed record ProfileWorld(Guid TeamId, Guid ActorId, Guid ProfileId, Guid FirstRevisionId);
}
