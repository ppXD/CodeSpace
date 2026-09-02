using System.Text.Json;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Services.Workflows.Artifacts.Providers;
using CodeSpace.Core.Services.Workflows.Artifacts.Providers.AliyunOss;
using CodeSpace.Core.Services.Workflows.Artifacts.Providers.Local;
using CodeSpace.Core.Services.Workflows.Artifacts.Runtime;
using CodeSpace.IntegrationTests.Workflows.Artifacts.Providers.AliyunOss;
using CodeSpace.Messages.Dtos.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows.Artifacts.Runtime;

/// <summary>
/// Qualifying a destination nobody has saved, over the real provider modules and the real driver factory with only
/// the HTTP transport faked.
///
/// <para>What every case here is ultimately protecting: a <c>storage_profile</c> cannot be deleted, so the value of
/// this seam is entirely in what it does NOT leave behind. A refusal that reached the database first would be worse
/// than no test at all - it would hand an operator a permanent row as the price of a typo.</para>
/// </summary>
[Trait("Category", "Unit")]
public sealed class StorageConfigurationProbeServiceTests : IDisposable
{
    private const string OssTypeKey = "aliyun-oss/v1";
    private readonly FakeAliyunOssHandler _oss = new();
    private readonly List<string> _roots = [];

    public enum Refusal
    {
        UnknownProvider,
        ConfigurationIncomplete,
        ConfigurationCarriesASecret,
        SecretMissing,
        SecretIncomplete,
        SecretNotAnObject,
    }

    [Theory]
    [InlineData(Refusal.UnknownProvider, StorageProfileProbeFailureStageValue.Provider, StorageProfileProbeFailureCodeValue.ProviderModuleMissing)]
    [InlineData(Refusal.ConfigurationIncomplete, StorageProfileProbeFailureStageValue.Configuration, StorageProfileProbeFailureCodeValue.ConfigurationInvalid)]
    [InlineData(Refusal.ConfigurationCarriesASecret, StorageProfileProbeFailureStageValue.Configuration, StorageProfileProbeFailureCodeValue.ConfigurationInvalid)]
    [InlineData(Refusal.SecretMissing, StorageProfileProbeFailureStageValue.Credential, StorageProfileProbeFailureCodeValue.CredentialMissing)]
    [InlineData(Refusal.SecretIncomplete, StorageProfileProbeFailureStageValue.Credential, StorageProfileProbeFailureCodeValue.CredentialSecretInvalid)]
    [InlineData(Refusal.SecretNotAnObject, StorageProfileProbeFailureStageValue.Credential, StorageProfileProbeFailureCodeValue.CredentialSecretInvalid)]
    public async Task Input_the_save_path_would_refuse_is_refused_here_too_and_never_reaches_the_provider(Refusal refusal, StorageProfileProbeFailureStageValue stage, StorageProfileProbeFailureCodeValue code)
    {
        var request = Refused(refusal);

        var result = await Service().ProbeAsync(request, CancellationToken.None);

        result.Status.ShouldBe(StorageProfileProbeStatusValue.Unavailable);
        result.Failure.ShouldNotBeNull().Stage.ShouldBe(stage);
        result.Failure!.Code.ShouldBe(code);
        result.Failure.Retryable.ShouldBeFalse("nothing about a value an operator typed comes good on a retry");
        _oss.Calls.ShouldBeEmpty("a value the save path would refuse must not be sent to a provider at all");
    }

    [Fact]
    public async Task A_destination_that_answers_qualifies_and_leaves_no_object_behind()
    {
        var result = await Service().ProbeAsync(OssRequest(), CancellationToken.None);

        result.Status.ShouldBe(StorageProfileProbeStatusValue.Available, result.Failure?.Code.ToString());
        result.Failure.ShouldBeNull();
        result.ProviderTypeKey.ShouldBe(OssTypeKey);
        _oss.Calls.ShouldContain(call => call.StartsWith("GET /?list-type=2", StringComparison.Ordinal), "a write-verifying probe reads first");
        _oss.Calls.ShouldContain(call => call.StartsWith("PUT /codespace/.codespace/probe/", StringComparison.Ordinal), "and proves a write, since a destination that cannot take bytes is not worth saving");
        _oss.Keys.ShouldBeEmpty("the probe object it wrote is the only trace it could leave, and it discards it");
    }

    /// <summary>
    /// The case the whole seam exists for: the operator mistyped the secret. The answer names the signature, so the
    /// remedy is "re-enter the secret" rather than a hunt through endpoint, region and bucket - and it costs nothing
    /// but a retry, because there is no profile and no credential to unpick.
    /// </summary>
    [Fact]
    public async Task A_wrong_secret_is_named_as_a_signature_mismatch()
    {
        _oss.RejectEverySignature = true;

        var result = await Service().ProbeAsync(OssRequest(), CancellationToken.None);

        result.Status.ShouldBe(StorageProfileProbeStatusValue.Unavailable);
        result.Failure.ShouldNotBeNull().Code.ShouldBe(StorageProfileProbeFailureCodeValue.ProbeSignatureMismatch);
    }

    /// <summary>A provider with no secret inputs at all is qualified with no secret, which is not a credential failure.</summary>
    [Fact]
    public async Task A_provider_that_needs_no_secret_qualifies_without_one()
    {
        var root = Path.Combine(Path.GetTempPath(), $"codespace-probe-{Guid.NewGuid():N}");
        _roots.Add(root);

        var result = await Service().ProbeAsync(new StorageConfigurationProbeRequest(LocalRwxArtifactStorageDriverFactory.TypeKey, Json(new { rootPath = root }), null), CancellationToken.None);

        result.Status.ShouldBe(StorageProfileProbeStatusValue.Available, result.Failure?.Code.ToString());
    }

    /// <summary>
    /// The invariant the seam's whole value rests on, pinned where it cannot drift: this service has no database. A
    /// later change that gave it one - to record the attempt, say - would silently reintroduce the permanent row this
    /// exists to avoid, and every behavioural test above would still pass.
    /// </summary>
    [Fact]
    public void The_probe_holds_no_database_so_it_cannot_leave_a_row_behind()
    {
        var fields = typeof(StorageConfigurationProbeService).GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);

        fields.ShouldNotBeEmpty("a service with no fields at all would pass this vacuously");
        fields.ShouldNotContain(field => typeof(CodeSpaceDbContext).IsAssignableFrom(field.FieldType), "qualifying an unsaved destination must not be able to persist anything");
    }

    public void Dispose()
    {
        _oss.Dispose();
        foreach (var root in _roots.Where(Directory.Exists)) Directory.Delete(root, recursive: true);
    }

    private StorageConfigurationProbeService Service()
    {
        var modules = new StorageProviderModuleCatalog([new AliyunOssStorageProviderModule(), new LocalRwxStorageProviderModule()]);
        var factories = new ArtifactStorageDriverFactoryCatalog([new AliyunOssArtifactStorageDriverFactory(_oss), new LocalRwxArtifactStorageDriverFactory()], modules);

        return new StorageConfigurationProbeService(modules, factories, new StorageDriverActivator(NullLogger<StorageDriverActivator>.Instance));
    }

    private static StorageConfigurationProbeRequest OssRequest(object? configuration = null, object? secret = null) => new(
        OssTypeKey,
        Json(configuration ?? new { endpoint = FakeAliyunOssHandler.Host, region = FakeAliyunOssHandler.Region, bucket = FakeAliyunOssHandler.Bucket, keyPrefix = "codespace/" }),
        Json(secret ?? new { accessKeyId = FakeAliyunOssHandler.AccessKeyId, accessKeySecret = FakeAliyunOssHandler.AccessKeySecret, securityToken = FakeAliyunOssHandler.SecurityToken }));

    private static StorageConfigurationProbeRequest Refused(Refusal refusal) => refusal switch
    {
        Refusal.UnknownProvider => new StorageConfigurationProbeRequest("not-a-provider/v1", Json(new { endpoint = FakeAliyunOssHandler.Host }), Json(new { accessKeyId = "id", accessKeySecret = "secret" })),
        Refusal.ConfigurationIncomplete => OssRequest(configuration: new { endpoint = FakeAliyunOssHandler.Host, region = FakeAliyunOssHandler.Region }),
        Refusal.ConfigurationCarriesASecret => OssRequest(configuration: new { endpoint = FakeAliyunOssHandler.Host, region = FakeAliyunOssHandler.Region, bucket = FakeAliyunOssHandler.Bucket, accessKeySecret = FakeAliyunOssHandler.AccessKeySecret }),
        Refusal.SecretMissing => new StorageConfigurationProbeRequest(OssTypeKey, OssRequest().NonSecretConfig, null),
        Refusal.SecretIncomplete => OssRequest(secret: new { accessKeyId = FakeAliyunOssHandler.AccessKeyId }),
        _ => new StorageConfigurationProbeRequest(OssTypeKey, OssRequest().NonSecretConfig, JsonSerializer.SerializeToElement("a string is not a secret envelope")),
    };

    private static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value);
}
