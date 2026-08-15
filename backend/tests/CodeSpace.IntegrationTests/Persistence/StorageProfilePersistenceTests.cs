using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.Persistence;

/// <summary>
/// Real-Postgres proof for the storage profile ledger: tenant identity cannot cross teams, revisions cannot be
/// rewritten or deleted, current revision is a real deferred FK, JSON/type/fingerprint constraints fail closed,
/// and xmin rejects stale profile state transitions. No ArtifactStore runtime path consumes this schema.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class StorageProfilePersistenceTests
{
    private readonly PostgresFixture _fixture;

    public StorageProfilePersistenceTests(PostgresFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Profile_and_first_revision_round_trip_without_a_plaintext_secret()
    {
        var teamId = await SeedTeamAsync();
        var actorId = Guid.NewGuid();
        var profile = Profile(teamId, "primary-artifacts", actorId);
        profile.Revisions.Add(Revision(profile, 1, actorId));

        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            db.StorageProfile.Add(profile);
            await db.SaveChangesAsync();
        }

        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            var stored = await db.StorageProfile.Include(p => p.Revisions).SingleAsync(p => p.Id == profile.Id);

            stored.TeamId.ShouldBe(teamId);
            stored.StableName.ShouldBe("primary-artifacts");
            stored.State.ShouldBe(StorageProfileState.Draft);
            stored.CurrentRevision.ShouldBe(1);
            stored.Xmin.ShouldNotBe(0u);

            var revision = stored.Revisions.ShouldHaveSingleItem();
            revision.ProviderTypeKey.ShouldBe("local-rwx/v1");
            revision.NonSecretConfigJson.ShouldBe("{\"rootPath\":\"/srv/codespace/artifacts\"}");
            revision.CredentialRef.ShouldBe("credential://storage/primary");
            revision.NamespaceFingerprint.ShouldBe(Fingerprint('a'));
        }
    }

    [Fact]
    public async Task Revision_is_immutable_and_a_namespace_change_requires_a_new_revision()
    {
        var (profile, actorId) = await SeedProfileAsync();

        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            var revision = await db.StorageProfileRevision.SingleAsync(r => r.StorageProfileId == profile.Id && r.Revision == 1);
            revision.NamespaceFingerprint = Fingerprint('b');
            var update = await db.SaveChangesAsync().ShouldThrowAsync<DbUpdateException>();
            update.InnerException?.Message.ShouldContain("immutable");
        }

        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            var revision = await db.StorageProfileRevision.SingleAsync(r => r.StorageProfileId == profile.Id && r.Revision == 1);
            db.StorageProfileRevision.Remove(revision);
            var delete = await db.SaveChangesAsync().ShouldThrowAsync<DbUpdateException>();
            delete.InnerException?.Message.ShouldContain("immutable");
        }

        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            var current = await db.StorageProfile.SingleAsync(p => p.Id == profile.Id);
            db.StorageProfileRevision.Add(Revision(current, 2, actorId, Fingerprint('b')));
            current.CurrentRevision = 2;
            await db.SaveChangesAsync();
        }

        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            var revisions = await db.StorageProfileRevision.AsNoTracking().Where(r => r.StorageProfileId == profile.Id).OrderBy(r => r.Revision).ToListAsync();
            revisions.Select(r => r.Revision).ShouldBe(new[] { 1, 2 });
            revisions.Select(r => r.NamespaceFingerprint).ShouldBe(new[] { Fingerprint('a'), Fingerprint('b') });
            (await db.StorageProfile.AsNoTracking().SingleAsync(p => p.Id == profile.Id)).CurrentRevision.ShouldBe(2);
        }
    }

    [Fact]
    public async Task Composite_scope_and_stable_name_constraints_fail_closed()
    {
        var (profile, actorId) = await SeedProfileAsync();
        var otherTeamId = await SeedTeamAsync();

        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            var crossTeam = Revision(profile, 2, actorId);
            crossTeam.TeamId = otherTeamId;
            db.StorageProfileRevision.Add(crossTeam);
            await db.SaveChangesAsync().ShouldThrowAsync<DbUpdateException>();
        }

        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            var duplicate = Profile(profile.TeamId, profile.StableName, actorId);
            duplicate.Revisions.Add(Revision(duplicate, 1, actorId));
            db.StorageProfile.Add(duplicate);
            await db.SaveChangesAsync().ShouldThrowAsync<DbUpdateException>();
        }

        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            db.StorageProfileRevision.Add(Revision(profile, 1, actorId));
            await db.SaveChangesAsync().ShouldThrowAsync<DbUpdateException>();
        }

        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            var sameNameOtherTeam = Profile(otherTeamId, profile.StableName, actorId);
            sameNameOtherTeam.Revisions.Add(Revision(sameNameOtherTeam, 1, actorId));
            db.StorageProfile.Add(sameNameOtherTeam);
            await db.SaveChangesAsync();
        }
    }

    [Fact]
    public async Task Type_key_config_credential_ref_fingerprint_and_current_pointer_are_database_guarded()
    {
        var (profile, actorId) = await SeedProfileAsync();

        await InvalidRevisionAsync(profile, actorId, revision => revision.ProviderTypeKey = "local-rwx");
        await InvalidRevisionAsync(profile, actorId, revision => revision.NonSecretConfigJson = "[]");
        await InvalidRevisionAsync(profile, actorId, revision => revision.CredentialRef = "   ");
        await InvalidRevisionAsync(profile, actorId, revision => revision.NamespaceFingerprint = "/srv/plaintext/namespace");

        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            var stored = await db.StorageProfile.SingleAsync(p => p.Id == profile.Id);
            stored.State = (StorageProfileState)99;
            await db.SaveChangesAsync().ShouldThrowAsync<DbUpdateException>();
        }

        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            var stored = await db.StorageProfile.SingleAsync(p => p.Id == profile.Id);
            stored.CurrentRevision = 99;
            await db.SaveChangesAsync().ShouldThrowAsync<DbUpdateException>();
        }
    }

    [Fact]
    public async Task Xmin_rejects_stale_profile_state_transition()
    {
        var (profile, _) = await SeedProfileAsync();
        using var firstScope = _fixture.BeginScope();
        using var secondScope = _fixture.BeginScope();
        var first = await firstScope.Resolve<CodeSpaceDbContext>().StorageProfile.SingleAsync(p => p.Id == profile.Id);
        var secondDb = secondScope.Resolve<CodeSpaceDbContext>();
        var second = await secondDb.StorageProfile.SingleAsync(p => p.Id == profile.Id);

        first.State = StorageProfileState.Active;
        await firstScope.Resolve<CodeSpaceDbContext>().SaveChangesAsync();

        second.State = StorageProfileState.Disabled;
        await secondDb.SaveChangesAsync().ShouldThrowAsync<DbUpdateConcurrencyException>();
    }

    [Fact]
    public async Task Retired_state_is_terminal_at_the_database_boundary()
    {
        var (profile, _) = await SeedProfileAsync();

        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            var stored = await db.StorageProfile.SingleAsync(p => p.Id == profile.Id);
            stored.State = StorageProfileState.Retired;
            await db.SaveChangesAsync();
        }

        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            var retired = await db.StorageProfile.SingleAsync(p => p.Id == profile.Id);
            retired.State = StorageProfileState.Active;
            var resurrection = await db.SaveChangesAsync().ShouldThrowAsync<DbUpdateException>();
            resurrection.InnerException?.Message.ShouldContain("terminal");
        }
    }

    private async Task InvalidRevisionAsync(StorageProfile profile, Guid actorId, Action<StorageProfileRevision> mutate)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var revision = Revision(profile, 2, actorId);
        mutate(revision);
        db.StorageProfileRevision.Add(revision);
        await db.SaveChangesAsync().ShouldThrowAsync<DbUpdateException>();
    }

    private async Task<(StorageProfile Profile, Guid ActorId)> SeedProfileAsync()
    {
        var teamId = await SeedTeamAsync();
        var actorId = Guid.NewGuid();
        var profile = Profile(teamId, $"store-{Guid.NewGuid():N}", actorId);
        profile.Revisions.Add(Revision(profile, 1, actorId));

        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        db.StorageProfile.Add(profile);
        await db.SaveChangesAsync();
        return (profile, actorId);
    }

    private static StorageProfile Profile(Guid teamId, string stableName, Guid actorId) => new()
    {
        Id = Guid.NewGuid(),
        TeamId = teamId,
        StableName = stableName,
        CurrentRevision = 1,
        State = StorageProfileState.Draft,
        CreatedDate = DateTimeOffset.UtcNow,
        CreatedBy = actorId,
        LastModifiedDate = DateTimeOffset.UtcNow,
        LastModifiedBy = actorId,
    };

    private static StorageProfileRevision Revision(StorageProfile profile, int revision, Guid actorId, string? namespaceFingerprint = null) => new()
    {
        Id = Guid.NewGuid(),
        TeamId = profile.TeamId,
        StorageProfileId = profile.Id,
        Revision = revision,
        ProviderTypeKey = "local-rwx/v1",
        NonSecretConfigJson = "{\"rootPath\":\"/srv/codespace/artifacts\"}",
        CredentialRef = "credential://storage/primary",
        NamespaceFingerprint = namespaceFingerprint ?? Fingerprint('a'),
        CreatedDate = DateTimeOffset.UtcNow,
        CreatedBy = actorId,
    };

    private async Task<Guid> SeedTeamAsync()
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var userId = Guid.NewGuid();
        var teamId = Guid.NewGuid();
        db.User.Add(new User { Id = userId, Email = $"storage-{userId:N}@test.local", Name = $"storage-{userId:N}" });
        db.Team.Add(new Team { Id = teamId, Slug = $"storage-{teamId:N}", Name = "Storage Team", Kind = TeamKind.Workspace });
        db.TeamMembership.Add(new TeamMembership { Id = Guid.NewGuid(), TeamId = teamId, UserId = userId, Role = TeamRole.Owner });
        await db.SaveChangesAsync();
        return teamId;
    }

    private static string Fingerprint(char hex) => $"sha256:{new string(hex, 64)}";
}
