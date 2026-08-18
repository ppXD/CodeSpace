using System.Security.Cryptography;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Workflows.Artifacts.Profiles;
using CodeSpace.Core.Services.Workflows.Artifacts.Profiles.Exceptions;
using CodeSpace.Core.Services.Workflows.Artifacts.Providers.Local;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.Messages.Commands.Storage;
using CodeSpace.Messages.Dtos.Storage;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows.Artifacts.Profiles;

/// <summary>
/// Retirement is the only irreversible storage-profile transition, so it must not be reachable while a live route or a
/// stored artifact still names the profile. Disable stays reachable throughout: it is reversible and it is how an
/// operator quiesces writes before retiring.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class StorageProfileRetirementTests
{
    private readonly PostgresFixture _fixture;

    public StorageProfileRetirementTests(PostgresFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Retire_is_refused_while_an_active_route_targets_the_profile_and_allowed_once_the_route_is_disabled()
    {
        var world = await SeedWorldAsync();
        var routeId = await SeedRouteAsync(world, StorageRouteState.Active);

        var refused = await Should.ThrowAsync<StorageProfileConflictException>(() => RetireAsync(world));
        refused.Message.ShouldContain("active storage route");
        (await StateAsync(world)).ShouldBe(StorageProfileStateValue.Active);

        await SetRouteStateAsync(routeId, StorageRouteState.Disabled);

        (await RetireAsync(world))!.State.ShouldBe(StorageProfileStateValue.Retired);
    }

    [Fact]
    public async Task Retire_is_refused_while_an_available_artifact_location_lives_under_the_profile()
    {
        var world = await SeedWorldAsync();
        var locationId = await SeedLocationAsync(world, ArtifactLocationState.Available);

        var refused = await Should.ThrowAsync<StorageProfileConflictException>(() => RetireAsync(world));
        refused.Message.ShouldContain("stored artifact location");
        refused.Code.ShouldBe(CodeSpace.Messages.Failures.FailureCodes.StorageProfileConflict);
        refused.Kind.ShouldBe(CodeSpace.Messages.Failures.FailureKind.Conflict);
        (await StateAsync(world)).ShouldBe(StorageProfileStateValue.Active);

        await ReleaseLocationAsync(world, locationId);

        (await RetireAsync(world))!.State.ShouldBe(StorageProfileStateValue.Retired);
    }

    [Fact]
    public async Task Disable_stays_reachable_with_both_references_present_because_it_is_reversible()
    {
        var world = await SeedWorldAsync();
        await SeedRouteAsync(world, StorageRouteState.Active);
        await SeedLocationAsync(world, ArtifactLocationState.Available);

        (await SetStateAsync(world, StorageProfileStateValue.Disabled))!.State.ShouldBe(StorageProfileStateValue.Disabled);
        (await SetStateAsync(world, StorageProfileStateValue.Active))!.State.ShouldBe(StorageProfileStateValue.Active);
    }

    [Fact]
    public async Task A_foreign_teams_route_and_location_never_block_retirement()
    {
        var world = await SeedWorldAsync();
        var foreign = await SeedWorldAsync();
        await SeedRouteAsync(foreign, StorageRouteState.Active);
        await SeedLocationAsync(foreign, ArtifactLocationState.Available);

        (await RetireAsync(world))!.State.ShouldBe(StorageProfileStateValue.Retired);
    }

    private Task<StorageProfileDetail?> RetireAsync(World world) => SetStateAsync(world, StorageProfileStateValue.Retired);

    private async Task<StorageProfileDetail?> SetStateAsync(World world, StorageProfileStateValue state)
    {
        using var scope = _fixture.BeginScope();
        var profile = await scope.Resolve<CodeSpaceDbContext>().StorageProfile.AsNoTracking()
            .SingleAsync(value => value.TeamId == world.TeamId && value.Id == world.ProfileId);
        return await scope.Resolve<IStorageProfileService>().SetStateAsync(world.TeamId, world.ActorId, new SetStorageProfileStateCommand
        {
            ProfileId = world.ProfileId, ExpectedXmin = profile.Xmin, ExpectedCurrentRevision = profile.CurrentRevision, State = state,
        }, CancellationToken.None);
    }

    private async Task<StorageProfileStateValue> StateAsync(World world)
    {
        using var scope = _fixture.BeginScope();
        return (await scope.Resolve<IStorageProfileService>().GetAsync(world.TeamId, world.ProfileId, CancellationToken.None))!.State;
    }

    private async Task<World> SeedWorldAsync()
    {
        var actorId = Guid.NewGuid();
        var teamId = Guid.NewGuid();
        var profileId = Guid.NewGuid();
        var revisionId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        db.User.Add(new User { Id = actorId, Email = $"retire-{actorId:N}@test.local", Name = $"retire-{actorId:N}" });
        db.Team.Add(new Team { Id = teamId, Slug = $"retire-{teamId:N}", Name = "Storage Retirement Team", Kind = TeamKind.Workspace });
        db.TeamMembership.Add(new TeamMembership { Id = Guid.NewGuid(), TeamId = teamId, UserId = actorId, Role = TeamRole.Owner });
        var profile = new StorageProfile
        {
            Id = profileId, TeamId = teamId, StableName = $"retire-{Guid.NewGuid():N}", CurrentRevision = 1,
            State = StorageProfileState.Active, CreatedDate = now, CreatedBy = actorId, LastModifiedDate = now, LastModifiedBy = actorId,
        };
        profile.Revisions.Add(new StorageProfileRevision
        {
            Id = revisionId, TeamId = teamId, StorageProfileId = profileId, Revision = 1,
            ProviderTypeKey = LocalRwxArtifactStorageDriverFactory.TypeKey, NonSecretConfigJson = "{\"rootPath\":\"/unused/retire\"}",
            NamespaceFingerprint = $"sha256:{new string('a', 64)}", CreatedDate = now, CreatedBy = actorId,
        });
        db.StorageProfile.Add(profile);
        await db.SaveChangesAsync();
        return new World(teamId, actorId, profileId, revisionId);
    }

    /// <summary>A storage_route row is forced to start Draft at revision 1 by a database trigger, so the target state is a follow-up update.</summary>
    private async Task<Guid> SeedRouteAsync(World world, StorageRouteState state)
    {
        var now = DateTimeOffset.UtcNow;
        var route = new StorageRoute
        {
            Id = Guid.NewGuid(), TeamId = world.TeamId, DataClassTypeKey = $"retire-{Guid.NewGuid():N}/v1", CurrentRevision = 1,
            State = StorageRouteState.Draft, CreatedDate = now, CreatedBy = world.ActorId, LastModifiedDate = now, LastModifiedBy = world.ActorId,
        };
        route.Revisions.Add(new StorageRouteRevision
        {
            Id = Guid.NewGuid(), TeamId = world.TeamId, StorageRouteId = route.Id, Revision = 1,
            StorageProfileId = world.ProfileId, ProfileRevisionMode = StorageProfileRevisionMode.Pinned, PinnedProfileRevision = 1,
            CreatedDate = now, CreatedBy = world.ActorId,
        });

        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        db.StorageRoute.Add(route);
        await db.SaveChangesAsync();
        await SetRouteStateAsync(route.Id, state);
        return route.Id;
    }

    /// <summary>Every artifact_location revision needs a matching append-only event snapshot, enforced by a deferred database trigger.</summary>
    private async Task<Guid> SeedLocationAsync(World world, ArtifactLocationState state)
    {
        var now = DateTimeOffset.UtcNow;
        var digest = SHA256.HashData([]);
        var artifact = new ArtifactObject
        {
            Id = Guid.NewGuid(), TeamId = world.TeamId, DigestAlgorithm = ArtifactDigestAlgorithm.Sha256,
            Digest = digest, SizeBytes = 0, CreatedDate = now, CreatedBy = world.ActorId,
        };
        var location = new ArtifactLocation
        {
            Id = Guid.NewGuid(), TeamId = world.TeamId, ArtifactObjectId = artifact.Id, StorageProfileRevisionId = world.ProfileRevisionId,
            Locator = $"test://{artifact.Id:N}", ObjectKey = $"cas/{artifact.Id:N}.bin", ProviderChecksumAlgorithm = "Sha256",
            ProviderChecksum = digest, ObservedSizeBytes = 0, State = state, Revision = 1, VerifiedAt = now,
            CreatedDate = now, CreatedBy = world.ActorId, LastModifiedDate = now, LastModifiedBy = world.ActorId,
        };

        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        db.ArtifactObject.Add(artifact);
        db.ArtifactLocation.Add(location);
        db.ArtifactLocationEvent.Add(Event(location, ArtifactLocationEventType.Verified, world.ActorId));
        await db.SaveChangesAsync();
        return location.Id;
    }

    private async Task ReleaseLocationAsync(World world, Guid locationId)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var location = await db.ArtifactLocation.SingleAsync(value => value.Id == locationId);
        location.State = ArtifactLocationState.Deleted;
        location.Revision++;
        location.LastModifiedDate = DateTimeOffset.UtcNow;
        db.ArtifactLocationEvent.Add(Event(location, ArtifactLocationEventType.StateChanged, world.ActorId));
        await db.SaveChangesAsync();
    }

    private static ArtifactLocationEvent Event(ArtifactLocation location, ArtifactLocationEventType eventType, Guid actorId) => new()
    {
        Id = Guid.NewGuid(), TeamId = location.TeamId, ArtifactLocationId = location.Id, Revision = location.Revision,
        EventType = eventType, State = location.State, ObservedAt = location.LastModifiedDate,
        ProviderChecksumAlgorithm = location.ProviderChecksumAlgorithm, ProviderChecksum = location.ProviderChecksum,
        ObservedSizeBytes = location.ObservedSizeBytes, VerifiedAt = location.VerifiedAt, DetailsJson = "{}", CreatedBy = actorId,
    };

    private async Task SetRouteStateAsync(Guid routeId, StorageRouteState state)
    {
        using var scope = _fixture.BeginScope();
        await scope.Resolve<CodeSpaceDbContext>().StorageRoute.Where(value => value.Id == routeId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(value => value.State, state));
    }

    private sealed record World(Guid TeamId, Guid ActorId, Guid ProfileId, Guid ProfileRevisionId);
}
