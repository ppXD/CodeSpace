using System.Security.Cryptography;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Workflows.Artifacts.Credentials;
using CodeSpace.Core.Services.Workflows.Artifacts.Credentials.Exceptions;
using CodeSpace.Core.Services.Workflows.Artifacts.Providers.Local;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.Messages.Commands.Storage;
using CodeSpace.Messages.Dtos.Storage;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows.Artifacts.Credentials;

/// <summary>
/// Revocation is terminal and the secret resolver admits none but an Active credential, so revoking one a stored
/// object still resolves through would make those bytes permanently unreadable — the stranding the profile lifecycle
/// used to cause, one layer down. It must be refused while anything still needs the credential.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class StorageCredentialRevocationTests
{
    private readonly PostgresFixture _fixture;

    public StorageCredentialRevocationTests(PostgresFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Revoke_is_refused_while_a_stored_location_still_resolves_through_the_credential()
    {
        var world = await SeedWorldAsync();
        var locationId = await SeedLocationAsync(world, ArtifactLocationState.Available);
        await RetireProfileAsync(world);   // the profile is out of the way; only the stored bytes still need the key

        var refused = await Should.ThrowAsync<StorageCredentialConflictException>(() => RevokeAsync(world));

        refused.Message.ShouldContain("stored artifact location");
        refused.Code.ShouldBe(CodeSpace.Messages.Failures.FailureCodes.StorageCredentialConflict);
        refused.Kind.ShouldBe(CodeSpace.Messages.Failures.FailureKind.Conflict);
        (await StateAsync(world)).ShouldBe(StorageCredentialStateValue.Active, "a refused revoke must leave the credential exactly as it was — the transition is irreversible");

        await ReleaseLocationAsync(world, locationId);

        (await RevokeAsync(world))!.State.ShouldBe(StorageCredentialStateValue.Revoked, "nothing resolves through it any more, so the key can go");
    }

    [Fact]
    public async Task Revoke_is_refused_while_a_live_profile_still_references_the_credential()
    {
        var world = await SeedWorldAsync();

        var refused = await Should.ThrowAsync<StorageCredentialConflictException>(() => RevokeAsync(world));

        refused.Message.ShouldContain("storage profile");
        (await StateAsync(world)).ShouldBe(StorageCredentialStateValue.Active);

        await RetireProfileAsync(world);

        (await RevokeAsync(world))!.State.ShouldBe(StorageCredentialStateValue.Revoked);
    }

    [Fact]
    public async Task A_reference_belonging_to_another_credential_never_blocks_revocation()
    {
        // The reference is the structured db:{id}:{version} form matched by prefix, so a different credential's
        // reference must not be mistaken for this one's — otherwise one team's stored bytes would pin every key.
        var world = await SeedWorldAsync();
        var other = await SeedWorldAsync();
        await SeedLocationAsync(other, ArtifactLocationState.Available);
        await RetireProfileAsync(world);

        (await RevokeAsync(world))!.State.ShouldBe(StorageCredentialStateValue.Revoked);
    }

    [Fact]
    public async Task A_foreign_teams_location_never_blocks_revocation()
    {
        var world = await SeedWorldAsync();
        var foreign = await SeedWorldAsync();
        await SeedLocationAsync(foreign, ArtifactLocationState.Available);
        await RetireProfileAsync(world);

        (await RevokeAsync(world))!.State.ShouldBe(StorageCredentialStateValue.Revoked);
    }

    private async Task<StorageCredentialMetadata?> RevokeAsync(World world)
    {
        using var scope = _fixture.BeginScope();
        var credential = await scope.Resolve<CodeSpaceDbContext>().StorageCredential.AsNoTracking()
            .SingleAsync(value => value.TeamId == world.TeamId && value.Id == world.CredentialId);

        return await scope.Resolve<IStorageCredentialService>().RevokeAsync(world.TeamId, world.ActorId, new RevokeStorageCredentialCommand
        {
            CredentialId = world.CredentialId, ExpectedXmin = credential.Xmin, ExpectedCurrentRevision = credential.CurrentRevision,
        }, CancellationToken.None);
    }

    private async Task<StorageCredentialStateValue> StateAsync(World world)
    {
        using var scope = _fixture.BeginScope();
        return (await scope.Resolve<IStorageCredentialService>().GetAsync(world.TeamId, world.CredentialId, CancellationToken.None))!.State;
    }

    /// <summary>Take the profile out of the picture so a test can isolate the location guard from the live-profile one.</summary>
    private async Task RetireProfileAsync(World world)
    {
        using var scope = _fixture.BeginScope();
        await scope.Resolve<CodeSpaceDbContext>().StorageProfile.Where(value => value.Id == world.ProfileId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(value => value.State, StorageProfileState.Retired));
    }

    private async Task<World> SeedWorldAsync()
    {
        var actorId = Guid.NewGuid();
        var teamId = Guid.NewGuid();
        var credentialId = Guid.NewGuid();
        var profileId = Guid.NewGuid();
        var revisionId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        db.User.Add(new User { Id = actorId, Email = $"revoke-{actorId:N}@test.local", Name = $"revoke-{actorId:N}" });
        db.Team.Add(new Team { Id = teamId, Slug = $"revoke-{teamId:N}", Name = "Storage Revocation Team", Kind = TeamKind.Workspace });
        db.TeamMembership.Add(new TeamMembership { Id = Guid.NewGuid(), TeamId = teamId, UserId = actorId, Role = TeamRole.Owner });

        var credential = new StorageCredential
        {
            Id = credentialId, TeamId = teamId, StableName = $"revoke-{Guid.NewGuid():N}", CurrentRevision = 1,
            State = StorageCredentialState.Active, CreatedDate = now, CreatedBy = actorId,
        };
        credential.Revisions.Add(new StorageCredentialRevision
        {
            Id = Guid.NewGuid(), TeamId = teamId, StorageCredentialId = credentialId, Revision = 1,
            ProviderTypeKey = LocalRwxArtifactStorageDriverFactory.TypeKey, EncryptedPayload = "encrypted-placeholder",
            EnvelopeFingerprint = $"sha256:{new string('b', 64)}", CreatedDate = now, CreatedBy = actorId,
        });
        db.StorageCredential.Add(credential);

        var profile = new StorageProfile
        {
            Id = profileId, TeamId = teamId, StableName = $"revoke-{Guid.NewGuid():N}", CurrentRevision = 1,
            State = StorageProfileState.Active, CreatedDate = now, CreatedBy = actorId, LastModifiedDate = now, LastModifiedBy = actorId,
        };
        profile.Revisions.Add(new StorageProfileRevision
        {
            Id = revisionId, TeamId = teamId, StorageProfileId = profileId, Revision = 1,
            ProviderTypeKey = LocalRwxArtifactStorageDriverFactory.TypeKey, NonSecretConfigJson = "{\"rootPath\":\"/unused/revoke\"}",
            CredentialRef = $"db:{credentialId}:1", NamespaceFingerprint = $"sha256:{new string('c', 64)}", CreatedDate = now, CreatedBy = actorId,
        });
        db.StorageProfile.Add(profile);

        await db.SaveChangesAsync();
        return new World(teamId, actorId, credentialId, profileId, revisionId);
    }

    /// <summary>Every artifact_location revision needs a matching append-only event snapshot, enforced by a deferred database trigger.</summary>
    private async Task<Guid> SeedLocationAsync(World world, ArtifactLocationState state)
    {
        var now = DateTimeOffset.UtcNow;
        var digest = SHA256.HashData(Guid.NewGuid().ToByteArray());
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

    private sealed record World(Guid TeamId, Guid ActorId, Guid CredentialId, Guid ProfileId, Guid ProfileRevisionId);
}
