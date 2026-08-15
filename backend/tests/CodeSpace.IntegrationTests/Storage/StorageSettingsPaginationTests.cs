using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Workflows.Artifacts.Credentials;
using CodeSpace.Core.Services.Workflows.Artifacts.Profiles;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.Storage;

[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class StorageSettingsPaginationTests
{
    private readonly PostgresFixture _fixture;

    public StorageSettingsPaginationTests(PostgresFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Profile_and_credential_pages_are_tenant_scoped_stable_and_bounded()
    {
        var (teamId, actorId) = await SeedWorldAsync();
        var (foreignTeamId, foreignActorId) = await SeedWorldAsync();
        await SeedRowsAsync(teamId, actorId, "alpha", "bravo", "charlie");
        await SeedRowsAsync(foreignTeamId, foreignActorId, "foreign");

        using var scope = _fixture.BeginScope();
        var profileService = scope.Resolve<IStorageProfileService>();
        var credentialService = scope.Resolve<IStorageCredentialService>();

        var profileFirst = await profileService.ListPageAsync(teamId, null, 2, CancellationToken.None);
        var profileSecond = await profileService.ListPageAsync(teamId, profileFirst.NextCursor, 2, CancellationToken.None);
        profileFirst.Items.Select(value => value.StableName).ShouldBe(["alpha", "bravo"]);
        profileFirst.NextCursor.ShouldNotBeNull();
        profileSecond.Items.Select(value => value.StableName).ShouldBe(["charlie"]);
        profileSecond.NextCursor.ShouldBeNull();

        var credentialFirst = await credentialService.ListPageAsync(teamId, null, 2, CancellationToken.None);
        var credentialSecond = await credentialService.ListPageAsync(teamId, credentialFirst.NextCursor, 2, CancellationToken.None);
        credentialFirst.Items.Select(value => value.StableName).ShouldBe(["alpha", "bravo"]);
        credentialSecond.Items.Select(value => value.StableName).ShouldBe(["charlie"]);

        (await profileService.ListPageAsync(teamId, null, 0, CancellationToken.None)).Items.Count.ShouldBe(1);
        (await credentialService.ListPageAsync(teamId, null, 0, CancellationToken.None)).Items.Count.ShouldBe(1);
    }

    private async Task SeedRowsAsync(Guid teamId, Guid actorId, params string[] stableNames)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        foreach (var stableName in stableNames)
        {
            var profile = Profile(teamId, actorId, stableName);
            profile.Revisions.Add(ProfileRevision(profile, actorId));
            db.StorageProfile.Add(profile);

            var credential = Credential(teamId, actorId, stableName);
            credential.Revisions.Add(CredentialRevision(credential, actorId));
            db.StorageCredential.Add(credential);
        }
        await db.SaveChangesAsync();
    }

    private async Task<(Guid TeamId, Guid ActorId)> SeedWorldAsync()
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var actorId = Guid.NewGuid();
        var teamId = Guid.NewGuid();
        db.User.Add(new User { Id = actorId, Email = $"storage-page-{actorId:N}@test.local", Name = "Storage pagination" });
        db.Team.Add(new Team { Id = teamId, Slug = $"storage-page-{teamId:N}", Name = "Storage pagination", Kind = TeamKind.Workspace });
        db.TeamMembership.Add(new TeamMembership { Id = Guid.NewGuid(), TeamId = teamId, UserId = actorId, Role = TeamRole.Owner });
        await db.SaveChangesAsync();
        return (teamId, actorId);
    }

    private static StorageProfile Profile(Guid teamId, Guid actorId, string stableName) => new()
    {
        Id = Guid.NewGuid(), TeamId = teamId, StableName = stableName, CurrentRevision = 1, State = StorageProfileState.Draft,
        CreatedDate = DateTimeOffset.UtcNow, CreatedBy = actorId, LastModifiedDate = DateTimeOffset.UtcNow, LastModifiedBy = actorId,
    };

    private static StorageProfileRevision ProfileRevision(StorageProfile profile, Guid actorId) => new()
    {
        Id = Guid.NewGuid(), TeamId = profile.TeamId, StorageProfileId = profile.Id, Revision = 1, ProviderTypeKey = "local-rwx/v1",
        NonSecretConfigJson = "{\"rootPath\":\"/srv/artifacts\"}", NamespaceFingerprint = "sha256:" + new string('a', 64),
        CreatedDate = DateTimeOffset.UtcNow, CreatedBy = actorId,
    };

    private static StorageCredential Credential(Guid teamId, Guid actorId, string stableName) => new()
    {
        Id = Guid.NewGuid(), TeamId = teamId, StableName = stableName, CurrentRevision = 1, State = StorageCredentialState.Active,
        CreatedDate = DateTimeOffset.UtcNow, CreatedBy = actorId,
    };

    private static StorageCredentialRevision CredentialRevision(StorageCredential credential, Guid actorId) => new()
    {
        Id = Guid.NewGuid(), TeamId = credential.TeamId, StorageCredentialId = credential.Id, Revision = 1,
        ProviderTypeKey = "local-rwx/v1", EncryptedPayload = "CfDJ8pagination", EnvelopeFingerprint = "sha256:" + new string('b', 64),
        CreatedDate = DateTimeOffset.UtcNow, CreatedBy = actorId,
    };
}
