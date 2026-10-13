using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Workflows.Artifacts.Providers.Local;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.Messages.Commands.Credentials;
using CodeSpace.Messages.Enums;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Shouldly;

namespace CodeSpace.IntegrationTests.Credentials;

/// <summary>
/// A rolling deploy runs the previous build beside this one after 0249 has run, and a rollback runs it alone. That build
/// retires secrets without blanking them: its provider-instance delete marks tokens Revoked and keeps them, its variable
/// delete keeps the ciphertext, its storage revoke keeps every revision, its storage-default rotation keeps the envelope it
/// replaced. 0249 ran once, before those rows existed. The recurring sweep must blank them on its next tick — and touch
/// nothing live.
///
/// <para>Mixed-version, simulated: the previous build's retirements are its own statements, replayed as raw SQL against
/// this build's schema (0249's relaxed guards included), on rows seeded through the real model. The sweep is sent through
/// the real mediator — the pipeline its recurring job uses — and every assertion reads rows this test owns in a fresh
/// scope, never the sweep's global count: this suite shares one database.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class RetiredSecretShredSweepFlowTests
{
    private readonly PostgresFixture _fixture;

    public RetiredSecretShredSweepFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task What_the_previous_build_retires_after_0249_is_shredded_by_the_next_sweep_and_nothing_live_is()
    {
        var world = await SeedAsync();

        await RetireAsThePreviousBuildDoesAsync(world);
        (await PayloadAsync(world.RetiredCredentialId)).ShouldNotBeEmpty("fixture check: the previous build's delete keeps the token");

        await SweepAsync();

        await ShouldHoldNoRetiredSecretAndEveryLiveOneAsync(world);
    }

    [Fact]
    public async Task A_second_sweep_finds_nothing_left_to_change()
    {
        var world = await SeedAsync();
        await RetireAsThePreviousBuildDoesAsync(world);
        await SweepAsync();

        await Should.NotThrowAsync(SweepAsync, "a blanked revision or envelope is no longer a candidate — the guards refuse a second blanking, so a sweep that re-selected one would fail every tick");

        await ShouldHoldNoRetiredSecretAndEveryLiveOneAsync(world);
    }

    private async Task SweepAsync()
    {
        using var scope = _fixture.BeginScope();

        await scope.Resolve<IMediator>().Send(new ShredRetiredSecretsCommand());
    }

    private async Task ShouldHoldNoRetiredSecretAndEveryLiveOneAsync(World world)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        (await PayloadAsync(world.RetiredCredentialId)).ShouldBe(string.Empty, "the previous build's provider delete left the token Revoked and decryptable");
        (await PayloadAsync(world.LiveCredentialId)).ShouldBe("CfDJ8-CANARY-live-token", "a live connection's token is untouched");
        (await db.ProviderInstance.AsNoTracking().Where(p => p.Id == world.DeletedInstanceId).Select(p => p.OauthClientSecretEnc).SingleAsync()).ShouldBeNull("a deleted connection's client secret");
        (await db.ProviderInstance.AsNoTracking().Where(p => p.Id == world.LiveInstanceId).Select(p => p.OauthClientSecretEnc).SingleAsync()).ShouldBe("CfDJ8-CANARY-live-client-secret");

        var deletedHook = await db.ConnectionWebhook.AsNoTracking().SingleAsync(w => w.ProviderInstanceId == world.DeletedInstanceId);
        deletedHook.ShouldSatisfyAllConditions(
            h => h.RegistrationStatus.ShouldBe(RepositoryWebhookRegistrationStatus.Cancelled, "a deleted connection's hook is out of service — ingestion refuses it before it reads a secret"),
            h => h.SecretEnc.ShouldBe(string.Empty),
            h => h.ExternalId.ShouldBe("hook-777", "the provider's hook id stays, as the record of what to remove at the provider by hand"));
        (await db.ConnectionWebhook.AsNoTracking().SingleAsync(w => w.ProviderInstanceId == world.LiveInstanceId)).ShouldSatisfyAllConditions(
            h => h.RegistrationStatus.ShouldBe(RepositoryWebhookRegistrationStatus.Registered),
            h => h.SecretEnc.ShouldBe("CfDJ8-CANARY-live-hook-secret", "a live hook keeps the secret its deliveries are verified against"));

        (await db.Variable.AsNoTracking().SingleAsync(v => v.Id == world.DeletedVariableId)).ValueEncrypted.ShouldBeNull("the previous build's variable delete kept the ciphertext");
        (await db.Variable.AsNoTracking().SingleAsync(v => v.Id == world.LiveVariableId)).ValueEncrypted.ShouldBe(new byte[] { 7, 7, 7, 7 });

        (await RevisionsAsync(db, world.RevokedStorageCredentialId)).ShouldBe(new[] { string.Empty, string.Empty }, ignoreOrder: false, customMessage: "the previous build's storage revoke kept every revision");
        (await RevisionsAsync(db, world.ActiveStorageCredentialId)).ShouldBe(new[] { "CfDJ8-CANARY-active-rev-1", "CfDJ8-CANARY-active-rev-2" }, ignoreOrder: false, customMessage: "an Active credential's superseded revision may be pinned by a profile and is kept");

        (await EnvelopeAsync(db, world.ReplacedEnvelopeId)).ShouldBe(string.Empty, "the previous build's rotation kept the envelope it replaced");
        (await EnvelopeAsync(db, world.RotatedEnvelopeId)).ShouldBe("CfDJ8-CANARY-rotated-envelope", "the envelope the template now names is the one the materializer decrypts");
    }

    /// <summary>
    /// The previous build's retirements, statement for statement: <c>ProviderInstanceService.DeleteAsync</c>'s
    /// cascade-revoke then soft delete, <c>VariableService.DeleteAsync</c>, <c>StorageCredentialService.RevokeAsync</c>,
    /// and <c>StorageDefaultService.UpdateAsync</c> appending a new envelope and repointing the template. None of them
    /// blanks anything. Each must still be accepted by this build's schema.
    /// </summary>
    private async Task RetireAsThePreviousBuildDoesAsync(World world)
    {
        await using var connection = new NpgsqlConnection(_fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            UPDATE credential SET status = 'Revoked', deleted_date = now(), last_error = 'Provider instance deleted' WHERE provider_instance_id = @deleted AND deleted_date IS NULL AND status = 'Active';
            UPDATE provider_instance SET deleted_date = now() WHERE id = @deleted;
            UPDATE variable SET deleted_date = now(), last_modified_date = now(), last_modified_by = @actor WHERE id = @variable;
            UPDATE storage_credential SET state = 'Revoked', revoked_date = now(), revoked_by = @actor WHERE id = @storage;
            INSERT INTO storage_default_credential (id, provider_type_key, encrypted_payload, envelope_fingerprint, created_date, created_by) VALUES (@rotated, @provider, 'CfDJ8-CANARY-rotated-envelope', @fingerprint, now(), @actor);
            UPDATE storage_default SET credential_id = @rotated, revision = revision + 1, last_modified_date = now(), last_modified_by = @actor WHERE id = @template;
            """, connection);

        command.Parameters.AddWithValue("deleted", world.DeletedInstanceId);
        command.Parameters.AddWithValue("actor", world.ActorId);
        command.Parameters.AddWithValue("variable", world.DeletedVariableId);
        command.Parameters.AddWithValue("storage", world.RevokedStorageCredentialId);
        command.Parameters.AddWithValue("rotated", world.RotatedEnvelopeId);
        command.Parameters.AddWithValue("provider", LocalRwxArtifactStorageDriverFactory.TypeKey);
        command.Parameters.AddWithValue("fingerprint", $"sha256:{new string('d', 64)}");
        command.Parameters.AddWithValue("template", world.TemplateId);

        await command.ExecuteNonQueryAsync();
    }

    private sealed record World(Guid ActorId, Guid DeletedInstanceId, Guid LiveInstanceId, Guid RetiredCredentialId, Guid LiveCredentialId, Guid DeletedVariableId, Guid LiveVariableId, Guid RevokedStorageCredentialId, Guid ActiveStorageCredentialId, Guid TemplateId, Guid ReplacedEnvelopeId, Guid RotatedEnvelopeId);

    /// <summary>Everything live, as this build or the previous one creates it: two connections with a token and a group hook each, two Secret variables, two storage credentials rotated once, and a deployment default naming its envelope.</summary>
    private async Task<World> SeedAsync()
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var suffix = Guid.NewGuid().ToString("N")[..10];
        var actor = new User { Id = Guid.NewGuid(), Email = $"sweep-{suffix}@test.local", Name = $"sweep-{suffix}" };
        var team = new Team { Id = Guid.NewGuid(), Slug = $"sweep-{suffix}", Name = "Sweep Team" };

        db.User.Add(actor);
        db.Team.Add(team);
        db.TeamMembership.Add(new TeamMembership { Id = Guid.NewGuid(), TeamId = team.Id, UserId = actor.Id, Role = TeamRole.Owner });

        var deletedInstance = Instance(team.Id, $"https://gone-{suffix}.example", "CfDJ8-CANARY-deleted-client-secret");
        var liveInstance = Instance(team.Id, $"https://live-{suffix}.example", "CfDJ8-CANARY-live-client-secret");
        var retired = Token(team.Id, deletedInstance.Id, "CfDJ8-CANARY-retired-token");
        var live = Token(team.Id, liveInstance.Id, "CfDJ8-CANARY-live-token");

        db.ProviderInstance.AddRange(deletedInstance, liveInstance);
        db.Credential.AddRange(retired, live);

        var deletedVariable = SecretVariable(team.Id, "DELETED_SECRET", new byte[] { 1, 2, 3, 4 });
        var liveVariable = SecretVariable(team.Id, "LIVE_SECRET", new byte[] { 7, 7, 7, 7 });

        db.Variable.AddRange(deletedVariable, liveVariable);
        await db.SaveChangesAsync();

        db.ConnectionWebhook.AddRange(Hook(deletedInstance.Id, retired.Id, "hook-777", "CfDJ8-CANARY-deleted-hook-secret"), Hook(liveInstance.Id, live.Id, "hook-888", "CfDJ8-CANARY-live-hook-secret"));

        var revoked = await StorageCredentialAsync(db, team.Id, actor.Id, "revoked");
        var active = await StorageCredentialAsync(db, team.Id, actor.Id, "active");
        var replaced = new StorageDefaultCredential { Id = Guid.NewGuid(), ProviderTypeKey = LocalRwxArtifactStorageDriverFactory.TypeKey, EncryptedPayload = "CfDJ8-CANARY-replaced-envelope", EnvelopeFingerprint = $"sha256:{new string('c', 64)}", CreatedDate = DateTimeOffset.UtcNow, CreatedBy = actor.Id };
        var template = Template(actor.Id, replaced.Id);

        db.StorageDefaultCredential.Add(replaced);
        db.StorageDefault.Add(template);
        await db.SaveChangesAsync();

        return new World(actor.Id, deletedInstance.Id, liveInstance.Id, retired.Id, live.Id, deletedVariable.Id, liveVariable.Id, revoked.Id, active.Id, template.Id, replaced.Id, Guid.NewGuid());
    }

    private static ProviderInstance Instance(Guid teamId, string baseUrl, string clientSecretEnc) => new()
    {
        Id = Guid.NewGuid(), TeamId = teamId, Provider = ProviderKind.GitLab, DisplayName = "gitlab", BaseUrl = baseUrl, OauthClientId = "client", OauthClientSecretEnc = clientSecretEnc, WebhookScope = ProviderWebhookScope.Connection
    };

    private static Credential Token(Guid teamId, Guid instanceId, string payload) => new()
    {
        Id = Guid.NewGuid(), TeamId = teamId, ProviderInstanceId = instanceId, Ownership = CredentialOwnership.TeamService, AuthType = AuthType.OAuth, DisplayName = "token", EncryptedPayload = payload, Status = CredentialStatus.Active
    };

    private static Variable SecretVariable(Guid teamId, string name, byte[] ciphertext) => new()
    {
        Id = Guid.NewGuid(), Scope = VariableScope.Team, ScopeId = teamId, TeamId = teamId, Name = name, ValueType = VariableValueType.Secret, ValueEncrypted = ciphertext
    };

    private static ConnectionWebhook Hook(Guid instanceId, Guid credentialId, string externalId, string secretEnc)
    {
        var id = Guid.NewGuid();

        return new ConnectionWebhook
        {
            Id = id, ProviderInstanceId = instanceId, CredentialId = credentialId, OwnerPath = "acme", ExternalId = externalId, CallbackUrl = $"https://codespace.test/api/webhooks/connection/{id}",
            SecretEnc = secretEnc, SubscribedEvents = new List<string> { "push" }, RegistrationStatus = RepositoryWebhookRegistrationStatus.Registered
        };
    }

    /// <summary>A template on a data class of its own — a data class is instance-wide, so this test claims none another test uses.</summary>
    private static StorageDefault Template(Guid actorId, Guid credentialId) => new()
    {
        Id = Guid.NewGuid(), DataClassTypeKey = $"t{Guid.NewGuid():N}/v1", Revision = 1, ProviderTypeKey = LocalRwxArtifactStorageDriverFactory.TypeKey, NonSecretConfigJson = "{}", NamespaceRoot = "codespace-defaults/",
        CredentialId = credentialId, AdoptionPolicy = StorageDefaultAdoptionPolicy.Explicit, IsEnabled = true, CreatedDate = DateTimeOffset.UtcNow, CreatedBy = actorId, LastModifiedDate = DateTimeOffset.UtcNow, LastModifiedBy = actorId
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
        EncryptedPayload = payload, EnvelopeFingerprint = $"sha256:{new string((char)('a' + revision), 64)}", CreatedDate = DateTimeOffset.UtcNow, CreatedBy = actorId
    };

    private async Task<string> PayloadAsync(Guid credentialId)
    {
        using var scope = _fixture.BeginScope();

        return await scope.Resolve<CodeSpaceDbContext>().Credential.AsNoTracking().Where(c => c.Id == credentialId).Select(c => c.EncryptedPayload).SingleAsync();
    }

    private static async Task<List<string>> RevisionsAsync(CodeSpaceDbContext db, Guid credentialId) =>
        await db.StorageCredentialRevision.AsNoTracking().Where(r => r.StorageCredentialId == credentialId).OrderBy(r => r.Revision).Select(r => r.EncryptedPayload).ToListAsync();

    private static async Task<string> EnvelopeAsync(CodeSpaceDbContext db, Guid envelopeId) =>
        await db.StorageDefaultCredential.AsNoTracking().Where(e => e.Id == envelopeId).Select(e => e.EncryptedPayload).SingleAsync();
}
