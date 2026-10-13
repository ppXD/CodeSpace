using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Workflows.Artifacts.Credentials;
using CodeSpace.Core.Services.Workflows.Artifacts.Providers.Local;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.Messages.Commands.Storage;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows.Artifacts.Credentials;

/// <summary>
/// Revoking a storage credential blanks the encrypted payload of every one of its revisions, and only a revoked
/// credential's revisions may ever be blanked.
///
/// <para>Why revoked and not merely superseded: the secret resolver reads the exact revision a storage profile pins
/// (<c>db:{id}:{revision}</c>), so while a credential is Active every revision of it is a live key — a profile revision
/// that still holds stored bytes resolves through it, and a new profile revision may pin it
/// (<c>StorageCredentialSecretResolverTests.Exact_old_revision_never_falls_forward_to_the_current_secret</c>). Blanking
/// one would make routed bytes permanently unreadable. A Revoked credential resolves for nobody: the resolver answers
/// <c>NotActive</c> before it reads any envelope, revocation is terminal, and it is refused while a stored location or a
/// live profile still resolves through the credential. So its ciphertext is dead weight, recoverable only by someone
/// holding a database copy and the key ring.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class StorageCredentialShredTests
{
    private readonly PostgresFixture _fixture;

    public StorageCredentialShredTests(PostgresFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Revoking_blanks_every_revisions_payload_and_keeps_what_the_audit_trail_reads()
    {
        var world = await SeedRotatedCredentialAsync();
        var before = await RevisionsAsync(world);

        await RevokeAsync(world);

        var after = await RevisionsAsync(world);
        after.ShouldAllBe(r => r.EncryptedPayload == string.Empty, "a revoked credential's revisions must not stay decryptable");
        after.Select(Audit).ShouldBe(before.Select(Audit), "revision, provider, hint, envelope fingerprint and authorship stay for the audit trail");

        using var scope = _fixture.BeginScope();
        var resolver = scope.Resolve<IStorageCredentialSecretResolver>();
        foreach (var revision in new[] { 1, 2 })
            (await resolver.ResolveAsync(new StorageCredentialSecretRequest(world.TeamId, world.CredentialId, revision, LocalRwxArtifactStorageDriverFactory.TypeKey), CancellationToken.None))
                .ShouldBe(new StorageCredentialSecretResolution.NotActive(StorageCredentialState.Revoked), "a revoked credential resolves for nobody, so nothing reads the blanked envelope");
    }

    [Fact]
    public async Task A_superseded_revision_of_an_active_credential_keeps_its_payload()
    {
        var world = await SeedRotatedCredentialAsync();

        var refused = await Should.ThrowAsync<PostgresException>(() => ExecuteAsync($"UPDATE storage_credential_revision SET encrypted_payload = '' WHERE storage_credential_id = '{world.CredentialId}' AND revision = 1"));

        refused.MessageText.ShouldContain("Revoked", Case.Sensitive, "a profile may still pin revision 1 of an Active credential; blanking it would strand the bytes stored through it");
        (await RevisionsAsync(world)).ShouldAllBe(r => r.EncryptedPayload != string.Empty);
    }

    [Theory]
    [InlineData("UPDATE storage_credential_revision SET envelope_fingerprint = 'sha256:" + "ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff" + "' WHERE storage_credential_id = '{0}'")]
    [InlineData("UPDATE storage_credential_revision SET encrypted_payload = 'CfDJ8-restored' WHERE storage_credential_id = '{0}'")]
    [InlineData("UPDATE storage_credential_revision SET encrypted_payload = '' WHERE storage_credential_id = '{0}'")]
    [InlineData("DELETE FROM storage_credential_revision WHERE storage_credential_id = '{0}'")]
    public async Task On_a_revoked_credential_the_one_blanking_update_is_all_the_guard_ever_admits(string statement)
    {
        var world = await SeedRotatedCredentialAsync();
        await RevokeAsync(world);

        var refused = await Should.ThrowAsync<PostgresException>(() => ExecuteAsync(string.Format(statement, world.CredentialId)));

        refused.MessageText.ShouldContain("immutable", Case.Sensitive, "a blanked revision is final — no second blanking, no restore, no other edit, no delete");
    }

    [Fact]
    public async Task A_revision_never_arrives_blank()
    {
        var world = await SeedRotatedCredentialAsync();

        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var credential = await db.StorageCredential.SingleAsync(c => c.Id == world.CredentialId);
        db.StorageCredentialRevision.Add(Revision(world, 3, string.Empty));
        credential.CurrentRevision = 3;

        var refused = await Should.ThrowAsync<DbUpdateException>(() => db.SaveChangesAsync());

        refused.InnerException.ShouldBeOfType<PostgresException>().MessageText.ShouldContain("payload is required", Case.Sensitive);
    }

    private static object Audit(StorageCredentialRevision r) => new { r.Id, r.Revision, r.ProviderTypeKey, r.SafeHint, r.EnvelopeFingerprint, r.CreatedDate, r.CreatedBy };

    private async Task RevokeAsync(World world)
    {
        using var scope = _fixture.BeginScope();
        var credential = await scope.Resolve<CodeSpaceDbContext>().StorageCredential.AsNoTracking().SingleAsync(c => c.Id == world.CredentialId);

        await scope.Resolve<IStorageCredentialService>().RevokeAsync(world.TeamId, world.ActorId, new RevokeStorageCredentialCommand { CredentialId = world.CredentialId, ExpectedXmin = credential.Xmin, ExpectedCurrentRevision = credential.CurrentRevision }, CancellationToken.None);
    }

    private async Task<List<StorageCredentialRevision>> RevisionsAsync(World world)
    {
        using var scope = _fixture.BeginScope();

        return await scope.Resolve<CodeSpaceDbContext>().StorageCredentialRevision.AsNoTracking().Where(r => r.StorageCredentialId == world.CredentialId).OrderBy(r => r.Revision).ToListAsync();
    }

    private async Task ExecuteAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(_fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>An Active credential rotated once — revision 1 superseded, revision 2 current — that no profile references.</summary>
    private async Task<World> SeedRotatedCredentialAsync()
    {
        var world = new World(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());

        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        db.User.Add(new User { Id = world.ActorId, Email = $"shred-{world.ActorId:N}@test.local", Name = $"shred-{world.ActorId:N}" });
        db.Team.Add(new Team { Id = world.TeamId, Slug = $"shred-{world.TeamId:N}", Name = "Storage Shred Team", Kind = TeamKind.Workspace });
        db.TeamMembership.Add(new TeamMembership { Id = Guid.NewGuid(), TeamId = world.TeamId, UserId = world.ActorId, Role = TeamRole.Owner });

        var credential = new StorageCredential { Id = world.CredentialId, TeamId = world.TeamId, StableName = $"shred-{Guid.NewGuid():N}", CurrentRevision = 1, State = StorageCredentialState.Active, CreatedDate = DateTimeOffset.UtcNow, CreatedBy = world.ActorId };
        credential.Revisions.Add(Revision(world, 1, "CfDJ8-CANARY-storage-revision-1"));
        db.StorageCredential.Add(credential);
        await db.SaveChangesAsync();

        db.StorageCredentialRevision.Add(Revision(world, 2, "CfDJ8-CANARY-storage-revision-2"));
        credential.CurrentRevision = 2;
        await db.SaveChangesAsync();

        return world;
    }

    private static StorageCredentialRevision Revision(World world, int revision, string encryptedPayload) => new()
    {
        Id = Guid.NewGuid(), TeamId = world.TeamId, StorageCredentialId = world.CredentialId, Revision = revision,
        ProviderTypeKey = LocalRwxArtifactStorageDriverFactory.TypeKey, EncryptedPayload = encryptedPayload, SafeHint = $"rev-{revision}",
        EnvelopeFingerprint = $"sha256:{new string((char)('a' + revision), 64)}", CreatedDate = DateTimeOffset.UtcNow, CreatedBy = world.ActorId,
    };

    private sealed record World(Guid TeamId, Guid ActorId, Guid CredentialId);
}
