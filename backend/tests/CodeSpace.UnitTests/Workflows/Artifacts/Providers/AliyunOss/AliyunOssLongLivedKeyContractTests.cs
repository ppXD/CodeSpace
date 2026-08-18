using System.Text;
using CodeSpace.Core.Services.Workflows.Artifacts.Providers;
using CodeSpace.Core.Services.Workflows.Artifacts.Providers.AliyunOss;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows.Artifacts.Providers.AliyunOss;

/// <summary>
/// Runs the whole conformance kit a second time over the credential shape the secret schema makes the default: a
/// long-lived AccessKey with no STS token. It is a different signed header set on the wire, and the integration test
/// that models an operator's real profile configures exactly this shape, so it cannot be left to the STS lane.
/// </summary>
[Trait("Category", "Unit")]
public sealed class AliyunOssLongLivedKeyContractTests : ArtifactStorageDriverConformanceTests, IDisposable
{
    private readonly FakeAliyunOssHandler _oss = new();

    [Fact]
    public async Task A_long_lived_access_key_signs_every_request_without_an_sts_token_header()
    {
        await using var driver = await CreateDriverAsync();
        await using var input = new MemoryStream(Encoding.UTF8.GetBytes("long lived"));

        var stored = await driver.PutAsync(new ArtifactStoragePutRequest("sts/absent", input) { Condition = ArtifactStorageWriteCondition.CreateOnly }, CancellationToken.None);

        stored.IsSuccess.ShouldBeTrue(stored.Error?.Message);
        _oss.SecurityTokens.ShouldNotBeEmpty();
        _oss.SecurityTokens.ShouldAllBe(token => token == null, "an AccessKey profile has no STS token, so the driver must omit the header rather than send an empty one");
    }

    public void Dispose() => _oss.Dispose();

    protected override async ValueTask<IArtifactStorageDriver> CreateDriverAsync()
    {
        var factory = new AliyunOssArtifactStorageDriverFactory(_oss, TimeProvider.System);
        using var credential = AliyunOssTestProfile.Credential(new { accessKeyId = FakeAliyunOssHandler.AccessKeyId, accessKeySecret = FakeAliyunOssHandler.AccessKeySecret });
        return await factory.CreateAsync(new ArtifactStorageDriverCreateRequest(AliyunOssTestProfile.Snapshot()) { CredentialHandle = credential }, CancellationToken.None);
    }
}
