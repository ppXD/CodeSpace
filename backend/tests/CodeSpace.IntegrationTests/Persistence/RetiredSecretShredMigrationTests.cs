using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Credentials;
using CodeSpace.Core.Services.Workflows.Artifacts.Providers.Local;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.Messages.Enums;
using DbUp;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Shouldly;

namespace CodeSpace.IntegrationTests.Persistence;

/// <summary>
/// 0249 over a database that already holds what retirement used to leave behind: a deleted connection's Revoked tokens,
/// OAuth client secret and group-hook secrets, a deleted Secret variable's ciphertext, every revision of a revoked storage
/// credential, and the deployment-default envelopes a rotation or a clear left unnamed — all still decryptable. The
/// upgrade blanks exactly those and nothing live, relaxes the three schema guards that forbade it, and must not refuse:
/// a migration that throws is a deployment that does not start.
///
/// <para>Driven through the production <see cref="DbUpRunner"/> over its own database, migrated up to (not including)
/// 0249 and seeded through the real model — the schema a deployment upgrading across 0249 meets. Dropped on dispose; the
/// shared test database is never touched.</para>
///
/// <para>0249 runs once. The same rows written after it — by a build older than 0249 still serving, or after a rollback —
/// are the recurring sweep's (<see cref="RetiredSecretShredService"/>), and it is held to the same assertions.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class RetiredSecretShredMigrationTests
{
    private const string Shredding = "0249";

    private readonly PostgresFixture _fixture;

    public RetiredSecretShredMigrationTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task Upgrading_shreds_what_retirement_left_behind_and_leaves_every_live_secret_alone()
    {
        await using var database = await PreUpgradeDatabase.CreateAsync(_fixture.ConnectionString, Shredding);
        var world = await SeedAsync(database.ConnectionString);

        new DbUpRunner(database.ConnectionString).Run();

        await using var db = Context(database.ConnectionString);

        await ShouldHoldNoRetiredSecretAndEveryLiveOneAsync(db, world);
    }

    /// <summary>
    /// The same rows written AFTER the upgrade — what a build older than 0249 leaves when it retires something while it
    /// still serves during a rolling deploy, or after a rollback. 0249 has already run and does not run again; the
    /// recurring sweep must shred exactly what it would have. Same seed, same assertions as the upgrade above, so the two
    /// cannot drift apart on this corpus: a kind or a predicate the sweep misses, or one it over-reaches, fails here.
    /// </summary>
    [Fact]
    public async Task The_sweep_shreds_what_an_older_build_retires_after_the_upgrade_exactly_as_the_upgrade_does()
    {
        await using var database = await PreUpgradeDatabase.CreateAsync(_fixture.ConnectionString, Shredding);

        new DbUpRunner(database.ConnectionString).Run();

        var world = await SeedAsync(database.ConnectionString);

        await using (var sweep = Context(database.ConnectionString))
        {
            (await new RetiredSecretShredService(sweep, NullLogger<RetiredSecretShredService>.Instance).ShredAsync(CancellationToken.None)).ShouldBeGreaterThan(0, "fixture check: the older build's retirements were written after 0249 and left their ciphertext");
        }

        await using var db = Context(database.ConnectionString);

        await ShouldHoldNoRetiredSecretAndEveryLiveOneAsync(db, world);
    }

    private static async Task ShouldHoldNoRetiredSecretAndEveryLiveOneAsync(CodeSpaceDbContext db, World world)
    {
        (await PayloadAsync(db, world.RetiredCredentialId)).ShouldBe(string.Empty, "a deleted connection's Revoked token must not stay decryptable");
        (await PayloadAsync(db, world.SoftDeletedCredentialId)).ShouldBe(string.Empty, "a soft-deleted token is out of every list and every read; nothing presents it again");
        (await PayloadAsync(db, world.DisconnectedCredentialId)).ShouldBe(string.Empty, "a Revoked token on a live connection is no more usable than one on a deleted connection");
        (await PayloadAsync(db, world.LiveCredentialId)).ShouldBe("CfDJ8-CANARY-live-token", "a live token is untouched");
        (await ClientSecretAsync(db, world.DeletedInstanceId)).ShouldBeNull("a deleted connection's OAuth client secret must not stay decryptable");
        (await ClientSecretAsync(db, world.LiveInstanceId)).ShouldBe("CfDJ8-CANARY-live-client-secret", "a live connection keeps its client secret");

        (await VariableAsync(db, world.DeletedSecretVariableId)).ValueEncrypted.ShouldBeNull("a deleted Secret variable's ciphertext must not survive the upgrade");
        (await VariableAsync(db, world.LiveSecretVariableId)).ValueEncrypted.ShouldBe(world.LiveCiphertext, "a live Secret keeps its ciphertext byte for byte");
        (await VariableAsync(db, world.DeletedPlainVariableId)).ValuePlain.ShouldBe("\"plain-kept\"", "a plain value is no secret and stays");

        (await RevisionPayloadsAsync(db, world.RevokedStorageCredentialId)).ShouldBe(new[] { string.Empty, string.Empty }, ignoreOrder: false, customMessage: "every revision of a revoked storage credential is blanked");
        (await RevisionPayloadsAsync(db, world.ActiveStorageCredentialId)).ShouldBe(new[] { "CfDJ8-CANARY-active-rev-1", "CfDJ8-CANARY-active-rev-2" }, ignoreOrder: false, customMessage: "an Active credential's revisions — the superseded one too, which a profile may pin — are untouched");

        var deletedHooks = await HooksAsync(db, world.DeletedInstanceId);
        deletedHooks.Count.ShouldBe(3, "fixture check: a Registered, a Pending and a Cancelled hook were seeded on the deleted connection");
        deletedHooks.ShouldAllBe(h => h.RegistrationStatus == RepositoryWebhookRegistrationStatus.Cancelled && h.SecretEnc == string.Empty, "a deleted connection's group hooks keep no secret, and none stays in service with a secret it no longer holds");
        deletedHooks.Single(h => h.OwnerPath == "acme").ExternalId.ShouldBe("hook-777", "the provider's hook id stays: it is what an operator removes by hand, since no token is left to remove it");
        (await HooksAsync(db, world.LiveInstanceId)).ShouldHaveSingleItem().ShouldSatisfyAllConditions(
            h => h.RegistrationStatus.ShouldBe(RepositoryWebhookRegistrationStatus.Registered, "a live connection's hook stays in service"),
            h => h.SecretEnc.ShouldBe("CfDJ8-CANARY-live-hook-secret", "and keeps the secret its deliveries are verified against"));

        (await EnvelopeAsync(db, world.SupersededEnvelopeId)).ShouldBe(string.Empty, "a deployment-default envelope no template names has no reader left");
        (await EnvelopeAsync(db, world.NamedEnvelopeId)).ShouldBe("CfDJ8-CANARY-named-envelope", "the envelope a template names is the one the materializer decrypts");
    }

    [Fact]
    public async Task After_the_upgrade_the_guards_still_refuse_everything_but_a_retirement()
    {
        await using var database = await PreUpgradeDatabase.CreateAsync(_fixture.ConnectionString, Shredding);
        var world = await SeedAsync(database.ConnectionString);

        new DbUpRunner(database.ConnectionString).Run();

        (await RefusalAsync(database.ConnectionString, $"UPDATE storage_credential_revision SET encrypted_payload = '' WHERE storage_credential_id = '{world.ActiveStorageCredentialId}' AND revision = 1")).ShouldContain("Revoked");
        (await RefusalAsync(database.ConnectionString, $"UPDATE storage_credential_revision SET envelope_fingerprint = 'sha256:{new string('f', 64)}' WHERE storage_credential_id = '{world.RevokedStorageCredentialId}'")).ShouldContain("immutable");
        (await RefusalAsync(database.ConnectionString, $"UPDATE variable SET value_encrypted = NULL WHERE id = '{world.LiveSecretVariableId}'")).ShouldContain("chk_variable_value_exclusive");
        (await RefusalAsync(database.ConnectionString, $"UPDATE storage_default_credential SET encrypted_payload = '' WHERE id = '{world.NamedEnvelopeId}'")).ShouldContain("no template names it");
        (await RefusalAsync(database.ConnectionString, $"UPDATE storage_default_credential SET safe_hint = 'rewritten' WHERE id = '{world.SupersededEnvelopeId}'")).ShouldContain("append-only");
        (await RefusalAsync(database.ConnectionString, $"DELETE FROM storage_default_credential WHERE id = '{world.SupersededEnvelopeId}'")).ShouldContain("append-only");
        (await RefusalAsync(database.ConnectionString, $"INSERT INTO storage_default_credential (id, provider_type_key, encrypted_payload, envelope_fingerprint, created_date, created_by) SELECT gen_random_uuid(), provider_type_key, '', envelope_fingerprint, now(), created_by FROM storage_default_credential WHERE id = '{world.NamedEnvelopeId}'")).ShouldContain("payload is required");
    }

    /// <summary>
    /// The rollback the script's header documents, run as written on a database the script has shredded. The old checks
    /// reject exactly the rows a shred leaves — a deleted Secret with no ciphertext, a blank revision or envelope — so a
    /// note that restored them as they were would fail with 23514 during the very incident it is followed in. The
    /// statements are read out of the header itself, so the note cannot drift from what this proves.
    /// </summary>
    [Fact]
    public async Task The_documented_rollback_runs_on_a_shredded_database_and_restores_the_old_rules_for_new_rows()
    {
        await using var database = await PreUpgradeDatabase.CreateAsync(_fixture.ConnectionString, Shredding);
        var world = await SeedAsync(database.ConnectionString);

        new DbUpRunner(database.ConnectionString).Run();

        var rollback = RollbackScript();

        rollback.ShouldContain("NOT VALID", customMessage: "fixture check: the statements were read out of 0249's header");

        await ExecuteAsync(database.ConnectionString, rollback);

        (await RefusalAsync(database.ConnectionString, $"UPDATE storage_credential_revision SET encrypted_payload = '' WHERE storage_credential_id = '{world.ActiveStorageCredentialId}' AND revision = 1")).ShouldContain("immutable");
        (await RefusalAsync(database.ConnectionString, $"UPDATE storage_default_credential SET encrypted_payload = 'x' WHERE id = '{world.NamedEnvelopeId}'")).ShouldContain("append-only");
        (await RefusalAsync(database.ConnectionString, $"UPDATE variable SET value_encrypted = NULL, deleted_date = now() WHERE id = '{world.LiveSecretVariableId}'")).ShouldContain("chk_variable_value_exclusive");
    }

    /// <summary>
    /// Rolling forward after that rollback, the way the header says: drop the script's journal row and start again. With
    /// the old checks back, this build could not retire a secret — a Secret variable's delete would fail with 23514 and
    /// the recurring sweep would fail every tick — so the script has to apply a second time over its own shredded rows,
    /// and admit the application's blanking again.
    /// </summary>
    [Fact]
    public async Task Rolling_forward_after_the_documented_rollback_applies_the_script_again_and_retirement_blanks_again()
    {
        await using var database = await PreUpgradeDatabase.CreateAsync(_fixture.ConnectionString, Shredding);
        var world = await SeedAsync(database.ConnectionString);

        new DbUpRunner(database.ConnectionString).Run();
        await ExecuteAsync(database.ConnectionString, RollbackScript());

        var rollForward = HeaderStatements("DELETE FROM schemaversions ").ShouldHaveSingleItem("fixture check: the roll-forward statement was read out of 0249's header");

        await ExecuteAsync(database.ConnectionString, rollForward);
        new DbUpRunner(database.ConnectionString).Run();

        await ExecuteAsync(database.ConnectionString, $"UPDATE variable SET value_encrypted = NULL, deleted_date = now() WHERE id = '{world.LiveSecretVariableId}'");

        await using var db = Context(database.ConnectionString);

        (await VariableAsync(db, world.LiveSecretVariableId)).ValueEncrypted.ShouldBeNull("a Secret variable's delete blanks it again, as VariableRetirement does");
        (await new RetiredSecretShredService(db, NullLogger<RetiredSecretShredService>.Instance).ShredAsync(CancellationToken.None)).ShouldBe(0, "the second run's backfill found the rows the first left blank, and the sweep has nothing left to do");
    }

    /// <summary>
    /// 0249's rollback: the statements its header lists, then 0126's and 0173's guard functions verbatim, as the header
    /// says to.
    /// </summary>
    private static string RollbackScript()
    {
        var statements = HeaderStatements("ALTER TABLE ", "DROP TRIGGER ", "CREATE TRIGGER ");

        return string.Join("\n", statements.Append(FunctionBlock("0126_", "storage_credential_revision_guard")).Append(FunctionBlock("0173_", "storage_default_credential_guard")));
    }

    /// <summary>The statements 0249's header comment lists that start with one of <paramref name="starts"/>.</summary>
    private static IReadOnlyList<string> HeaderStatements(params string[] starts) =>
        File.ReadLines(Script("0249_"))
            .TakeWhile(line => line.StartsWith("--", StringComparison.Ordinal))
            .Select(line => line.Length > 2 ? line[2..].Trim() : string.Empty)
            .Where(line => starts.Any(start => line.StartsWith(start, StringComparison.Ordinal)))
            .ToList();

    private static string FunctionBlock(string scriptPrefix, string function)
    {
        var script = File.ReadAllText(Script(scriptPrefix));
        var start = script.IndexOf($"CREATE OR REPLACE FUNCTION {function}()", StringComparison.Ordinal);
        const string end = "$$ LANGUAGE plpgsql;";

        start.ShouldBeGreaterThanOrEqualTo(0, $"fixture check: {scriptPrefix} defines {function}");

        return script[start..(script.IndexOf(end, start, StringComparison.Ordinal) + end.Length)];
    }

    private static string Script(string prefix) =>
        Directory.GetFiles(Path.Combine(Path.GetDirectoryName(typeof(DbUpRunner).Assembly.Location)!, DbUpRunner.ScriptFolder), $"{prefix}*.sql").ShouldHaveSingleItem();

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);

        await command.ExecuteNonQueryAsync();
    }

    private static async Task<string> PayloadAsync(CodeSpaceDbContext db, Guid credentialId) =>
        await db.Credential.AsNoTracking().Where(c => c.Id == credentialId).Select(c => c.EncryptedPayload).SingleAsync();

    private static async Task<string?> ClientSecretAsync(CodeSpaceDbContext db, Guid instanceId) =>
        await db.ProviderInstance.AsNoTracking().Where(p => p.Id == instanceId).Select(p => p.OauthClientSecretEnc).SingleAsync();

    private static async Task<Variable> VariableAsync(CodeSpaceDbContext db, Guid variableId) =>
        await db.Variable.AsNoTracking().SingleAsync(v => v.Id == variableId);

    private static async Task<List<string>> RevisionPayloadsAsync(CodeSpaceDbContext db, Guid credentialId) =>
        await db.StorageCredentialRevision.AsNoTracking().Where(r => r.StorageCredentialId == credentialId).OrderBy(r => r.Revision).Select(r => r.EncryptedPayload).ToListAsync();

    private static async Task<List<ConnectionWebhook>> HooksAsync(CodeSpaceDbContext db, Guid instanceId) =>
        await db.ConnectionWebhook.AsNoTracking().Where(w => w.ProviderInstanceId == instanceId).ToListAsync();

    private static async Task<string> EnvelopeAsync(CodeSpaceDbContext db, Guid envelopeId) =>
        await db.StorageDefaultCredential.AsNoTracking().Where(e => e.Id == envelopeId).Select(e => e.EncryptedPayload).SingleAsync();

    private static async Task<string> RefusalAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);

        var refused = await Should.ThrowAsync<PostgresException>(() => command.ExecuteNonQueryAsync());

        return $"{refused.ConstraintName} {refused.MessageText}";
    }

    private sealed record World(Guid DeletedInstanceId, Guid LiveInstanceId, Guid RetiredCredentialId, Guid SoftDeletedCredentialId, Guid DisconnectedCredentialId, Guid LiveCredentialId, Guid DeletedSecretVariableId, Guid LiveSecretVariableId, Guid DeletedPlainVariableId, byte[] LiveCiphertext, Guid RevokedStorageCredentialId, Guid ActiveStorageCredentialId, Guid SupersededEnvelopeId, Guid NamedEnvelopeId);

    /// <summary>
    /// The rows the pre-0249 code leaves: what its provider-instance delete, variable delete, storage revoke and
    /// deployment-default rotation write, beside live ones of each kind.
    /// </summary>
    private static async Task<World> SeedAsync(string connectionString)
    {
        await using var db = Context(connectionString);
        var now = DateTimeOffset.UtcNow;
        var suffix = Guid.NewGuid().ToString("N")[..10];
        var actor = new User { Id = Guid.NewGuid(), Email = $"shred-{suffix}@test.local", Name = $"shred-{suffix}" };
        var team = new Team { Id = Guid.NewGuid(), Slug = $"shred-{suffix}", Name = "Shred Team", Kind = TeamKind.Workspace };

        db.User.Add(actor);
        db.Team.Add(team);
        db.TeamMembership.Add(new TeamMembership { Id = Guid.NewGuid(), TeamId = team.Id, UserId = actor.Id, Role = TeamRole.Owner });

        var deletedInstance = new ProviderInstance { Id = Guid.NewGuid(), TeamId = team.Id, Provider = ProviderKind.GitLab, DisplayName = "gone", BaseUrl = $"https://gone-{suffix}.example", OauthClientId = "client", OauthClientSecretEnc = "CfDJ8-CANARY-deleted-client-secret", DeletedDate = now };
        var liveInstance = new ProviderInstance { Id = Guid.NewGuid(), TeamId = team.Id, Provider = ProviderKind.GitLab, DisplayName = "live", BaseUrl = $"https://live-{suffix}.example", OauthClientId = "client", OauthClientSecretEnc = "CfDJ8-CANARY-live-client-secret" };
        var retired = Token(team.Id, deletedInstance.Id, "CfDJ8-CANARY-retired-token", CredentialStatus.Revoked, now);
        var softDeleted = Token(team.Id, liveInstance.Id, "CfDJ8-CANARY-soft-deleted-token", CredentialStatus.Active, now);
        var disconnected = Token(team.Id, liveInstance.Id, "CfDJ8-CANARY-disconnected-token", CredentialStatus.Revoked, deletedDate: null);
        var live = Token(team.Id, liveInstance.Id, "CfDJ8-CANARY-live-token", CredentialStatus.Active, deletedDate: null);

        db.ProviderInstance.AddRange(deletedInstance, liveInstance);
        db.Credential.AddRange(retired, softDeleted, disconnected, live);

        var liveCiphertext = new byte[] { 7, 7, 7, 7 };
        var deletedSecret = Variable(team.Id, "DELETED_SECRET", VariableValueType.Secret, plain: null, encrypted: new byte[] { 1, 2, 3, 4 }, now);
        var liveSecret = Variable(team.Id, "LIVE_SECRET", VariableValueType.Secret, plain: null, encrypted: liveCiphertext, deletedDate: null);
        var deletedPlain = Variable(team.Id, "DELETED_PLAIN", VariableValueType.String, plain: "\"plain-kept\"", encrypted: null, now);

        db.Variable.AddRange(deletedSecret, liveSecret, deletedPlain);
        await db.SaveChangesAsync();

        var revoked = await StorageCredentialAsync(db, team.Id, actor.Id, "revoked");
        var active = await StorageCredentialAsync(db, team.Id, actor.Id, "active");

        revoked.State = StorageCredentialState.Revoked;
        revoked.RevokedDate = now;
        revoked.RevokedBy = actor.Id;

        db.ConnectionWebhook.AddRange(
            Hook(deletedInstance.Id, retired.Id, "acme", RepositoryWebhookRegistrationStatus.Registered, "hook-777", "CfDJ8-CANARY-deleted-hook-secret"),
            Hook(deletedInstance.Id, retired.Id, "acme/web", RepositoryWebhookRegistrationStatus.Pending, externalId: null, "CfDJ8-CANARY-pending-hook-secret"),
            Hook(deletedInstance.Id, retired.Id, "acme/old", RepositoryWebhookRegistrationStatus.Cancelled, externalId: null, "CfDJ8-CANARY-cancelled-hook-secret"),
            Hook(liveInstance.Id, live.Id, "acme", RepositoryWebhookRegistrationStatus.Registered, "hook-888", "CfDJ8-CANARY-live-hook-secret"));

        var superseded = Envelope(actor.Id, "CfDJ8-CANARY-superseded-envelope");
        var named = Envelope(actor.Id, "CfDJ8-CANARY-named-envelope");

        db.StorageDefaultCredential.AddRange(superseded, named);
        db.StorageDefault.Add(Template(actor.Id, named.Id, now));
        await db.SaveChangesAsync();

        return new World(deletedInstance.Id, liveInstance.Id, retired.Id, softDeleted.Id, disconnected.Id, live.Id, deletedSecret.Id, liveSecret.Id, deletedPlain.Id, liveCiphertext, revoked.Id, active.Id, superseded.Id, named.Id);
    }

    private static ConnectionWebhook Hook(Guid instanceId, Guid credentialId, string ownerPath, RepositoryWebhookRegistrationStatus status, string? externalId, string secretEnc)
    {
        var id = Guid.NewGuid();

        return new ConnectionWebhook
        {
            Id = id, ProviderInstanceId = instanceId, CredentialId = credentialId, OwnerPath = ownerPath, ExternalId = externalId, CallbackUrl = $"https://codespace.test/api/webhooks/connection/{id}",
            SecretEnc = secretEnc, SubscribedEvents = new List<string> { "push" }, RegistrationStatus = status,
        };
    }

    private static StorageDefaultCredential Envelope(Guid actorId, string payload) => new()
    {
        Id = Guid.NewGuid(), ProviderTypeKey = LocalRwxArtifactStorageDriverFactory.TypeKey, EncryptedPayload = payload, EnvelopeFingerprint = $"sha256:{new string('c', 64)}", CreatedDate = DateTimeOffset.UtcNow, CreatedBy = actorId,
    };

    /// <summary>A template on a key of its own — a data class is instance-wide, so the shred test claims none another test uses.</summary>
    private static StorageDefault Template(Guid actorId, Guid credentialId, DateTimeOffset now) => new()
    {
        Id = Guid.NewGuid(), DataClassTypeKey = $"t{Guid.NewGuid():N}/v1", Revision = 2, ProviderTypeKey = LocalRwxArtifactStorageDriverFactory.TypeKey, NonSecretConfigJson = "{}", NamespaceRoot = "codespace-defaults/",
        CredentialId = credentialId, AdoptionPolicy = StorageDefaultAdoptionPolicy.Explicit, IsEnabled = true, CreatedDate = now, CreatedBy = actorId, LastModifiedDate = now, LastModifiedBy = actorId,
    };

    private static Credential Token(Guid teamId, Guid instanceId, string payload, CredentialStatus status, DateTimeOffset? deletedDate) => new()
    {
        Id = Guid.NewGuid(), TeamId = teamId, ProviderInstanceId = instanceId, Ownership = CredentialOwnership.TeamService, AuthType = AuthType.OAuth,
        DisplayName = "token", EncryptedPayload = payload, Status = status, DeletedDate = deletedDate,
    };

    private static Variable Variable(Guid teamId, string name, VariableValueType type, string? plain, byte[]? encrypted, DateTimeOffset? deletedDate) => new()
    {
        Id = Guid.NewGuid(), Scope = VariableScope.Team, ScopeId = teamId, TeamId = teamId, Name = name, ValueType = type, ValuePlain = plain, ValueEncrypted = encrypted, DeletedDate = deletedDate,
    };

    /// <summary>A storage credential rotated once, through the append-only path its guard admits.</summary>
    private static async Task<StorageCredential> StorageCredentialAsync(CodeSpaceDbContext db, Guid teamId, Guid actorId, string name)
    {
        var credential = new StorageCredential { Id = Guid.NewGuid(), TeamId = teamId, StableName = $"{name}-{Guid.NewGuid():N}", CurrentRevision = 1, State = StorageCredentialState.Active, CreatedDate = DateTimeOffset.UtcNow, CreatedBy = actorId };
        credential.Revisions.Add(StorageRevision(credential, actorId, 1, $"CfDJ8-CANARY-{name}-rev-1"));
        db.StorageCredential.Add(credential);
        await db.SaveChangesAsync();

        db.StorageCredentialRevision.Add(StorageRevision(credential, actorId, 2, $"CfDJ8-CANARY-{name}-rev-2"));
        credential.CurrentRevision = 2;
        await db.SaveChangesAsync();

        return credential;
    }

    private static StorageCredentialRevision StorageRevision(StorageCredential credential, Guid actorId, int revision, string payload) => new()
    {
        Id = Guid.NewGuid(), TeamId = credential.TeamId, StorageCredentialId = credential.Id, Revision = revision, ProviderTypeKey = LocalRwxArtifactStorageDriverFactory.TypeKey,
        EncryptedPayload = payload, EnvelopeFingerprint = $"sha256:{new string((char)('a' + revision), 64)}", CreatedDate = DateTimeOffset.UtcNow, CreatedBy = actorId,
    };

    private static CodeSpaceDbContext Context(string connectionString) =>
        new(new DbContextOptionsBuilder<CodeSpaceDbContext>().UseNpgsql(connectionString).UseSnakeCaseNamingConvention().Options);

    /// <summary>
    /// Its own database, migrated only up to (not including) <c>firstPendingScript</c>, so the runner meets exactly the
    /// schema a deployment upgrading across that script meets. The name carries a GUID; dropped on dispose.
    /// </summary>
    private sealed class PreUpgradeDatabase : IAsyncDisposable
    {
        private readonly string _name;

        private PreUpgradeDatabase(string connectionString, string name) { ConnectionString = connectionString; _name = name; }

        public string ConnectionString { get; }

        public static async Task<PreUpgradeDatabase> CreateAsync(string sharedConnectionString, string firstPendingScript)
        {
            var name = $"codespace_shred_{Guid.NewGuid():N}";
            var database = new PreUpgradeDatabase(new NpgsqlConnectionStringBuilder(sharedConnectionString) { Database = name }.ConnectionString, name);

            await database.AdminAsync($"CREATE DATABASE \"{name}\"");

            try
            {
                var scripts = Path.Combine(Path.GetDirectoryName(typeof(DbUpRunner).Assembly.Location)!, DbUpRunner.ScriptFolder);
                var upgrade = DeployChanges.To.PostgresqlDatabase(database.ConnectionString)
                    .WithScriptsFromFileSystem(scripts, path => string.CompareOrdinal(Path.GetFileName(path)[..4], firstPendingScript) < 0)
                    .WithVariablesDisabled().WithTransaction().LogToNowhere().Build().PerformUpgrade();

                if (!upgrade.Successful) throw upgrade.Error;

                return database;
            }
            catch
            {
                await database.DisposeAsync();
                throw;
            }
        }

        public async ValueTask DisposeAsync() => await AdminAsync($"DROP DATABASE IF EXISTS \"{_name}\" WITH (FORCE)");

        private async Task AdminAsync(string sql)
        {
            await using var admin = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(ConnectionString) { Database = "postgres" }.ConnectionString);
            await admin.OpenAsync();
            await using var command = new NpgsqlCommand(sql, admin);
            await command.ExecuteNonQueryAsync();
        }
    }
}
