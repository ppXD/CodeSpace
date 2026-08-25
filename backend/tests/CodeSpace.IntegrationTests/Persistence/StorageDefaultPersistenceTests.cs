using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Credentials;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Shouldly;

namespace CodeSpace.IntegrationTests.Persistence;

/// <summary>
/// Real-Postgres proof for the deployment storage template that migration 0173 creates: instance-scope ciphertext,
/// the CHECKed adoption-policy vocabulary, the immutability triggers, and the (team, data class) adoption key.
///
/// <para>No service, API, ArtifactStore, harness or completion path consumes this schema — the materializer lane is
/// the intended reader — so these constraints are the whole of what protects it today.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class StorageDefaultPersistenceTests
{
    private readonly PostgresFixture _fixture;

    public StorageDefaultPersistenceTests(PostgresFixture fixture) => _fixture = fixture;

    /// <summary>
    /// 0173 lands on top of the migrations already in the tree rather than beside a same-numbered sibling — the
    /// journal is keyed by NAME, so two scripts sharing a number both run and neither conflicts in git.
    /// </summary>
    [Fact]
    public async Task Migration_0173_applied_alongside_its_neighbours()
    {
        var applied = await ScriptNamesAsync();

        applied.Count(name => name.Contains("0173_", StringComparison.Ordinal)).ShouldBe(
            1, customMessage: $"expected exactly one 0173 script; applied scripts starting 016/017: {string.Join(", ", applied.Where(n => n.Contains("017", StringComparison.Ordinal)))}");

        foreach (var number in new[] { "0166", "0167", "0168", "0169", "0170", "0171", "0172", "0173" })
            applied.ShouldContain(name => name.Contains($"{number}_", StringComparison.Ordinal), $"migration {number} did not apply");
    }

    [Fact]
    public async Task Template_credential_and_provenance_round_trip_including_ciphertext()
    {
        var (teamId, actorId) = await SeedTeamAsync();
        var profileId = await SeedProfileAsync(teamId, actorId);
        const string plaintext = """{"accessKeyId":"AK","accessKeySecret":"shhh"}""";
        var dataClassTypeKey = UniqueDataClass();
        Guid templateId;

        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            var credential = Credential(actorId, scope.Resolve<IPayloadEncryptor>().Encrypt(plaintext));
            var template = Template(dataClassTypeKey, actorId, credential.Id);
            templateId = template.Id;
            db.StorageDefaultCredential.Add(credential);
            db.StorageDefault.Add(template);
            db.StorageDefaultMaterialization.Add(Materialization(teamId, profileId, actorId));
            await db.SaveChangesAsync();
        }

        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            var stored = await db.StorageDefault.AsNoTracking().SingleAsync(value => value.Id == templateId);

            stored.DataClassTypeKey.ShouldBe(dataClassTypeKey);
            stored.NamespaceRoot.ShouldBe("codespace-defaults/");
            stored.AdoptionPolicy.ShouldBe(StorageDefaultAdoptionPolicy.Explicit);
            stored.Revision.ShouldBe(1);
            stored.Xmin.ShouldNotBe(0u);

            var envelope = await db.StorageDefaultCredential.AsNoTracking().SingleAsync(value => value.Id == stored.CredentialId);
            envelope.EncryptedPayload.ShouldNotContain("shhh");
            scope.Resolve<IPayloadEncryptor>().Decrypt(envelope.EncryptedPayload).ShouldBe(plaintext);

            var provenance = await db.StorageDefaultMaterialization.AsNoTracking().SingleAsync(value => value.TeamId == teamId);
            provenance.StorageProfileId.ShouldBe(profileId);
            provenance.SourceRevision.ShouldBe(1);
        }
    }

    /// <summary>
    /// The vocabulary is a CHECK, not a convention: a third data class added later cannot invent its own policy word,
    /// and no writer — including the materializer lane, or hand-run SQL — can slip one past it.
    /// </summary>
    [Fact]
    public async Task Adoption_policy_check_rejects_an_unknown_value()
    {
        var (_, actorId) = await SeedTeamAsync();

        var exception = await InsertRawTemplateAsync(UniqueDataClass(), "Whenever", actorId).ShouldThrowAsync<PostgresException>();

        exception.SqlState.ShouldBe(PostgresErrorCodes.CheckViolation);
        exception.ConstraintName.ShouldBe("ck_storage_default_adoption_policy");
        await Should.NotThrowAsync(() => InsertRawTemplateAsync(UniqueDataClass(), "Automatic", actorId));
    }

    [Fact]
    public async Task Template_identity_is_immutable_and_the_row_cannot_be_deleted()
    {
        var (_, actorId) = await SeedTeamAsync();
        var templateId = await SeedTemplateAsync(UniqueDataClass(), actorId);

        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            var template = await db.StorageDefault.SingleAsync(value => value.Id == templateId);
            template.DataClassTypeKey = UniqueDataClass();
            (await db.SaveChangesAsync().ShouldThrowAsync<DbUpdateException>()).InnerException?.Message.ShouldContain("immutable");
        }

        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            db.StorageDefault.Remove(await db.StorageDefault.SingleAsync(value => value.Id == templateId));
            (await db.SaveChangesAsync().ShouldThrowAsync<DbUpdateException>()).InnerException?.Message.ShouldContain("DELETE rejected");
        }

        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            var template = await db.StorageDefault.SingleAsync(value => value.Id == templateId);
            template.Revision = 0;
            (await db.SaveChangesAsync().ShouldThrowAsync<DbUpdateException>()).InnerException?.Message.ShouldContain("monotonic");
        }
    }

    [Fact]
    public async Task Credential_envelope_is_append_only()
    {
        var (_, actorId) = await SeedTeamAsync();
        Guid credentialId;

        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            var credential = Credential(actorId, "protected-envelope");
            credentialId = credential.Id;
            db.StorageDefaultCredential.Add(credential);
            await db.SaveChangesAsync();
        }

        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            var credential = await db.StorageDefaultCredential.SingleAsync(value => value.Id == credentialId);
            credential.SafeHint = "rotated-in-place";
            (await db.SaveChangesAsync().ShouldThrowAsync<DbUpdateException>()).InnerException?.Message.ShouldContain("append-only");
        }

        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            db.StorageDefaultCredential.Remove(await db.StorageDefaultCredential.SingleAsync(value => value.Id == credentialId));
            (await db.SaveChangesAsync().ShouldThrowAsync<DbUpdateException>()).InnerException?.Message.ShouldContain("append-only");
        }
    }

    /// <summary>
    /// One adoption per (team, data class), and the profile it names must belong to that same team — a materialization
    /// that recorded another team's profile would be provenance pointing at storage the team does not own.
    /// </summary>
    [Fact]
    public async Task Materialization_is_one_row_per_team_and_class_and_binds_a_same_team_profile()
    {
        var (teamId, actorId) = await SeedTeamAsync();
        var (foreignTeamId, foreignActorId) = await SeedTeamAsync();
        var profileId = await SeedProfileAsync(teamId, actorId);
        var foreignProfileId = await SeedProfileAsync(foreignTeamId, foreignActorId);

        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            db.StorageDefaultMaterialization.Add(Materialization(teamId, profileId, actorId));
            await db.SaveChangesAsync();
        }

        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            db.StorageDefaultMaterialization.Add(Materialization(teamId, profileId, actorId));
            await db.SaveChangesAsync().ShouldThrowAsync<DbUpdateException>();
        }

        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            var crossTeam = Materialization(teamId, foreignProfileId, actorId);
            crossTeam.DataClassTypeKey = "agent-run-log/v1";
            db.StorageDefaultMaterialization.Add(crossTeam);
            await db.SaveChangesAsync().ShouldThrowAsync<DbUpdateException>();
        }
    }

    private async Task<IReadOnlyList<string>> ScriptNamesAsync()
    {
        await using var connection = new NpgsqlConnection(_fixture.ConnectionString);
        await connection.OpenAsync().ConfigureAwait(false);
        await using var command = new NpgsqlCommand("SELECT scriptname FROM schemaversions", connection);
        await using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        var names = new List<string>();
        while (await reader.ReadAsync().ConfigureAwait(false)) names.Add(reader.GetString(0));
        return names;
    }

    private async Task InsertRawTemplateAsync(string dataClassTypeKey, string adoptionPolicy, Guid actorId)
    {
        await using var connection = new NpgsqlConnection(_fixture.ConnectionString);
        await connection.OpenAsync().ConfigureAwait(false);
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO storage_default (id, data_class_type_key, revision, provider_type_key, config_jsonb, namespace_root,
                                         adoption_policy, is_enabled, created_date, created_by, last_modified_date, last_modified_by)
            VALUES (@id, @key, 1, 'local-rwx/v1', '{}'::jsonb, 'codespace-defaults/', @policy, true, now(), @actor, now(), @actor)
            """, connection);
        command.Parameters.AddWithValue("id", Guid.NewGuid());
        command.Parameters.AddWithValue("key", dataClassTypeKey);
        command.Parameters.AddWithValue("policy", adoptionPolicy);
        command.Parameters.AddWithValue("actor", actorId);
        await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    private async Task<Guid> SeedTemplateAsync(string dataClassTypeKey, Guid actorId)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var template = Template(dataClassTypeKey, actorId, credentialId: null);
        db.StorageDefault.Add(template);
        await db.SaveChangesAsync();
        return template.Id;
    }

    private async Task<Guid> SeedProfileAsync(Guid teamId, Guid actorId)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var profile = new StorageProfile
        {
            Id = Guid.NewGuid(), TeamId = teamId, StableName = $"seeded-{Guid.NewGuid():N}"[..24], CurrentRevision = 1,
            State = StorageProfileState.Active, CreatedDate = DateTimeOffset.UtcNow, CreatedBy = actorId,
            LastModifiedDate = DateTimeOffset.UtcNow, LastModifiedBy = actorId,
        };
        profile.Revisions.Add(new StorageProfileRevision
        {
            Id = Guid.NewGuid(), TeamId = teamId, StorageProfileId = profile.Id, Revision = 1,
            ProviderTypeKey = "local-rwx/v1", NonSecretConfigJson = """{"rootPath":"/srv/codespace"}""",
            NamespaceFingerprint = "sha256:" + new string('a', 64), CreatedDate = DateTimeOffset.UtcNow, CreatedBy = actorId,
        });
        db.StorageProfile.Add(profile);
        await db.SaveChangesAsync();
        return profile.Id;
    }

    private async Task<(Guid TeamId, Guid ActorId)> SeedTeamAsync()
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var user = new User { Id = Guid.NewGuid(), Email = $"default-{suffix}@test.local", Name = "Storage Default" };
        var team = new Team { Id = Guid.NewGuid(), Slug = $"default-{suffix}", Name = "Defaults", Kind = TeamKind.Workspace };
        db.User.Add(user);
        db.Team.Add(team);
        await db.SaveChangesAsync();
        return (team.Id, user.Id);
    }

    /// <summary>
    /// A template is INSTANCE scoped and unique per data class, and the identity trigger refuses DELETE — so a test
    /// that claimed a real key would permanently deny it to every other test sharing this fixture's database. These
    /// rows exercise constraints, not the routed-data-class catalog, so a synthetic key is the honest choice.
    /// </summary>
    private static string UniqueDataClass() => $"t{Guid.NewGuid():N}/v1";

    private static StorageDefault Template(string dataClassTypeKey, Guid actorId, Guid? credentialId) => new()
    {
        Id = Guid.NewGuid(), DataClassTypeKey = dataClassTypeKey, Revision = 1, ProviderTypeKey = "local-rwx/v1",
        NonSecretConfigJson = "{}", NamespaceRoot = "codespace-defaults/", CredentialId = credentialId,
        AdoptionPolicy = StorageDefaultAdoptionPolicy.Explicit, IsEnabled = true,
        CreatedDate = DateTimeOffset.UtcNow, CreatedBy = actorId, LastModifiedDate = DateTimeOffset.UtcNow, LastModifiedBy = actorId,
    };

    private static StorageDefaultCredential Credential(Guid actorId, string encryptedPayload) => new()
    {
        Id = Guid.NewGuid(), ProviderTypeKey = "aliyun-oss/v1", EncryptedPayload = encryptedPayload, SafeHint = "hint-a1b2",
        EnvelopeFingerprint = "sha256:" + new string('b', 64), CreatedDate = DateTimeOffset.UtcNow, CreatedBy = actorId,
    };

    private static StorageDefaultMaterialization Materialization(Guid teamId, Guid profileId, Guid actorId) => new()
    {
        Id = Guid.NewGuid(), TeamId = teamId, DataClassTypeKey = "workflow-artifact/v1", StorageProfileId = profileId,
        SourceRevision = 1, CreatedDate = DateTimeOffset.UtcNow, CreatedBy = actorId,
    };
}
