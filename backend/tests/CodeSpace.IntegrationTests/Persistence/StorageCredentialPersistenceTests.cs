using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.Persistence;

/// <summary>
/// Real-Postgres proof for the additive storage credential ledger. It covers tenant-bound identity, immutable encrypted
/// revisions, deferred current pointers, actor references, terminal revocation and xmin. No service, API, ArtifactStore,
/// harness or completion path consumes this schema.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class StorageCredentialPersistenceTests
{
    private readonly PostgresFixture _fixture;

    public StorageCredentialPersistenceTests(PostgresFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Credential_and_first_encrypted_revision_round_trip()
    {
        var (teamId, actorId) = await SeedTeamAsync();
        var credential = Credential(teamId, "primary-storage", actorId);
        credential.Revisions.Add(Revision(credential, 1, actorId));

        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            db.StorageCredential.Add(credential);
            await db.SaveChangesAsync();
        }

        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            var stored = await db.StorageCredential.Include(c => c.Revisions).SingleAsync(c => c.Id == credential.Id);

            stored.TeamId.ShouldBe(teamId);
            stored.StableName.ShouldBe("primary-storage");
            stored.State.ShouldBe(StorageCredentialState.Active);
            stored.CurrentRevision.ShouldBe(1);
            stored.RevokedDate.ShouldBeNull();
            stored.RevokedBy.ShouldBeNull();
            stored.Xmin.ShouldNotBe(0u);

            var revision = stored.Revisions.ShouldHaveSingleItem();
            revision.ProviderTypeKey.ShouldBe("s3-compatible/v1");
            revision.EncryptedPayload.ShouldBe(ProtectedEnvelope(1));
            revision.SafeHint.ShouldBe("hint-a1b2");
            revision.EnvelopeFingerprint.ShouldBe(Fingerprint('a'));
        }
    }

    [Fact]
    public async Task Revision_is_immutable_and_rotation_requires_a_new_revision()
    {
        var (credential, actorId) = await SeedCredentialAsync();

        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            var revision = await db.StorageCredentialRevision.SingleAsync(r => r.StorageCredentialId == credential.Id && r.Revision == 1);
            revision.EnvelopeFingerprint = Fingerprint('b');
            var update = await db.SaveChangesAsync().ShouldThrowAsync<DbUpdateException>();
            update.InnerException?.Message.ShouldContain("immutable");
        }

        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            var revision = await db.StorageCredentialRevision.SingleAsync(r => r.StorageCredentialId == credential.Id && r.Revision == 1);
            db.StorageCredentialRevision.Remove(revision);
            var delete = await db.SaveChangesAsync().ShouldThrowAsync<DbUpdateException>();
            delete.InnerException?.Message.ShouldContain("immutable");
        }

        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            var current = await db.StorageCredential.SingleAsync(c => c.Id == credential.Id);
            db.StorageCredentialRevision.Add(Revision(current, 2, actorId, 'b'));
            current.CurrentRevision = 2;
            await db.SaveChangesAsync();
        }

        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            var revisions = await db.StorageCredentialRevision.AsNoTracking().Where(r => r.StorageCredentialId == credential.Id).OrderBy(r => r.Revision).ToListAsync();
            revisions.Select(r => r.Revision).ShouldBe(new[] { 1, 2 });
            revisions.Select(r => r.EnvelopeFingerprint).ShouldBe(new[] { Fingerprint('a'), Fingerprint('b') });
            (await db.StorageCredential.AsNoTracking().SingleAsync(c => c.Id == credential.Id)).CurrentRevision.ShouldBe(2);
        }
    }

    [Fact]
    public async Task Tenant_actor_name_revision_and_current_pointer_constraints_fail_closed()
    {
        var (credential, actorId) = await SeedCredentialAsync();
        var (otherTeamId, _) = await SeedTeamAsync();

        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            var crossTeam = Revision(credential, 2, actorId);
            crossTeam.TeamId = otherTeamId;
            db.StorageCredentialRevision.Add(crossTeam);
            await db.SaveChangesAsync().ShouldThrowAsync<DbUpdateException>();
        }

        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            var duplicate = Credential(credential.TeamId, credential.StableName, actorId);
            duplicate.Revisions.Add(Revision(duplicate, 1, actorId));
            db.StorageCredential.Add(duplicate);
            await db.SaveChangesAsync().ShouldThrowAsync<DbUpdateException>();
        }

        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            var otherActorId = await ActorForTeamAsync(db, otherTeamId);
            var sameNameOtherTeam = Credential(otherTeamId, credential.StableName, otherActorId);
            sameNameOtherTeam.Revisions.Add(Revision(sameNameOtherTeam, 1, otherActorId));
            db.StorageCredential.Add(sameNameOtherTeam);
            await db.SaveChangesAsync();
        }

        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            db.StorageCredentialRevision.Add(Revision(credential, 1, actorId));
            await db.SaveChangesAsync().ShouldThrowAsync<DbUpdateException>();
        }

        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            db.StorageCredentialRevision.Add(Revision(credential, 3, actorId));
            var outOfOrder = await db.SaveChangesAsync().ShouldThrowAsync<DbUpdateException>();
            outOfOrder.InnerException?.Message.ShouldContain("contiguous append-only sequence");
        }

        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            var invalidActor = Credential(credential.TeamId, $"invalid-actor-{Guid.NewGuid():N}", Guid.NewGuid());
            invalidActor.Revisions.Add(Revision(invalidActor, 1, actorId));
            db.StorageCredential.Add(invalidActor);
            await db.SaveChangesAsync().ShouldThrowAsync<DbUpdateException>();
        }

        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            var invalidActor = Revision(credential, 2, Guid.NewGuid());
            db.StorageCredentialRevision.Add(invalidActor);
            await db.SaveChangesAsync().ShouldThrowAsync<DbUpdateException>();
        }

        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            var stored = await db.StorageCredential.SingleAsync(c => c.Id == credential.Id);
            stored.State = StorageCredentialState.Revoked;
            stored.RevokedDate = DateTimeOffset.UtcNow;
            stored.RevokedBy = Guid.NewGuid();
            await db.SaveChangesAsync().ShouldThrowAsync<DbUpdateException>();
        }

        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            var stored = await db.StorageCredential.SingleAsync(c => c.Id == credential.Id);
            stored.CurrentRevision = 99;
            await db.SaveChangesAsync().ShouldThrowAsync<DbUpdateException>();
        }
    }

    [Fact]
    public async Task Provider_envelope_hint_fingerprint_state_and_revocation_shape_are_database_guarded()
    {
        var (credential, actorId) = await SeedCredentialAsync();

        await InvalidRevisionAsync(credential, actorId, revision => revision.ProviderTypeKey = "s3-compatible");
        await InvalidRevisionAsync(credential, actorId, revision => revision.EncryptedPayload = "   ");
        await InvalidRevisionAsync(credential, actorId, revision => revision.SafeHint = "line\nbreak");
        await InvalidRevisionAsync(credential, actorId, revision => revision.EnvelopeFingerprint = "opaque-envelope");

        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            var stored = await db.StorageCredential.SingleAsync(c => c.Id == credential.Id);
            stored.State = (StorageCredentialState)99;
            await db.SaveChangesAsync().ShouldThrowAsync<DbUpdateException>();
        }

        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            var stored = await db.StorageCredential.SingleAsync(c => c.Id == credential.Id);
            stored.State = StorageCredentialState.Revoked;
            await db.SaveChangesAsync().ShouldThrowAsync<DbUpdateException>();
        }
    }

    [Fact]
    public async Task Revocation_is_terminal_and_blocks_new_revisions_and_hard_delete()
    {
        var (credential, actorId) = await SeedCredentialAsync();

        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            var stored = await db.StorageCredential.SingleAsync(c => c.Id == credential.Id);
            stored.State = StorageCredentialState.Revoked;
            stored.RevokedDate = DateTimeOffset.UtcNow;
            stored.RevokedBy = actorId;
            await db.SaveChangesAsync();
        }

        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            var revoked = await db.StorageCredential.SingleAsync(c => c.Id == credential.Id);
            revoked.State = StorageCredentialState.Active;
            revoked.RevokedDate = null;
            revoked.RevokedBy = null;
            var resurrection = await db.SaveChangesAsync().ShouldThrowAsync<DbUpdateException>();
            resurrection.InnerException?.Message.ShouldContain("terminal");
        }

        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            db.StorageCredentialRevision.Add(Revision(credential, 2, actorId, 'b'));
            var append = await db.SaveChangesAsync().ShouldThrowAsync<DbUpdateException>();
            append.InnerException?.Message.ShouldContain("terminal");
        }

        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            var revoked = await db.StorageCredential.SingleAsync(c => c.Id == credential.Id);
            db.StorageCredential.Remove(revoked);
            var delete = await db.SaveChangesAsync().ShouldThrowAsync<DbUpdateException>();
            delete.InnerException?.Message.ShouldContain("durable identity");
        }
    }

    [Fact]
    public async Task Xmin_rejects_a_stale_revocation_transition()
    {
        var (credential, actorId) = await SeedCredentialAsync();
        using var firstScope = _fixture.BeginScope();
        using var secondScope = _fixture.BeginScope();
        var firstDb = firstScope.Resolve<CodeSpaceDbContext>();
        var first = await firstDb.StorageCredential.SingleAsync(c => c.Id == credential.Id);
        var secondDb = secondScope.Resolve<CodeSpaceDbContext>();
        var second = await secondDb.StorageCredential.SingleAsync(c => c.Id == credential.Id);

        first.State = StorageCredentialState.Revoked;
        first.RevokedDate = DateTimeOffset.UtcNow;
        first.RevokedBy = actorId;
        await firstDb.SaveChangesAsync();

        second.CurrentRevision = 2;
        await secondDb.SaveChangesAsync().ShouldThrowAsync<DbUpdateConcurrencyException>();
    }

    private async Task InvalidRevisionAsync(StorageCredential credential, Guid actorId, Action<StorageCredentialRevision> mutate)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var revision = Revision(credential, 2, actorId);
        mutate(revision);
        db.StorageCredentialRevision.Add(revision);
        await db.SaveChangesAsync().ShouldThrowAsync<DbUpdateException>();
    }

    private async Task<(StorageCredential Credential, Guid ActorId)> SeedCredentialAsync()
    {
        var (teamId, actorId) = await SeedTeamAsync();
        var credential = Credential(teamId, $"store-{Guid.NewGuid():N}", actorId);
        credential.Revisions.Add(Revision(credential, 1, actorId));

        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        db.StorageCredential.Add(credential);
        await db.SaveChangesAsync();
        return (credential, actorId);
    }

    private static StorageCredential Credential(Guid teamId, string stableName, Guid actorId) => new()
    {
        Id = Guid.NewGuid(),
        TeamId = teamId,
        StableName = stableName,
        CurrentRevision = 1,
        State = StorageCredentialState.Active,
        CreatedDate = DateTimeOffset.UtcNow,
        CreatedBy = actorId,
    };

    private static StorageCredentialRevision Revision(StorageCredential credential, int revision, Guid actorId, char fingerprint = 'a') => new()
    {
        Id = Guid.NewGuid(),
        TeamId = credential.TeamId,
        StorageCredentialId = credential.Id,
        Revision = revision,
        ProviderTypeKey = "s3-compatible/v1",
        EncryptedPayload = ProtectedEnvelope(revision),
        SafeHint = "hint-a1b2",
        EnvelopeFingerprint = Fingerprint(fingerprint),
        CreatedDate = DateTimeOffset.UtcNow,
        CreatedBy = actorId,
    };

    private async Task<(Guid TeamId, Guid ActorId)> SeedTeamAsync()
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var actorId = Guid.NewGuid();
        var teamId = Guid.NewGuid();
        db.User.Add(new User { Id = actorId, Email = $"storage-credential-{actorId:N}@test.local", Name = $"storage-credential-{actorId:N}" });
        db.Team.Add(new Team { Id = teamId, Slug = $"storage-credential-{teamId:N}", Name = "Storage Credential Team", Kind = TeamKind.Workspace });
        db.TeamMembership.Add(new TeamMembership { Id = Guid.NewGuid(), TeamId = teamId, UserId = actorId, Role = TeamRole.Owner });
        await db.SaveChangesAsync();
        return (teamId, actorId);
    }

    private static Task<Guid> ActorForTeamAsync(CodeSpaceDbContext db, Guid teamId) => db.TeamMembership.Where(m => m.TeamId == teamId).Select(m => m.UserId).SingleAsync();

    private static string ProtectedEnvelope(int revision) => $"protected-envelope-{revision:D4}-opaque";
    private static string Fingerprint(char hex) => $"sha256:{new string(hex, 64)}";
}
