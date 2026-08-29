using System.Text;
using System.Text.Json;
using CodeSpace.Core.Services.Workflows.Artifacts.Providers;
using CodeSpace.Core.Services.Workflows.Artifacts.Providers.AliyunOss;
using Shouldly;
using CodeSpace.IntegrationTests.Workflows.Artifacts.Providers.AliyunOss;

namespace CodeSpace.UnitTests.Workflows.Artifacts.Providers.AliyunOss;

/// <summary>
/// Runs the provider-neutral driver conformance kit against the Aliyun OSS driver over an in-memory OSS endpoint,
/// then pins the behaviours the kit cannot express: real HTTP range semantics and credential hygiene.
/// </summary>
[Trait("Category", "Unit")]
public sealed class AliyunOssArtifactStorageDriverContractTests : ArtifactStorageDriverConformanceTests, IDisposable
{
    private readonly FakeAliyunOssHandler _oss = new();

    [Fact]
    public async Task Range_reads_are_real_http_range_requests_that_cut_mid_utf8_character()
    {
        await using var driver = await CreateDriverAsync();
        var payload = Encoding.UTF8.GetBytes("aé漢字");
        await StoreAsync(driver, "range/utf8", payload);

        var opened = await driver.OpenReadAsync(new ArtifactStorageReadRequest("range/utf8") { Range = new ArtifactStorageByteRange(2, 3) }, CancellationToken.None);

        opened.IsSuccess.ShouldBeTrue(opened.Error?.Message);
        opened.ContentLength.ShouldBe(3);
        opened.TotalLength.ShouldBe(9);
        (await DrainAsync(opened)).ShouldBe(payload[2..5], "the driver must return raw bytes across a multi-byte boundary, never a decoded or repaired string");
        _oss.Calls.ShouldContain(call => call.StartsWith("GET /", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_zero_length_range_returns_an_empty_body_and_the_true_total_length()
    {
        await using var driver = await CreateDriverAsync();
        await StoreAsync(driver, "range/empty", Encoding.UTF8.GetBytes("aé漢字"));
        _oss.Calls.Clear();

        var opened = await driver.OpenReadAsync(new ArtifactStorageReadRequest("range/empty") { Range = new ArtifactStorageByteRange(4, 0) }, CancellationToken.None);

        opened.IsSuccess.ShouldBeTrue(opened.Error?.Message);
        opened.ContentLength.ShouldBe(0);
        opened.TotalLength.ShouldBe(9);
        (await DrainAsync(opened)).ShouldBeEmpty();
        _oss.Calls.ShouldAllBe(call => call.StartsWith("HEAD /", StringComparison.Ordinal), "HTTP cannot express a zero-length range, so the driver must not send an unsatisfiable one");
    }

    [Fact]
    public async Task A_range_starting_exactly_at_the_end_is_empty_and_past_the_end_is_invalid()
    {
        await using var driver = await CreateDriverAsync();
        await StoreAsync(driver, "range/edge", Encoding.UTF8.GetBytes("0123456789"));

        var atEnd = await driver.OpenReadAsync(new ArtifactStorageReadRequest("range/edge") { Range = new ArtifactStorageByteRange(10) }, CancellationToken.None);
        var beyond = await driver.OpenReadAsync(new ArtifactStorageReadRequest("range/edge") { Range = new ArtifactStorageByteRange(11) }, CancellationToken.None);

        atEnd.IsSuccess.ShouldBeTrue(atEnd.Error?.Message);
        atEnd.ContentLength.ShouldBe(0);
        atEnd.TotalLength.ShouldBe(10);
        beyond.Error!.Code.ShouldBe(ArtifactStorageErrorCode.InvalidRequest);
    }

    /// <summary>
    /// The fixture's bucket has versioning enabled, so every response carries <c>x-oss-version-id</c>. The driver must
    /// still report no version, because the CAS coordinator feeds a reported version straight back as the read and
    /// delete condition - and this driver refuses an <c>ExpectedVersion</c> outright.
    /// </summary>
    [Fact]
    public async Task A_versioning_enabled_bucket_never_yields_a_version_the_driver_would_refuse_back()
    {
        await using var driver = await CreateDriverAsync();
        await StoreAsync(driver, "version/round-trip", Encoding.UTF8.GetBytes("versioned"));

        var head = await driver.HeadAsync(new ArtifactStorageHeadRequest("version/round-trip"), CancellationToken.None);
        var read = await driver.OpenReadAsync(new ArtifactStorageReadRequest("version/round-trip") { ExpectedETag = head.Metadata!.ETag, ExpectedVersion = head.Metadata.Version }, CancellationToken.None);
        var removed = await driver.DeleteAsync(new ArtifactStorageDeleteRequest("version/round-trip") { ExpectedETag = head.Metadata.ETag, ExpectedVersion = head.Metadata.Version }, CancellationToken.None);

        head.Metadata.Version.ShouldBeNull("the bucket sends x-oss-version-id, but the module does not declare ObjectVersioning");
        read.IsSuccess.ShouldBeTrue(read.Error?.Message);
        (await DrainAsync(read)).ShouldBe(Encoding.UTF8.GetBytes("versioned"));
        removed.Deleted.ShouldBeTrue(removed.Error?.Message);
    }

    [Fact]
    public async Task An_unverified_object_is_never_placed_at_the_destination_key()
    {
        await using var driver = await CreateDriverAsync();
        await using var input = new MemoryStream(Encoding.UTF8.GetBytes("actual"));

        var result = await driver.PutAsync(new ArtifactStoragePutRequest("integrity/staged", input) { ExpectedSha256 = new string('a', 64) }, CancellationToken.None);

        result.Error!.Code.ShouldBe(ArtifactStorageErrorCode.IntegrityMismatch);
        _oss.Keys.ShouldBeEmpty("a failed checksum must leave neither the destination object nor its staging upload behind");
    }

    [Fact]
    public async Task User_metadata_and_content_type_survive_the_staged_publish()
    {
        await using var driver = await CreateDriverAsync();
        await using var input = new MemoryStream(Encoding.UTF8.GetBytes("described"));

        var stored = await driver.PutAsync(new ArtifactStoragePutRequest("described/value", input)
        {
            ContentType = "application/json",
            Metadata = new Dictionary<string, string>(StringComparer.Ordinal) { ["run-id"] = "42" }
        }, CancellationToken.None);

        stored.IsSuccess.ShouldBeTrue(stored.Error?.Message);
        var head = await driver.HeadAsync(new ArtifactStorageHeadRequest("described/value"), CancellationToken.None);
        head.Metadata!.ContentType.ShouldBe("application/json", "the server-side copy must carry the staged object's descriptors, not drop them");
        head.Metadata.Metadata["run-id"].ShouldBe("42");
    }

    [Theory]
    [InlineData("bad name", "42")]
    [InlineData("run-id", "\u00e9")]
    public async Task Metadata_that_http_headers_cannot_carry_is_rejected_before_a_partial_upload(string name, string value)
    {
        await using var driver = await CreateDriverAsync();
        await using var input = new MemoryStream(Encoding.UTF8.GetBytes("rejected"));

        var result = await driver.PutAsync(new ArtifactStoragePutRequest("metadata/value", input)
        {
            Metadata = new Dictionary<string, string>(StringComparer.Ordinal) { [name] = value }
        }, CancellationToken.None);

        result.Error!.Code.ShouldBe(ArtifactStorageErrorCode.InvalidRequest);
        _oss.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_stream_that_cannot_report_its_length_is_rejected_rather_than_buffered()
    {
        await using var driver = await CreateDriverAsync();
        await using var input = new UnmeasurableStream(Encoding.UTF8.GetBytes("streamed"));

        var result = await driver.PutAsync(new ArtifactStoragePutRequest("unmeasured/value", input), CancellationToken.None);

        result.Error!.Code.ShouldBe(ArtifactStorageErrorCode.InvalidRequest);
        result.Error.Message.ShouldContain("ContentLength", Case.Sensitive, "OSS rejects a chunked PutObject, so the caller must declare the size");
    }

    [Fact]
    public async Task Provider_failures_never_echo_credential_material_that_the_endpoint_sent_back()
    {
        await using var driver = await CreateDriverAsync();
        _oss.RejectEverySignature = true;

        var head = await driver.HeadAsync(new ArtifactStorageHeadRequest("denied/value"), CancellationToken.None);
        var read = await driver.OpenReadAsync(new ArtifactStorageReadRequest("denied/value"), CancellationToken.None);
        var probe = await driver.ProbeAsync(new ArtifactStorageProbeRequest { VerifyWriteAccess = true }, CancellationToken.None);

        head.Error!.Code.ShouldBe(ArtifactStorageErrorCode.Unauthorized);
        head.Error.ProviderCode.ShouldBe("SignatureDoesNotMatch");
        foreach (var text in new[] { head.Error.Message, read.Error!.Message, probe.Error!.Message, driver.ToString()!, driver.Capabilities.ToString() })
        {
            text.ShouldNotContain(FakeAliyunOssHandler.AccessKeySecret);
            text.ShouldNotContain(FakeAliyunOssHandler.SecurityToken);
            text.ShouldNotContain("StringToSign");
        }
    }

    [Fact]
    public async Task The_driver_holds_no_logger_so_no_template_can_carry_a_secret()
    {
        await using var driver = await CreateDriverAsync();

        var fields = driver.GetType().GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);

        fields.ShouldNotContain(field => field.FieldType.FullName!.Contains("Logger", StringComparison.Ordinal), "a driver that never logs cannot leak a secret through a log template");
        fields.Select(field => field.GetValue(driver)).OfType<string>().ShouldNotContain(FakeAliyunOssHandler.AccessKeySecret);
    }

    [Fact]
    public async Task Every_signed_request_carries_v4_material_and_the_sts_token_but_never_the_secret()
    {
        await using var driver = await CreateDriverAsync();
        await StoreAsync(driver, "signed/value", Encoding.UTF8.GetBytes("signed"));

        _oss.Authorizations.ShouldNotBeEmpty();
        _oss.Authorizations.ShouldAllBe(header => header.StartsWith("OSS4-HMAC-SHA256 Credential=", StringComparison.Ordinal));
        _oss.Authorizations.ShouldAllBe(header => !header.Contains(FakeAliyunOssHandler.AccessKeySecret, StringComparison.Ordinal));
        _oss.SecurityTokens.ShouldAllBe(token => token == FakeAliyunOssHandler.SecurityToken, "the endpoint accepts a request without an STS token, so the token must be asserted here rather than assumed from a 403");
    }

    /// <summary>
    /// Pins the exact code and the exact wire cost of what the kit only states as an invariant. A bucket-level 404 and
    /// an object-level 404 are the same bare status on a HEAD, so the driver must re-ask with a request whose failure
    /// carries a body - and the call log is asserted, because a verdict reached without that second request would be a
    /// guess that happened to be right against this fixture.
    /// </summary>
    [Fact]
    public async Task A_head_against_a_bucket_that_does_not_exist_reports_the_bucket_not_the_object()
    {
        await using var driver = await CreateDriverOverAbsentDestinationAsync();
        _oss.Calls.Clear();

        var head = await driver!.HeadAsync(new ArtifactStorageHeadRequest("absent/value"), CancellationToken.None);

        head.Error!.Code.ShouldBe(ArtifactStorageErrorCode.Unavailable, "a bucket that is not there is a profile an operator must fix, not an object the plane may treat as not yet written");
        head.Error.ProviderCode.ShouldBe("NoSuchBucket");
        head.Error.IsRetryable.ShouldBeTrue();
        _oss.Calls.ShouldBe(["HEAD /codespace/objects/absent/value", "GET /?list-type=2&max-keys=0"], "the HEAD carries no body to classify, so exactly one bucket-scoped re-ask must supply the token");
    }

    /// <summary>
    /// A HEAD miss on a bucket that IS there stays Missing, and the re-ask cannot promote a listing denial into the
    /// object's own verdict. A credential without <c>oss:ListObjects</c> would otherwise turn every dedup miss into a
    /// permissions failure and stall every first upload of a new artifact.
    /// </summary>
    [Fact]
    public async Task A_head_miss_on_a_bucket_that_exists_stays_missing()
    {
        await using var driver = await CreateDriverAsync();

        var head = await driver.HeadAsync(new ArtifactStorageHeadRequest("missing/value"), CancellationToken.None);

        head.Error!.Code.ShouldBe(ArtifactStorageErrorCode.Missing);
        head.Error.ProviderCode.ShouldBeNull("a HEAD carries no body, so the driver has no token of its own to report and must not borrow one that does not change the verdict");
    }

    public void Dispose() => _oss.Dispose();

    protected override async ValueTask<IArtifactStorageDriver> CreateDriverAsync()
    {
        var factory = new AliyunOssArtifactStorageDriverFactory(_oss, TimeProvider.System);
        using var credential = AliyunOssTestProfile.Credential();
        return await factory.CreateAsync(new ArtifactStorageDriverCreateRequest(AliyunOssTestProfile.Snapshot()) { CredentialHandle = credential }, CancellationToken.None);
    }

    /// <summary>A profile naming a bucket the endpoint does not host - exactly what a mistyped <c>BucketName</c> produces.</summary>
    protected override async ValueTask<IArtifactStorageDriver?> CreateDriverOverAbsentDestinationAsync()
    {
        var factory = new AliyunOssArtifactStorageDriverFactory(_oss, TimeProvider.System);
        using var credential = AliyunOssTestProfile.Credential();
        var snapshot = AliyunOssTestProfile.Snapshot(new
        {
            endpoint = FakeAliyunOssHandler.Host,
            region = FakeAliyunOssHandler.Region,
            bucket = FakeAliyunOssHandler.Bucket + "-typo",
            keyPrefix = "codespace/"
        });

        return await factory.CreateAsync(new ArtifactStorageDriverCreateRequest(snapshot) { CredentialHandle = credential }, CancellationToken.None);
    }

    private static async Task StoreAsync(IArtifactStorageDriver driver, string key, byte[] payload)
    {
        await using var input = new MemoryStream(payload, writable: false);
        var stored = await driver.PutAsync(new ArtifactStoragePutRequest(key, input) { ContentLength = payload.LongLength, Condition = ArtifactStorageWriteCondition.CreateOnly }, CancellationToken.None);
        stored.IsSuccess.ShouldBeTrue(stored.Error?.Message);
    }

    /// <summary>A forward-only stream, as a sandbox or a network source would hand one over.</summary>
    private sealed class UnmeasurableStream(byte[] bytes) : MemoryStream(bytes, writable: false)
    {
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
    }

    private static async Task<byte[]> DrainAsync(ArtifactStorageReadResult result)
    {
        await using var content = result.Content!;
        using var copy = new MemoryStream();
        await content.CopyToAsync(copy, CancellationToken.None);
        return copy.ToArray();
    }
}

/// <summary>Shared, secret-free profile/credential material for the Aliyun OSS unit lane.</summary>
internal static class AliyunOssTestProfile
{
    public static StorageProfileSnapshot Snapshot(object? configuration = null) => new()
    {
        ProfileId = Guid.NewGuid(),
        ProfileRevision = 3,
        ProviderTypeKey = AliyunOssArtifactStorageDriverFactory.TypeKey,
        Configuration = JsonSerializer.SerializeToElement(configuration ?? new
        {
            endpoint = FakeAliyunOssHandler.Host,
            region = FakeAliyunOssHandler.Region,
            bucket = FakeAliyunOssHandler.Bucket,
            keyPrefix = "codespace/"
        }),
        SecretReference = new StorageSecretReference("database/v1", Guid.NewGuid().ToString("D"), "4")
    };

    public static StorageCredentialHandle Credential(object? secret = null) => new(JsonSerializer.SerializeToElement(secret ?? new
    {
        accessKeyId = FakeAliyunOssHandler.AccessKeyId,
        accessKeySecret = FakeAliyunOssHandler.AccessKeySecret,
        securityToken = FakeAliyunOssHandler.SecurityToken
    }));
}
