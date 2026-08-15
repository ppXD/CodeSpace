using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Credentials;
using CodeSpace.Core.Services.Workflows.Artifacts.Credentials;
using CodeSpace.Core.Services.Workflows.Artifacts.Providers;
using CodeSpace.Core.Services.Workflows.Artifacts.Providers.Local;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows.Artifacts.Credentials;

[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class StorageCredentialSecretResolverTests
{
    private const string ProviderTypeKey = "test-secret-store/v1";
    private static readonly IStorageProviderModule Module = new TestStorageProviderModule(ProviderTypeKey, Schema("""
        {
          "type": "object",
          "properties": {
            "accessKey": { "type": "string", "minLength": 3 },
            "region": { "type": "string" }
          },
          "required": ["accessKey"],
          "additionalProperties": false
        }
        """));
    private readonly PostgresFixture _fixture;

    public StorageCredentialSecretResolverTests(PostgresFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Ready_returns_an_independent_schema_validated_object_without_tracking()
    {
        var (teamId, actorId) = await SeedTeamAsync();
        var credential = await SeedCredentialAsync(teamId, actorId, ProviderTypeKey, ["""{"region":"cn-hangzhou","accessKey":"secret-v1"}"""]);

        using var scope = ResolverScope(Module);
        var result = await scope.Resolve<IStorageCredentialSecretResolver>().ResolveAsync(Request(teamId, credential.Id, 1), CancellationToken.None);

        using var ready = result.ShouldBeOfType<StorageCredentialSecretResolution.Ready>();
        ready.UseSecret(secret => secret.ValueKind).ShouldBe(JsonValueKind.Object);
        ready.UseSecret(secret => secret.GetProperty("accessKey").GetString()).ShouldBe("secret-v1");
        ready.UseSecret(secret => secret.GetProperty("region").GetString()).ShouldBe("cn-hangzhou");
        ready.ToString().ShouldNotContain("secret-v1", Case.Sensitive);
        ready.ToString().ShouldContain("REDACTED", Case.Sensitive);
        scope.Resolve<CodeSpaceDbContext>().ChangeTracker.Entries().ShouldBeEmpty();
    }

    [Fact]
    public async Task Exact_old_revision_never_falls_forward_to_the_current_secret()
    {
        var (teamId, actorId) = await SeedTeamAsync();
        var credential = await SeedCredentialAsync(teamId, actorId, ProviderTypeKey, ["""{"accessKey":"old-key"}""", """{"accessKey":"new-key"}"""]);

        using var old = (await ResolveAsync(Request(teamId, credential.Id, 1))).ShouldBeOfType<StorageCredentialSecretResolution.Ready>();
        using var current = (await ResolveAsync(Request(teamId, credential.Id, 2))).ShouldBeOfType<StorageCredentialSecretResolution.Ready>();

        old.UseSecret(secret => secret.GetProperty("accessKey").GetString()).ShouldBe("old-key");
        current.UseSecret(secret => secret.GetProperty("accessKey").GetString()).ShouldBe("new-key");
    }

    [Fact]
    public async Task Foreign_identity_is_indistinguishable_from_missing_and_never_reaches_decryption()
    {
        var (ownerTeamId, actorId) = await SeedTeamAsync();
        var (callerTeamId, _) = await SeedTeamAsync();
        var credential = await SeedCredentialAsync(ownerTeamId, actorId, ProviderTypeKey, ["""{"accessKey":"never-read"}"""]);
        var encryptor = new RecordingRejectingEncryptor();

        using var scope = ResolverScope(Module, encryptor);
        var foreign = await scope.Resolve<IStorageCredentialSecretResolver>().ResolveAsync(Request(callerTeamId, credential.Id, 1), CancellationToken.None);
        var missing = await scope.Resolve<IStorageCredentialSecretResolver>().ResolveAsync(Request(callerTeamId, Guid.NewGuid(), 1), CancellationToken.None);

        foreign.ShouldBe(new StorageCredentialSecretResolution.Missing());
        missing.ShouldBe(new StorageCredentialSecretResolution.Missing());
        encryptor.DecryptCalls.ShouldBe(0);
    }

    [Fact]
    public async Task Missing_revoked_revision_missing_and_provider_mismatch_are_distinct_fail_closed_values()
    {
        var (teamId, actorId) = await SeedTeamAsync();
        var active = await SeedCredentialAsync(teamId, actorId, ProviderTypeKey, ["""{"accessKey":"active-key"}"""]);
        var revoked = await SeedCredentialAsync(teamId, actorId, ProviderTypeKey, ["""{"accessKey":"revoked-key"}"""], StorageCredentialState.Revoked);
        var wrongProvider = await SeedCredentialAsync(teamId, actorId, "other-secret-store/v1", ["""{"accessKey":"wrong-key"}"""]);

        (await ResolveAsync(Request(teamId, Guid.NewGuid(), 1))).ShouldBe(new StorageCredentialSecretResolution.Missing());
        (await ResolveAsync(Request(teamId, revoked.Id, 1))).ShouldBe(new StorageCredentialSecretResolution.NotActive(StorageCredentialState.Revoked));
        (await ResolveAsync(Request(teamId, active.Id, 2))).ShouldBe(new StorageCredentialSecretResolution.RevisionMissing());
        (await ResolveAsync(Request(teamId, wrongProvider.Id, 1))).ShouldBe(new StorageCredentialSecretResolution.ProviderMismatch());
    }

    [Fact]
    public async Task Missing_module_and_malformed_current_schema_are_provider_unavailable_without_decryption()
    {
        var (teamId, actorId) = await SeedTeamAsync();
        var credential = await SeedCredentialAsync(teamId, actorId, ProviderTypeKey, ["""{"accessKey":"never-read"}"""]);
        var encryptor = new RecordingRejectingEncryptor();

        using (var scope = ResolverScope(null, encryptor))
        {
            var result = await scope.Resolve<IStorageCredentialSecretResolver>().ResolveAsync(Request(teamId, credential.Id, 1), CancellationToken.None);
            result.ShouldBe(new StorageCredentialSecretResolution.ProviderUnavailable(StorageCredentialProviderUnavailableReason.ModuleMissing));
        }

        using (var scope = ResolverScope(new TestStorageProviderModule(ProviderTypeKey, Schema("""{"unsupported":true}""")), encryptor))
        {
            var result = await scope.Resolve<IStorageCredentialSecretResolver>().ResolveAsync(Request(teamId, credential.Id, 1), CancellationToken.None);
            result.ShouldBe(new StorageCredentialSecretResolution.ProviderUnavailable(StorageCredentialProviderUnavailableReason.SecretSchemaInvalid));
        }

        encryptor.DecryptCalls.ShouldBe(0);
    }

    [Fact]
    public async Task Tampered_ciphertext_malformed_json_and_schema_mismatch_are_typed_invalid_envelopes()
    {
        var (teamId, actorId) = await SeedTeamAsync();
        var tampered = await SeedEncryptedCredentialAsync(teamId, actorId, ProviderTypeKey, "CfDJ8-this-envelope-was-tampered");
        var malformedJson = await SeedCredentialAsync(teamId, actorId, ProviderTypeKey, ["{not-json}"]);
        var schemaMismatch = await SeedCredentialAsync(teamId, actorId, ProviderTypeKey, ["""{"region":"cn-hangzhou"}"""]);

        (await ResolveAsync(Request(teamId, tampered.Id, 1))).ShouldBe(new StorageCredentialSecretResolution.InvalidEnvelope(StorageCredentialEnvelopeInvalidReason.Decryption));
        (await ResolveAsync(Request(teamId, malformedJson.Id, 1))).ShouldBe(new StorageCredentialSecretResolution.InvalidEnvelope(StorageCredentialEnvelopeInvalidReason.Json));
        (await ResolveAsync(Request(teamId, schemaMismatch.Id, 1))).ShouldBe(new StorageCredentialSecretResolution.InvalidEnvelope(StorageCredentialEnvelopeInvalidReason.SchemaMismatch));
    }

    [Fact]
    public async Task Cancellation_is_observed_before_querying_or_decrypting()
    {
        var encryptor = new RecordingRejectingEncryptor();
        using var scope = ResolverScope(Module, encryptor);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Should.ThrowAsync<OperationCanceledException>(() => scope.Resolve<IStorageCredentialSecretResolver>()
            .ResolveAsync(Request(Guid.NewGuid(), Guid.NewGuid(), 1), cancellation.Token));
        encryptor.DecryptCalls.ShouldBe(0);
    }

    private async Task<StorageCredentialSecretResolution> ResolveAsync(StorageCredentialSecretRequest request)
    {
        using var scope = ResolverScope(Module);
        return await scope.Resolve<IStorageCredentialSecretResolver>().ResolveAsync(request, CancellationToken.None);
    }

    private ILifetimeScope ResolverScope(IStorageProviderModule? module, IPayloadEncryptor? encryptor = null) => _fixture.BeginScope(builder =>
    {
        builder.RegisterInstance(new SingleModuleCatalog(module)).As<IStorageProviderModuleCatalog>();
        if (encryptor != null) builder.RegisterInstance(encryptor).As<IPayloadEncryptor>();
    });

    private async Task<(Guid TeamId, Guid ActorId)> SeedTeamAsync()
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var actorId = Guid.NewGuid();
        var teamId = Guid.NewGuid();
        db.User.Add(new User { Id = actorId, Email = $"secret-resolver-{actorId:N}@test.local", Name = $"secret-resolver-{actorId:N}" });
        db.Team.Add(new Team { Id = teamId, Slug = $"secret-resolver-{teamId:N}", Name = "Secret Resolver Team", Kind = TeamKind.Workspace });
        db.TeamMembership.Add(new TeamMembership { Id = Guid.NewGuid(), TeamId = teamId, UserId = actorId, Role = TeamRole.Owner });
        await db.SaveChangesAsync(CancellationToken.None);
        return (teamId, actorId);
    }

    private async Task<StorageCredential> SeedCredentialAsync(Guid teamId, Guid actorId, string providerTypeKey, IReadOnlyList<string> plaintextRevisions, StorageCredentialState state = StorageCredentialState.Active)
    {
        using var scope = _fixture.BeginScope();
        var encryptor = scope.Resolve<IPayloadEncryptor>();
        var encrypted = plaintextRevisions.Select(encryptor.Encrypt).ToList();
        return await SeedCredentialAsync(scope.Resolve<CodeSpaceDbContext>(), teamId, actorId, new CredentialSeed(providerTypeKey, encrypted, state));
    }

    private async Task<StorageCredential> SeedEncryptedCredentialAsync(Guid teamId, Guid actorId, string providerTypeKey, string encryptedPayload)
    {
        using var scope = _fixture.BeginScope();
        return await SeedCredentialAsync(scope.Resolve<CodeSpaceDbContext>(), teamId, actorId, new CredentialSeed(providerTypeKey, [encryptedPayload], StorageCredentialState.Active));
    }

    private static async Task<StorageCredential> SeedCredentialAsync(CodeSpaceDbContext db, Guid teamId, Guid actorId, CredentialSeed seed)
    {
        var credential = new StorageCredential
        {
            Id = Guid.NewGuid(), TeamId = teamId, StableName = $"secret-{Guid.NewGuid():N}", CurrentRevision = 1, State = StorageCredentialState.Active,
            CreatedDate = DateTimeOffset.UtcNow, CreatedBy = actorId,
        };
        credential.Revisions.Add(Revision(credential, actorId, seed.ProviderTypeKey, 1, seed.EncryptedRevisions[0]));
        db.StorageCredential.Add(credential);
        await db.SaveChangesAsync(CancellationToken.None);

        for (var index = 1; index < seed.EncryptedRevisions.Count; index++)
        {
            var revision = index + 1;
            db.StorageCredentialRevision.Add(Revision(credential, actorId, seed.ProviderTypeKey, revision, seed.EncryptedRevisions[index]));
            credential.CurrentRevision = revision;
            await db.SaveChangesAsync(CancellationToken.None);
        }

        if (seed.State == StorageCredentialState.Revoked)
        {
            credential.State = StorageCredentialState.Revoked;
            credential.RevokedDate = DateTimeOffset.UtcNow;
            credential.RevokedBy = actorId;
            await db.SaveChangesAsync(CancellationToken.None);
        }

        return credential;
    }

    private static StorageCredentialRevision Revision(StorageCredential credential, Guid actorId, string providerTypeKey, int revision, string encryptedPayload) => new()
    {
        Id = Guid.NewGuid(), TeamId = credential.TeamId, StorageCredentialId = credential.Id, Revision = revision,
        ProviderTypeKey = providerTypeKey, EncryptedPayload = encryptedPayload, EnvelopeFingerprint = StorageCredentialRules.EnvelopeFingerprint(encryptedPayload),
        CreatedDate = DateTimeOffset.UtcNow, CreatedBy = actorId,
    };

    private static StorageCredentialSecretRequest Request(Guid teamId, Guid credentialId, int revision) => new(teamId, credentialId, revision, ProviderTypeKey);

    private static JsonElement Schema(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private sealed class SingleModuleCatalog(IStorageProviderModule? module) : IStorageProviderModuleCatalog
    {
        public IReadOnlyList<IStorageProviderModule> Modules => module == null ? [] : [module];
        public IStorageProviderModule? Get(string typeKey) => module != null && string.Equals(module.TypeKey, typeKey, StringComparison.Ordinal) ? module : null;
        public IStorageProviderModule Require(string typeKey) => Get(typeKey) ?? throw new NotSupportedException();
    }

    private sealed class TestStorageProviderModule(string typeKey, JsonElement secretSchema) : IStorageProviderModule
    {
        public string TypeKey => typeKey;
        public string DisplayName => "Test secret store";
        public JsonElement ConfigSchema => Schema("{}");
        public JsonElement SecretSchema => secretSchema;
        public StorageProviderCapabilities Capabilities => StorageProviderCapabilities.None;
        public Type FactoryType => typeof(LocalRwxArtifactStorageDriverFactory);
    }

    private sealed class RecordingRejectingEncryptor : IPayloadEncryptor
    {
        public int DecryptCalls { get; private set; }
        public string Encrypt(string plaintext) => throw new InvalidOperationException("The resolver never encrypts.");

        public string Decrypt(string ciphertext)
        {
            DecryptCalls++;
            throw new InvalidOperationException("The envelope must not reach decryption.");
        }
    }

    private sealed record CredentialSeed(string ProviderTypeKey, IReadOnlyList<string> EncryptedRevisions, StorageCredentialState State);
}
