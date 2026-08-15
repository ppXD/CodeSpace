using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Workflows.Artifacts.Profiles;
using CodeSpace.Core.Services.Workflows.Artifacts.Providers;
using CodeSpace.Core.Services.Workflows.Artifacts.Providers.Local;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows.Artifacts.Profiles;

[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class StorageProfileSnapshotResolverTests
{
    private readonly PostgresFixture _fixture;

    public StorageProfileSnapshotResolverTests(PostgresFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Ready_local_profile_pins_the_requested_revision_without_a_secret_or_tracking()
    {
        var (teamId, actorId) = await SeedTeamAsync();
        var profile = await SeedProfileAsync(teamId, actorId, """{"rootPath":"/srv/artifacts-v1"}""");
        await AppendProfileRevisionAsync(profile, actorId, 2, """{"rootPath":"/srv/artifacts-v2"}""");

        using var scope = _fixture.BeginScope();
        var result = await scope.Resolve<IStorageProfileSnapshotResolver>().ResolveAsync(new StorageProfileSnapshotRequest(teamId, profile.Id, 1), CancellationToken.None);

        var ready = result.ShouldBeOfType<StorageProfileSnapshotResolution.Ready>();
        ready.Snapshot.ProfileId.ShouldBe(profile.Id);
        ready.Snapshot.ProfileRevision.ShouldBe(1);
        ready.Snapshot.ProviderTypeKey.ShouldBe(LocalRwxArtifactStorageDriverFactory.TypeKey);
        ready.Snapshot.Configuration.GetProperty("rootPath").GetString().ShouldBe("/srv/artifacts-v1");
        ready.Snapshot.SecretReference.ShouldBeNull();
        scope.Resolve<CodeSpaceDbContext>().ChangeTracker.Entries().ShouldBeEmpty();
    }

    [Fact]
    public async Task Ready_credential_projects_only_an_opaque_reference_and_never_invokes_the_factory()
    {
        var (teamId, actorId) = await SeedTeamAsync();
        var credential = await SeedCredentialAsync(teamId, actorId, LocalRwxArtifactStorageDriverFactory.TypeKey);
        var profile = await SeedProfileAsync(teamId, actorId, """{"rootPath":"/srv/credentialed"}""", CredentialRef(credential.Id, 1));
        var factory = new RecordingFactory();

        using var scope = _fixture.BeginScope(builder => builder.RegisterInstance(new SingleFactoryCatalog(factory)).As<IArtifactStorageDriverFactoryCatalog>());
        var result = await scope.Resolve<IStorageProfileSnapshotResolver>().ResolveAsync(new StorageProfileSnapshotRequest(teamId, profile.Id, 1), CancellationToken.None);

        var ready = result.ShouldBeOfType<StorageProfileSnapshotResolution.Ready>();
        ready.Snapshot.SecretReference.ShouldBe(new StorageSecretReference("database/v1", credential.Id.ToString("D"), "1"));
        factory.CreateCalls.ShouldBe(0);
        ready.Snapshot.GetType().GetProperties().Select(property => property.Name).ShouldNotContain("EncryptedPayload");
    }

    [Fact]
    public async Task Disabled_and_retired_profiles_are_not_active()
    {
        var (teamId, actorId) = await SeedTeamAsync();
        var disabled = await SeedProfileAsync(teamId, actorId, """{"rootPath":"/srv/disabled"}""", state: StorageProfileState.Disabled);
        var retired = await SeedProfileAsync(teamId, actorId, """{"rootPath":"/srv/retired"}""", state: StorageProfileState.Retired);

        (await ResolveAsync(teamId, disabled.Id, 1)).ShouldBeOfType<StorageProfileSnapshotResolution.NotActive>();
        (await ResolveAsync(teamId, retired.Id, 1)).ShouldBeOfType<StorageProfileSnapshotResolution.NotActive>();
    }

    [Fact]
    public async Task Missing_foreign_and_missing_revision_profiles_have_distinct_typed_results()
    {
        var (teamId, actorId) = await SeedTeamAsync();
        var (foreignTeamId, _) = await SeedTeamAsync();
        var profile = await SeedProfileAsync(teamId, actorId, """{"rootPath":"/srv/scoped"}""");

        (await ResolveAsync(teamId, Guid.NewGuid(), 1)).ShouldBeOfType<StorageProfileSnapshotResolution.Missing>();
        (await ResolveAsync(foreignTeamId, profile.Id, 1)).ShouldBeOfType<StorageProfileSnapshotResolution.Missing>();
        (await ResolveAsync(teamId, profile.Id, 2)).ShouldBeOfType<StorageProfileSnapshotResolution.RevisionMissing>();
    }

    [Fact]
    public async Task Removed_provider_module_and_factory_are_reported_without_activation()
    {
        var (teamId, actorId) = await SeedTeamAsync();
        var profile = await SeedProfileAsync(teamId, actorId, """{"rootPath":"/srv/catalog"}""");

        using (var scope = _fixture.BeginScope(builder => builder.RegisterInstance(EmptyModuleCatalog.Instance).As<IStorageProviderModuleCatalog>()))
        {
            var result = await scope.Resolve<IStorageProfileSnapshotResolver>().ResolveAsync(new StorageProfileSnapshotRequest(teamId, profile.Id, 1), CancellationToken.None);
            var unavailable = result.ShouldBeOfType<StorageProfileSnapshotResolution.ProviderUnavailable>();
            unavailable.Reason.ShouldBe(StorageProfileProviderUnavailableReason.ModuleMissing);
            unavailable.ProviderTypeKey.ShouldBe(LocalRwxArtifactStorageDriverFactory.TypeKey);
        }

        using (var scope = _fixture.BeginScope(builder => builder.RegisterInstance(EmptyFactoryCatalog.Instance).As<IArtifactStorageDriverFactoryCatalog>()))
        {
            var result = await scope.Resolve<IStorageProfileSnapshotResolver>().ResolveAsync(new StorageProfileSnapshotRequest(teamId, profile.Id, 1), CancellationToken.None);
            result.ShouldBe(new StorageProfileSnapshotResolution.ProviderUnavailable(LocalRwxArtifactStorageDriverFactory.TypeKey, StorageProfileProviderUnavailableReason.FactoryMissing));
        }
    }

    [Fact]
    public async Task Invalid_persisted_configuration_is_a_typed_readiness_result()
    {
        var (teamId, actorId) = await SeedTeamAsync();
        var profile = await SeedProfileAsync(teamId, actorId, """{"rootPath":""}""");

        var result = await ResolveAsync(teamId, profile.Id, 1);

        result.ShouldBe(new StorageProfileSnapshotResolution.Invalid(StorageProfileSnapshotInvalidReason.Configuration));
    }

    [Fact]
    public async Task Revoked_foreign_missing_and_revision_missing_credentials_are_unavailable()
    {
        var (teamId, actorId) = await SeedTeamAsync();
        var (foreignTeamId, foreignActorId) = await SeedTeamAsync();
        var revoked = await SeedCredentialAsync(teamId, actorId, LocalRwxArtifactStorageDriverFactory.TypeKey, StorageCredentialState.Revoked);
        var foreign = await SeedCredentialAsync(foreignTeamId, foreignActorId, LocalRwxArtifactStorageDriverFactory.TypeKey);
        var revokedProfile = await SeedProfileAsync(teamId, actorId, """{"rootPath":"/srv/revoked"}""", CredentialRef(revoked.Id, 1));
        var foreignProfile = await SeedProfileAsync(teamId, actorId, """{"rootPath":"/srv/foreign"}""", CredentialRef(foreign.Id, 1));
        var missingProfile = await SeedProfileAsync(teamId, actorId, """{"rootPath":"/srv/missing"}""", CredentialRef(Guid.NewGuid(), 1));
        var missingRevisionProfile = await SeedProfileAsync(teamId, actorId, """{"rootPath":"/srv/missing-revision"}""", CredentialRef(revoked.Id, 2));

        var revokedResult = (await ResolveAsync(teamId, revokedProfile.Id, 1)).ShouldBeOfType<StorageProfileSnapshotResolution.CredentialUnavailable>();
        revokedResult.Reason.ShouldBe(StorageProfileCredentialUnavailableReason.NotActive);
        (await ResolveAsync(teamId, foreignProfile.Id, 1)).ShouldBe(new StorageProfileSnapshotResolution.CredentialUnavailable(StorageProfileCredentialUnavailableReason.Missing));
        (await ResolveAsync(teamId, missingProfile.Id, 1)).ShouldBe(new StorageProfileSnapshotResolution.CredentialUnavailable(StorageProfileCredentialUnavailableReason.Missing));
        (await ResolveAsync(teamId, missingRevisionProfile.Id, 1)).ShouldBe(new StorageProfileSnapshotResolution.CredentialUnavailable(StorageProfileCredentialUnavailableReason.NotActive));

        var active = await SeedCredentialAsync(teamId, actorId, LocalRwxArtifactStorageDriverFactory.TypeKey);
        var activeMissingRevisionProfile = await SeedProfileAsync(teamId, actorId, """{"rootPath":"/srv/active-missing-revision"}""", CredentialRef(active.Id, 2));
        (await ResolveAsync(teamId, activeMissingRevisionProfile.Id, 1)).ShouldBe(new StorageProfileSnapshotResolution.CredentialUnavailable(StorageProfileCredentialUnavailableReason.RevisionMissing));
    }

    [Fact]
    public async Task Malformed_and_wrong_provider_credentials_are_invalid()
    {
        var (teamId, actorId) = await SeedTeamAsync();
        var wrongProvider = await SeedCredentialAsync(teamId, actorId, "other-store/v1");
        var malformedProfile = await SeedProfileAsync(teamId, actorId, """{"rootPath":"/srv/malformed"}""", "env:STORAGE_SECRET");
        var mismatchedProfile = await SeedProfileAsync(teamId, actorId, """{"rootPath":"/srv/mismatch"}""", CredentialRef(wrongProvider.Id, 1));

        (await ResolveAsync(teamId, malformedProfile.Id, 1)).ShouldBe(new StorageProfileSnapshotResolution.CredentialInvalid(StorageProfileCredentialInvalidReason.MalformedReference));
        (await ResolveAsync(teamId, mismatchedProfile.Id, 1)).ShouldBe(new StorageProfileSnapshotResolution.CredentialInvalid(StorageProfileCredentialInvalidReason.ProviderMismatch));
    }

    private async Task<StorageProfileSnapshotResolution> ResolveAsync(Guid teamId, Guid profileId, int revision)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<IStorageProfileSnapshotResolver>().ResolveAsync(new StorageProfileSnapshotRequest(teamId, profileId, revision), CancellationToken.None);
    }

    private async Task<(Guid TeamId, Guid ActorId)> SeedTeamAsync()
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var actorId = Guid.NewGuid();
        var teamId = Guid.NewGuid();
        db.User.Add(new User { Id = actorId, Email = $"snapshot-{actorId:N}@test.local", Name = $"snapshot-{actorId:N}" });
        db.Team.Add(new Team { Id = teamId, Slug = $"snapshot-{teamId:N}", Name = "Snapshot Resolver Team", Kind = TeamKind.Workspace });
        db.TeamMembership.Add(new TeamMembership { Id = Guid.NewGuid(), TeamId = teamId, UserId = actorId, Role = TeamRole.Owner });
        await db.SaveChangesAsync(CancellationToken.None);
        return (teamId, actorId);
    }

    private async Task<StorageProfile> SeedProfileAsync(Guid teamId, Guid actorId, string config, string? credentialRef = null, StorageProfileState state = StorageProfileState.Active)
    {
        var profile = new StorageProfile
        {
            Id = Guid.NewGuid(), TeamId = teamId, StableName = $"snapshot-{Guid.NewGuid():N}", CurrentRevision = 1, State = state,
            CreatedDate = DateTimeOffset.UtcNow, CreatedBy = actorId, LastModifiedDate = DateTimeOffset.UtcNow, LastModifiedBy = actorId,
        };
        profile.Revisions.Add(ProfileRevision(profile, 1, actorId, config, credentialRef));

        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        db.StorageProfile.Add(profile);
        await db.SaveChangesAsync(CancellationToken.None);
        return profile;
    }

    private async Task AppendProfileRevisionAsync(StorageProfile profile, Guid actorId, int revision, string config)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var stored = await db.StorageProfile.SingleAsync(value => value.Id == profile.Id, CancellationToken.None);
        db.StorageProfileRevision.Add(ProfileRevision(stored, revision, actorId, config, null));
        stored.CurrentRevision = revision;
        stored.LastModifiedDate = DateTimeOffset.UtcNow;
        stored.LastModifiedBy = actorId;
        await db.SaveChangesAsync(CancellationToken.None);
    }

    private async Task<StorageCredential> SeedCredentialAsync(Guid teamId, Guid actorId, string providerTypeKey, StorageCredentialState state = StorageCredentialState.Active)
    {
        var credential = new StorageCredential
        {
            Id = Guid.NewGuid(), TeamId = teamId, StableName = $"snapshot-{Guid.NewGuid():N}", CurrentRevision = 1, State = StorageCredentialState.Active,
            CreatedDate = DateTimeOffset.UtcNow, CreatedBy = actorId,
        };
        credential.Revisions.Add(new StorageCredentialRevision
        {
            Id = Guid.NewGuid(), TeamId = teamId, StorageCredentialId = credential.Id, Revision = 1,
            ProviderTypeKey = providerTypeKey, EncryptedPayload = "opaque-encrypted-payload-must-never-be-read",
            SafeHint = "safe-hint", EnvelopeFingerprint = Fingerprint('c'), CreatedDate = DateTimeOffset.UtcNow, CreatedBy = actorId,
        });

        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        db.StorageCredential.Add(credential);
        await db.SaveChangesAsync(CancellationToken.None);

        if (state == StorageCredentialState.Revoked)
        {
            credential.State = StorageCredentialState.Revoked;
            credential.RevokedDate = DateTimeOffset.UtcNow;
            credential.RevokedBy = actorId;
            await db.SaveChangesAsync(CancellationToken.None);
        }

        return credential;
    }

    private static StorageProfileRevision ProfileRevision(StorageProfile profile, int revision, Guid actorId, string config, string? credentialRef) => new()
    {
        Id = Guid.NewGuid(), TeamId = profile.TeamId, StorageProfileId = profile.Id, Revision = revision,
        ProviderTypeKey = LocalRwxArtifactStorageDriverFactory.TypeKey, NonSecretConfigJson = config, CredentialRef = credentialRef,
        NamespaceFingerprint = Fingerprint('a'), CreatedDate = DateTimeOffset.UtcNow, CreatedBy = actorId,
    };

    private static string CredentialRef(Guid id, int revision) => $"db:{id:D}:{revision}";
    private static string Fingerprint(char hex) => $"sha256:{new string(hex, 64)}";

    private sealed class RecordingFactory : IArtifactStorageDriverFactory
    {
        public string ProviderTypeKey => LocalRwxArtifactStorageDriverFactory.TypeKey;
        public int CreateCalls { get; private set; }

        public ValueTask<IArtifactStorageDriver> CreateAsync(ArtifactStorageDriverCreateRequest request, CancellationToken cancellationToken)
        {
            CreateCalls++;
            throw new InvalidOperationException("The snapshot resolver must never instantiate a storage driver.");
        }
    }

    private sealed class SingleFactoryCatalog(IArtifactStorageDriverFactory factory) : IArtifactStorageDriverFactoryCatalog
    {
        public IArtifactStorageDriverFactory? Get(string providerTypeKey) => string.Equals(providerTypeKey, factory.ProviderTypeKey, StringComparison.Ordinal) ? factory : null;
        public IArtifactStorageDriverFactory Require(string providerTypeKey) => Get(providerTypeKey) ?? throw new NotSupportedException();
    }

    private sealed class EmptyFactoryCatalog : IArtifactStorageDriverFactoryCatalog
    {
        public static EmptyFactoryCatalog Instance { get; } = new();
        public IArtifactStorageDriverFactory? Get(string providerTypeKey) => null;
        public IArtifactStorageDriverFactory Require(string providerTypeKey) => throw new NotSupportedException();
    }

    private sealed class EmptyModuleCatalog : IStorageProviderModuleCatalog
    {
        public static EmptyModuleCatalog Instance { get; } = new();
        public IReadOnlyList<IStorageProviderModule> Modules => [];
        public IStorageProviderModule? Get(string typeKey) => null;
        public IStorageProviderModule Require(string typeKey) => throw new NotSupportedException();
    }
}
