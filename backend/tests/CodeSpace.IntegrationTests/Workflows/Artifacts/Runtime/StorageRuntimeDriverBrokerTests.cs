using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Credentials;
using CodeSpace.Core.Services.Workflows.Artifacts.Providers;
using CodeSpace.Core.Services.Workflows.Artifacts.Providers.Local;
using CodeSpace.Core.Services.Workflows.Artifacts.Runtime;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.Messages.Enums;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows.Artifacts.Runtime;

[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class StorageRuntimeDriverBrokerTests
{
    private readonly PostgresFixture _fixture;

    public StorageRuntimeDriverBrokerTests(PostgresFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Exact_persisted_profile_and_credential_revision_activate_a_streaming_driver_through_the_scoped_broker()
    {
        var oldRoot = Path.Combine(Path.GetTempPath(), "codespace-runtime-broker", Guid.NewGuid().ToString("N"), "old");
        var newRoot = Path.Combine(Path.GetDirectoryName(oldRoot)!, "new");
        try
        {
            var (teamId, actorId, profileId) = await SeedAsync(oldRoot, newRoot);

            using var scope = _fixture.BeginScope();
            var broker = scope.Resolve<IStorageRuntimeDriverBroker>();
            var resolution = await broker.OpenAsync(new StorageRuntimeDriverRequest(teamId, profileId, 1), CancellationToken.None);

            var ready = resolution.ShouldBeOfType<StorageRuntimeDriverResolution.Ready>();
            await using (ready.Lease)
            {
                await using var content = new MemoryStream(Encoding.UTF8.GetBytes("runtime-broker-payload"));
                var put = await ready.Lease.Driver.PutAsync(new ArtifactStoragePutRequest("exact/revision.txt", content)
                {
                    ContentLength = content.Length,
                    ExpectedSha256 = Convert.ToHexString(SHA256.HashData(content.ToArray())).ToLowerInvariant(),
                    Condition = ArtifactStorageWriteCondition.CreateOnly,
                }, CancellationToken.None);

                put.IsSuccess.ShouldBeTrue();
                var head = await ready.Lease.Driver.HeadAsync(new ArtifactStorageHeadRequest("exact/revision.txt"), CancellationToken.None);
                head.IsSuccess.ShouldBeTrue();
                head.Metadata!.Length.ShouldBe(content.Length);
            }

            File.Exists(Path.Combine(oldRoot, "objects", "exact", "revision.txt")).ShouldBeTrue("the broker must activate requested revision 1, not current revision 2");
            File.Exists(Path.Combine(newRoot, "objects", "exact", "revision.txt")).ShouldBeFalse();
            Should.Throw<ObjectDisposedException>(() => _ = ready.Lease.Driver);
        }
        finally
        {
            try
            {
                var root = Directory.GetParent(oldRoot)?.FullName;
                if (root != null && Directory.Exists(root)) Directory.Delete(root, recursive: true);
            }
            catch { }
        }
    }

    private async Task<(Guid TeamId, Guid ActorId, Guid ProfileId)> SeedAsync(string oldRoot, string newRoot)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var encryptor = scope.Resolve<IPayloadEncryptor>();
        var teamId = Guid.NewGuid();
        var actorId = Guid.NewGuid();
        var credentialId = Guid.NewGuid();
        var profileId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        db.User.Add(new User { Id = actorId, Email = $"runtime-{actorId:N}@test.local", Name = $"runtime-{actorId:N}" });
        db.Team.Add(new Team { Id = teamId, Slug = $"runtime-{teamId:N}", Name = "Storage Runtime Broker Team", Kind = TeamKind.Workspace });
        db.TeamMembership.Add(new TeamMembership { Id = Guid.NewGuid(), TeamId = teamId, UserId = actorId, Role = TeamRole.Owner });

        var credential = new StorageCredential
        {
            Id = credentialId, TeamId = teamId, StableName = $"runtime-{credentialId:N}", CurrentRevision = 1,
            State = StorageCredentialState.Active, CreatedDate = now, CreatedBy = actorId,
        };
        credential.Revisions.Add(new StorageCredentialRevision
        {
            Id = Guid.NewGuid(), TeamId = teamId, StorageCredentialId = credentialId, Revision = 1,
            ProviderTypeKey = LocalRwxArtifactStorageDriverFactory.TypeKey, EncryptedPayload = encryptor.Encrypt("{}"),
            SafeHint = "configured", EnvelopeFingerprint = Fingerprint('b'), CreatedDate = now, CreatedBy = actorId,
        });
        db.StorageCredential.Add(credential);

        var profile = new StorageProfile
        {
            Id = profileId, TeamId = teamId, StableName = $"runtime-{profileId:N}", CurrentRevision = 2,
            State = StorageProfileState.Active, CreatedDate = now, CreatedBy = actorId, LastModifiedDate = now, LastModifiedBy = actorId,
        };
        profile.Revisions.Add(ProfileRevision(profile, 1, actorId, oldRoot, $"db:{credentialId:D}:1"));
        profile.Revisions.Add(ProfileRevision(profile, 2, actorId, newRoot, null));
        db.StorageProfile.Add(profile);

        await db.SaveChangesAsync(CancellationToken.None);
        return (teamId, actorId, profileId);
    }

    private static StorageProfileRevision ProfileRevision(StorageProfile profile, int revision, Guid actorId, string root, string? credentialRef) => new()
    {
        Id = Guid.NewGuid(), TeamId = profile.TeamId, StorageProfileId = profile.Id, Revision = revision,
        ProviderTypeKey = LocalRwxArtifactStorageDriverFactory.TypeKey, NonSecretConfigJson = JsonSerializer.Serialize(new { rootPath = root }),
        CredentialRef = credentialRef, NamespaceFingerprint = Fingerprint(revision == 1 ? 'c' : 'd'), CreatedDate = DateTimeOffset.UtcNow, CreatedBy = actorId,
    };

    private static string Fingerprint(char hex) => $"sha256:{new string(hex, 64)}";
}
