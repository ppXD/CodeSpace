using CodeSpace.Core.Services.Workflows.Artifacts.Providers;
using CodeSpace.Core.Services.Workflows.Artifacts.Providers.AliyunOss;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows.Artifacts.Providers.AliyunOss;

/// <summary>
/// The factory is the only place a plaintext OSS secret is ever materialized, so every rejection it can produce has
/// to be typed and free of credential material. Anything it throws is what the runtime broker turns into
/// <c>ConfigurationInvalid</c>, and that resolution is surfaced to operators.
/// </summary>
[Trait("Category", "Unit")]
public sealed class AliyunOssArtifactStorageDriverFactoryTests : IDisposable
{
    private const string Secret = "wJalrXUtnFEMIK7MDENGbPxRfiCYFAKESECRET";
    private readonly FakeAliyunOssHandler _oss = new();

    [Theory]
    [InlineData("""{"region":"cn-hangzhou","bucket":"codespace-artifacts"}""")]
    [InlineData("""{"endpoint":"oss-cn-hangzhou.aliyuncs.com","region":"cn-hangzhou"}""")]
    [InlineData("""{"endpoint":"ftp://oss.example.com","region":"cn-hangzhou","bucket":"codespace-artifacts"}""")]
    [InlineData("""{"endpoint":"oss-cn-hangzhou.aliyuncs.com","region":"cn hangzhou","bucket":"codespace-artifacts"}""")]
    // A dot segment survives the schema pattern, but .NET's Uri compresses it out of the request path while the V4
    // signature covers the literal form, so such a profile could only ever 403. It must die at activation, not on the wire.
    [InlineData("""{"endpoint":"oss-cn-hangzhou.aliyuncs.com","region":"cn-hangzhou","bucket":"codespace-artifacts","keyPrefix":"../"}""")]
    [InlineData("""{"endpoint":"oss-cn-hangzhou.aliyuncs.com","region":"cn-hangzhou","bucket":"codespace-artifacts","keyPrefix":"team-7/../"}""")]
    [InlineData("""{"endpoint":"oss-cn-hangzhou.aliyuncs.com","region":"cn-hangzhou","bucket":"codespace-artifacts","keyPrefix":"team-7//"}""")]
    public async Task An_unusable_configuration_is_rejected_before_any_request_is_signed(string configJson)
    {
        using var document = System.Text.Json.JsonDocument.Parse(configJson);
        var profile = AliyunOssTestProfile.Snapshot() with { Configuration = document.RootElement };

        await Should.ThrowAsync<ArgumentException>(() => CreateAsync(profile).AsTask());

        _oss.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_profile_belonging_to_another_provider_or_schema_version_is_refused()
    {
        await Should.ThrowAsync<ArgumentException>(() => CreateAsync(AliyunOssTestProfile.Snapshot() with { ProviderTypeKey = "local-rwx/v1" }).AsTask());
        await Should.ThrowAsync<NotSupportedException>(() => CreateAsync(AliyunOssTestProfile.Snapshot() with { SchemaVersion = 99 }).AsTask());
    }

    [Fact]
    public async Task A_missing_credential_is_refused_because_oss_has_no_anonymous_write_path()
    {
        var factory = new AliyunOssArtifactStorageDriverFactory(_oss, TimeProvider.System);

        var error = await Should.ThrowAsync<ArgumentException>(() => factory.CreateAsync(new ArtifactStorageDriverCreateRequest(AliyunOssTestProfile.Snapshot()), CancellationToken.None).AsTask());

        error.Message.ShouldNotContain(Secret);
    }

    [Theory]
    [InlineData(""" {"accessKeyId":"LTAI5tFakeAccessKeyId"} """)]
    [InlineData(""" {"accessKeyId":"","accessKeySecret":"wJalrXUtnFEMIK7MDENGbPxRfiCYFAKESECRET"} """)]
    [InlineData(""" {"accessKeyId":"LTAI5tFakeAccessKeyId","accessKeySecret":"wJalrXUtnFEMIK7MDENGbPxRfiCYFAKESECRET","stolen":"wJalrXUtnFEMIK7MDENGbPxRfiCYFAKESECRET"} """)]
    public async Task An_invalid_secret_is_rejected_without_repeating_the_secret(string secretJson)
    {
        using var document = System.Text.Json.JsonDocument.Parse(secretJson);
        using var credential = new StorageCredentialHandle(document.RootElement);

        var error = await Should.ThrowAsync<ArgumentException>(() => CreateAsync(AliyunOssTestProfile.Snapshot(), credential).AsTask());

        error.ToString().ShouldNotContain(Secret);
    }

    [Fact]
    public async Task The_credential_handle_is_not_retained_beyond_driver_creation()
    {
        var credential = AliyunOssTestProfile.Credential();

        await using var driver = await CreateAsync(AliyunOssTestProfile.Snapshot(), credential);
        credential.Dispose();

        var probe = await driver.ProbeAsync(new ArtifactStorageProbeRequest(), CancellationToken.None);
        probe.Status.ShouldBe(ArtifactStorageProbeStatus.Available, "the driver must have copied its signing material, not borrowed the disposed handle");
    }

    public void Dispose() => _oss.Dispose();

    private ValueTask<IArtifactStorageDriver> CreateAsync(StorageProfileSnapshot profile, StorageCredentialHandle? credential = null)
    {
        var factory = new AliyunOssArtifactStorageDriverFactory(_oss, TimeProvider.System);
        return factory.CreateAsync(new ArtifactStorageDriverCreateRequest(profile) { CredentialHandle = credential ?? AliyunOssTestProfile.Credential() }, CancellationToken.None);
    }
}
