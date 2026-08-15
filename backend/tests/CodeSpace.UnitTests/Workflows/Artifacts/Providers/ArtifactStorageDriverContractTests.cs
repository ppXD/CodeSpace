using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CodeSpace.Core.Services.Workflows.Artifacts.Providers;
using CodeSpace.Core.Services.Workflows.Artifacts.Providers.Local;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows.Artifacts.Providers;

[Trait("Category", "Unit")]
public sealed class LocalRwxArtifactStorageDriverContractTests : ArtifactStorageDriverConformanceTests, IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "codespace-storage-driver-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void Profile_snapshot_is_versioned_and_serializes_only_a_secret_reference()
    {
        var secretReference = new StorageSecretReference("vault/v1", "storage/team-7/profile-3", "42");
        var profile = Profile(secretReference);
        var request = new ArtifactStorageDriverCreateRequest(profile)
        {
            CredentialHandle = new StorageCredentialHandle("credential-handle-9", secretReference, DateTimeOffset.UtcNow.AddMinutes(5))
        };
        var json = JsonSerializer.Serialize(profile);
        var requestJson = JsonSerializer.Serialize(request);

        profile.SchemaVersion.ShouldBe(StorageProfileSnapshot.CurrentSchemaVersion);
        json.ShouldContain("storage/team-7/profile-3");
        requestJson.ShouldContain("credential-handle-9");
        json.ShouldNotContain("accessKeySecret", Case.Insensitive);
        json.ShouldNotContain("plaintext", Case.Insensitive);
    }

    [Fact]
    public void Local_module_exposes_the_inert_driver_factory_without_activating_the_legacy_backend()
    {
        var module = new LocalRwxStorageProviderModule();
        var catalog = new StorageProviderModuleCatalog([module]);

        module.FactoryType.ShouldBe(typeof(LocalRwxArtifactStorageDriverFactory));
        (module.Capabilities & StorageProviderCapabilities.StreamingWrite).ShouldBe(StorageProviderCapabilities.StreamingWrite);
        (module.Capabilities & StorageProviderCapabilities.StreamingRead).ShouldBe(StorageProviderCapabilities.StreamingRead);
        (module.Capabilities & StorageProviderCapabilities.RangeRead).ShouldBe(StorageProviderCapabilities.RangeRead);
        (module.Capabilities & StorageProviderCapabilities.ConditionalCreate).ShouldBe(StorageProviderCapabilities.ConditionalCreate);
        (module.Capabilities & StorageProviderCapabilities.ProviderChecksum).ShouldBe(StorageProviderCapabilities.None, "local stat/head is O(1); strict checksum readback belongs to the caller");
        catalog.Require(module.TypeKey).ShouldBeSameAs(module);
    }

    [Fact]
    public void Result_states_cannot_be_default_constructed_or_forged_with_init_only_payloads()
    {
        foreach (var type in new[]
                 {
                     typeof(ArtifactStoragePutResult), typeof(ArtifactStorageHeadResult),
                     typeof(ArtifactStorageReadResult), typeof(ArtifactStorageDeleteResult)
                 })
        {
            type.GetConstructors().ShouldBeEmpty($"{type.Name} must be created through its invariant-preserving factories");
            type.GetProperties().Where(property => property.SetMethod?.IsPublic == true).ShouldBeEmpty($"{type.Name} state cannot be forged after construction");
        }

        var error = new ArtifactStorageError(ArtifactStorageErrorCode.Missing, "missing");
        ArtifactStoragePutResult.Failed(error).IsSuccess.ShouldBeFalse();
        ArtifactStorageHeadResult.Failed(error).Metadata.ShouldBeNull();
        ArtifactStorageReadResult.Failed(error).Content.ShouldBeNull();
        ArtifactStorageDeleteResult.Failed(error).Deleted.ShouldBeFalse();
    }

    [Fact]
    public async Task Local_head_is_constant_cost_metadata_and_does_not_claim_a_native_checksum()
    {
        await using var driver = await CreateDriverAsync();
        await using var input = new MemoryStream(RandomNumberGenerator.GetBytes(1024 * 1024));
        var put = await driver.PutAsync(new ArtifactStoragePutRequest("head/constant-cost", input), CancellationToken.None);

        var head = await driver.HeadAsync(new ArtifactStorageHeadRequest("head/constant-cost"), CancellationToken.None);

        put.Metadata!.Sha256.ShouldNotBeNullOrWhiteSpace();
        head.Metadata!.Sha256.ShouldBeNull();
        head.Metadata.ETag.ShouldBe(put.Metadata.ETag);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { }
    }

    protected override async ValueTask<IArtifactStorageDriver> CreateDriverAsync()
    {
        var factory = new LocalRwxArtifactStorageDriverFactory();
        return await factory.CreateAsync(new ArtifactStorageDriverCreateRequest(Profile()), CancellationToken.None);
    }

    private StorageProfileSnapshot Profile(StorageSecretReference? secretReference = null) => new()
    {
        ProfileId = Guid.NewGuid(),
        ProfileRevision = 7,
        ProviderTypeKey = "local-rwx/v1",
        Configuration = JsonSerializer.SerializeToElement(new { rootPath = _root }),
        SecretReference = secretReference
    };

}
