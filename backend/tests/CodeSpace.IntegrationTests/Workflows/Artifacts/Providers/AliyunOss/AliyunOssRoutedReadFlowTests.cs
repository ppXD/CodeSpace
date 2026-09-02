using System.Text;
using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Credentials;
using CodeSpace.Core.Services.Workflows.Artifacts;
using CodeSpace.Core.Services.Workflows.Artifacts.Providers;
using CodeSpace.Core.Services.Workflows.Artifacts.Exceptions;
using CodeSpace.Core.Services.Workflows.Artifacts.Providers.AliyunOss;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows.Artifacts.Providers.AliyunOss;

/// <summary>
/// The OBJECT STORE provider, driven through the whole stack instead of at its own seam.
///
/// <para>Every routed test in this suite pins <c>local-rwx/v1</c>, and the OSS driver is exercised only by driver-level
/// contract tests. So every claim about how a routed read behaves on the one provider a real deployment uses — ETag
/// pinning, the Content-Range total, the driver lease owning the response stream, credential resolution through the
/// profile revision — was an extrapolation from a local FileStream.</para>
///
/// <para>Medium-mock fidelity (Rule 12): the production factory, driver, CAS coordinator, store and Postgres are all
/// real; only the socket is replaced, by the SAME in-memory OSS endpoint the driver-level contract tests use, which
/// verifies v4 signatures and rejects an unsigned or mis-signed request. A real-bucket lane exists separately and is
/// opt-in behind four environment variables, so it cannot be what a default build relies on.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class AliyunOssRoutedReadFlowTests : IDisposable
{
    private readonly PostgresFixture _fixture;
    private readonly FakeAliyunOssHandler _oss = new();

    public AliyunOssRoutedReadFlowTests(PostgresFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task An_artifact_routed_to_object_storage_is_written_and_read_back_through_the_provider()
    {
        var world = await SeedRoutedToOssAsync();
        var payload = RoutedArtifactSeed.Payload("bytes that only exist in the bucket");

        var artifactId = await PutAsync(world, payload);

        var row = await ArtifactRowAsync(artifactId);
        row.CasArtifactObjectId.ShouldNotBeNull("the bytes must be reachable only through the location ledger");
        _oss.Calls.ShouldContain(call => call.StartsWith("PUT /", StringComparison.Ordinal), "the write must have reached the object store, not a local disk");

        (await GetBytesAsync(world, artifactId)).ShouldBe(payload, "every byte must come back, through a signed GET against the bucket");
        _oss.Calls.ShouldContain(call => call.StartsWith("GET /", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_range_read_against_object_storage_is_a_real_ranged_request()
    {
        // The UI pages large content through ranges. A range that silently fetched the whole object would work in a
        // test and cost the operator the entire artifact on every scroll.
        var world = await SeedRoutedToOssAsync();
        var payload = RoutedArtifactSeed.Payload("ranged");
        var artifactId = await PutAsync(world, payload);

        using var scope = BuildScope();
        var range = await scope.Resolve<IArtifactRangeReader>().ReadRangeAsync(world.TeamId, artifactId, 100, 64, CancellationToken.None);

        range.Bytes.ToArray().ShouldBe(payload[100..164]);
        range.TotalLength.ShouldBe(payload.Length, "the total must come from the provider's Content-Range, not from the row");
        _oss.Calls.ShouldContain(call => call.StartsWith("GET /", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_object_the_bucket_no_longer_holds_fails_typed_rather_than_returning_empty_bytes()
    {
        // The failure a purged or externally-deleted object produces. Empty bytes would render as an empty file and
        // read as success everywhere downstream.
        var world = await SeedRoutedToOssAsync();
        var artifactId = await PutAsync(world, RoutedArtifactSeed.Payload("about to vanish"));

        _oss.EmptyBucket();

        using var scope = BuildScope();
        var failure = await Should.ThrowAsync<ArtifactContentUnavailableException>(
            () => scope.Resolve<IArtifactStore>().GetBytesAsync(world.TeamId, artifactId, CancellationToken.None));

        failure.Kind.ShouldBe(ArtifactContentUnavailableKind.PhysicalObjectMissing);
    }

    [Fact]
    public async Task A_credential_the_bucket_rejects_fails_the_read_typed_rather_than_silently()
    {
        // What an operator's revoked key actually does to a read. It must arrive as AccessDenied, which is the one
        // kind that tells a UI to say "fix the credential" rather than "the file is gone".
        var world = await SeedRoutedToOssAsync();
        var artifactId = await PutAsync(world, RoutedArtifactSeed.Payload("stored while the key worked"));

        _oss.RejectEverySignature = true;

        using var scope = BuildScope();
        var failure = await Should.ThrowAsync<ArtifactContentUnavailableException>(
            () => scope.Resolve<IArtifactStore>().GetBytesAsync(world.TeamId, artifactId, CancellationToken.None));

        failure.Kind.ShouldBe(ArtifactContentUnavailableKind.AccessDenied,
            "a credential failure that surfaced as a missing object would send an operator looking for deleted data");
    }

    // ─── World + helpers ─────────────────────────────────────────────────────

    private async Task<Guid> PutAsync(OssWorld world, byte[] payload)
    {
        using var scope = BuildScope();

        return await scope.Resolve<IArtifactStore>().PutAsync(world.TeamId, payload, "application/octet-stream", CancellationToken.None);
    }

    private async Task<byte[]> GetBytesAsync(OssWorld world, Guid artifactId)
    {
        using var scope = BuildScope();

        return (await scope.Resolve<IArtifactStore>().GetBytesAsync(world.TeamId, artifactId, CancellationToken.None)).Bytes.ToArray();
    }

    /// <summary>
    /// A container scope whose OSS driver speaks to the in-memory endpoint. Everything else — the broker, the
    /// credential resolver, the CAS coordinator, the store — is production.
    ///
    /// <para>The CATALOG is replaced, not the factory: factories are registered SingleInstance on the root container,
    /// so a child-scope registration would be ignored and the real socket-backed factory would still be chosen. The
    /// replacement keeps every other provider's real factory, so only the OSS transport is swapped.</para>
    /// </summary>
    private ILifetimeScope BuildScope() => _fixture.BeginScope(builder =>
        builder.Register(context =>
        {
            var factories = context.Resolve<IEnumerable<IArtifactStorageDriverFactory>>()
                .Where(factory => factory.ProviderTypeKey != AliyunOssArtifactStorageDriverFactory.TypeKey)
                .Append(new AliyunOssArtifactStorageDriverFactory(_oss));

            return new ArtifactStorageDriverFactoryCatalog(factories, context.Resolve<IStorageProviderModuleCatalog>());
        }).As<IArtifactStorageDriverFactoryCatalog>().InstancePerLifetimeScope());

    private async Task<WorkflowArtifact> ArtifactRowAsync(Guid artifactId)
    {
        using var scope = _fixture.BeginScope();

        return await scope.Resolve<CodeSpaceDbContext>().WorkflowArtifact.AsNoTracking().SingleAsync(row => row.Id == artifactId);
    }

    private async Task<OssWorld> SeedRoutedToOssAsync()
    {
        var actorId = Guid.NewGuid();
        var teamId = Guid.NewGuid();
        var profileId = Guid.NewGuid();
        var credentialId = Guid.NewGuid();
        var routeId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        db.User.Add(new User { Id = actorId, Email = $"oss-{actorId:N}@test.local", Name = "Oss" });
        db.Team.Add(new Team { Id = teamId, Slug = $"oss-{teamId:N}", Name = "Oss", Kind = TeamKind.Workspace });
        db.TeamMembership.Add(new TeamMembership { Id = Guid.NewGuid(), TeamId = teamId, UserId = actorId, Role = TeamRole.Owner });

        var secret = JsonSerializer.Serialize(new { accessKeyId = FakeAliyunOssHandler.AccessKeyId, accessKeySecret = FakeAliyunOssHandler.AccessKeySecret, securityToken = FakeAliyunOssHandler.SecurityToken });
        var credential = new StorageCredential
        {
            Id = credentialId, TeamId = teamId, StableName = $"oss-{credentialId:N}", CurrentRevision = 1,
            State = StorageCredentialState.Active, CreatedDate = now, CreatedBy = actorId,
        };
        credential.Revisions.Add(new StorageCredentialRevision
        {
            Id = Guid.NewGuid(), TeamId = teamId, StorageCredentialId = credentialId, Revision = 1,
            ProviderTypeKey = AliyunOssArtifactStorageDriverFactory.TypeKey,
            EncryptedPayload = scope.Resolve<IPayloadEncryptor>().Encrypt(secret),
            SafeHint = "LTAI…yId", EnvelopeFingerprint = $"sha256:{new string('1', 64)}", CreatedDate = now, CreatedBy = actorId,
        });
        db.StorageCredential.Add(credential);

        var config = JsonSerializer.Serialize(new
        {
            endpoint = FakeAliyunOssHandler.Host,
            region = FakeAliyunOssHandler.Region,
            bucket = FakeAliyunOssHandler.Bucket,
            keyPrefix = $"team-{teamId:N}/",
        });
        var profile = new StorageProfile
        {
            Id = profileId, TeamId = teamId, StableName = $"oss-{profileId:N}", CurrentRevision = 1,
            State = StorageProfileState.Active, CreatedDate = now, CreatedBy = actorId, LastModifiedDate = now, LastModifiedBy = actorId,
        };
        profile.Revisions.Add(new StorageProfileRevision
        {
            Id = Guid.NewGuid(), TeamId = teamId, StorageProfileId = profileId, Revision = 1,
            ProviderTypeKey = AliyunOssArtifactStorageDriverFactory.TypeKey, NonSecretConfigJson = config,
            CredentialRef = $"db:{credentialId:D}:1",
            NamespaceFingerprint = $"sha256:{Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(config))).ToLowerInvariant()}",
            CreatedDate = now, CreatedBy = actorId,
        });
        db.StorageProfile.Add(profile);

        db.StorageRoute.Add(new StorageRoute
        {
            Id = routeId, TeamId = teamId, DataClassTypeKey = "workflow-artifact/v1", CurrentRevision = 1,
            State = StorageRouteState.Draft, CreatedDate = now, CreatedBy = actorId, LastModifiedDate = now, LastModifiedBy = actorId,
            Revisions =
            {
                new StorageRouteRevision
                {
                    Id = Guid.NewGuid(), TeamId = teamId, StorageRouteId = routeId, Revision = 1, StorageProfileId = profileId,
                    ProfileRevisionMode = StorageProfileRevisionMode.CurrentAtWrite, PinnedProfileRevision = null,
                    CreatedDate = now, CreatedBy = actorId,
                },
            },
        });
        await db.SaveChangesAsync();
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE storage_route SET state = 'Active' WHERE id = {routeId}");

        return new OssWorld(teamId, actorId, profileId);
    }

    public void Dispose() => _oss.Dispose();

    private sealed record OssWorld(Guid TeamId, Guid ActorId, Guid ProfileId);
}
