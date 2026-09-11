using System.Globalization;
using System.Net;
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
    /// <summary>Where this fixture's profile stages, spelled out rather than derived, so a change to either half of the key is visible here as a diff.</summary>
    private const string StagingArea = "codespace/.codespace/staging/";

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

    /// <summary>
    /// The write stages at the key its CALLER minted, not one the driver keeps to itself. That is what makes a killed
    /// writer's leftover findable: the caller records that key durably before the upload starts, so bytes that reach
    /// the bucket are named in the database whether or not the process survives to clean them up.
    /// </summary>
    [Fact]
    public async Task A_staged_write_occupies_the_temporary_object_its_caller_minted()
    {
        await using var driver = await CreateDriverAsync();
        var staging = ((IArtifactStorageStagingReclaimer)driver).MintStagingObjectKey();
        await using var input = new MemoryStream(Encoding.UTF8.GetBytes("staged where the caller said"));

        var stored = await driver.PutAsync(new ArtifactStoragePutRequest("staging/caller-minted", input) { StagingObjectKey = staging }, CancellationToken.None);

        stored.IsSuccess.ShouldBeTrue(stored.Error?.Message);
        staging.ShouldStartWith(StagingArea, Case.Sensitive, "a minted key must sit under the profile's own prefix in a reserved area that is a SIBLING of the published-object area, never inside it");
        _oss.Calls.ShouldContain($"PUT /{staging}", "the upload must go to the key the caller recorded, or the record names bytes that were never written");
        _oss.Keys.ShouldHaveSingleItem().ShouldBe("codespace/objects/staging/caller-minted", "a write that returns has already run its own cleanup, so only the published object may survive it");
    }

    /// <summary>
    /// Both surfaces that take a staging key hold to one predicate, because a persisted value is what they are handed
    /// and it may be wrong. The published-object row is the one that matters: <c>objects/</c> is a sibling of the
    /// staging area, so a reclaimer told to delete a published object must refuse rather than obey — and it must
    /// refuse without a wire call, since a DELETE that reaches OSS has already destroyed the object.
    /// </summary>
    [Theory]
    [InlineData("codespace/objects/victim")]
    [InlineData("codespace/.codespace/staging/")]
    [InlineData("codespace/.codespace/staging/not-hex-and-far-too-short")]
    [InlineData("codespace/.codespace/staging/00112233445566778899aabbccddeeff/deeper")]
    [InlineData("someone-else/.codespace/staging/00112233445566778899aabbccddeeff")]
    public async Task A_temporary_object_key_this_destination_never_minted_is_refused_by_both_surfaces(string foreign)
    {
        await using var driver = await CreateDriverAsync();
        _oss.Stash(foreign, Encoding.UTF8.GetBytes("someone else's bytes"));
        _oss.Calls.Clear();
        await using var input = new MemoryStream(Encoding.UTF8.GetBytes("refused"));

        var written = await driver.PutAsync(new ArtifactStoragePutRequest("staging/foreign", input) { StagingObjectKey = foreign }, CancellationToken.None);
        var discarded = await ((IArtifactStorageStagingReclaimer)driver).DiscardStagingAsync(foreign, CancellationToken.None);

        written.Error!.Code.ShouldBe(ArtifactStorageErrorCode.InvalidRequest);
        discarded.Error!.Code.ShouldBe(ArtifactStorageErrorCode.InvalidRequest);
        _oss.Calls.ShouldBeEmpty("a key outside this driver's own staging area must be refused before it reaches the wire — a DELETE that arrives has already destroyed whatever was there");
        _oss.Keys.ShouldContain(foreign);
    }

    /// <summary>
    /// The reclaim itself, over the two states a recorded key can be in when a sweep reaches it: the writer was killed
    /// before its own cleanup ran and the bytes are still there, or the cleanup did run and they are not. Both must
    /// answer success, because the caller's next act is to clear the record — and a reclaim that reported the second
    /// case as a failure would leave every ordinary write's row naming a temporary object forever.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Reclaiming_a_temporary_object_succeeds_whether_or_not_its_bytes_are_still_there(bool stillStaged)
    {
        await using var driver = await CreateDriverAsync();
        var reclaimer = (IArtifactStorageStagingReclaimer)driver;
        var staging = reclaimer.MintStagingObjectKey();
        if (stillStaged) _oss.Stash(staging, Encoding.UTF8.GetBytes("bytes a killed writer never published"));

        var discarded = await reclaimer.DiscardStagingAsync(staging, CancellationToken.None);

        discarded.IsSuccess.ShouldBeTrue(discarded.Error?.Message);
        _oss.Keys.ShouldNotContain(staging);
    }

    /// <summary>
    /// A minted key's shape, pinned directly. Nothing enforces it at the destination — OSS accepts any key — so the
    /// nonce length and alphabet ARE the guard that a persisted value cannot walk out of the staging area, and a
    /// widening here silently widens what <see cref="Reclaiming_a_temporary_object_succeeds_whether_or_not_its_bytes_are_still_there"/>
    /// is allowed to delete.
    /// </summary>
    [Fact]
    public async Task A_minted_temporary_object_key_is_one_unguessable_segment_under_the_reserved_area()
    {
        await using var driver = await CreateDriverAsync();
        var reclaimer = (IArtifactStorageStagingReclaimer)driver;

        var minted = Enumerable.Range(0, 8).Select(_ => reclaimer.MintStagingObjectKey()).ToList();
        var nonces = minted.Select(key => key.Substring(StagingArea.Length)).ToList();

        AliyunOssArtifactStorageDriver.StagingNonceLength.ShouldBe(32, "a GUID in N format; shortening it shortens the only thing keeping a recorded key inside the staging area");
        minted.Distinct(StringComparer.Ordinal).Count().ShouldBe(minted.Count, "two writes to one object key must never share a temporary object");
        minted.ShouldAllBe(key => key.StartsWith(StagingArea, StringComparison.Ordinal));
        nonces.ShouldAllBe(nonce => nonce.Length == AliyunOssArtifactStorageDriver.StagingNonceLength);
        nonces.ShouldAllBe(nonce => nonce.All(character => char.IsAsciiDigit(character) || (character >= 'a' && character <= 'f')));
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

    /// <summary>
    /// An object above the ceiling this driver's staged publish inherits is refused as a TYPED answer and refused
    /// BEFORE the wire — driven by declared length alone, because the point of refusing up front is that the payload
    /// is never touched and a test that allocated one would be measuring the opposite.
    ///
    /// <para>The two silent alternatives are what this pins against. One is paying for the whole upload only to have
    /// the staging upload that would carry it refuse it; the other is a fallback to writing the
    /// destination key directly, which this driver must never do — an unverified object occupying a content-addressed
    /// key is the exact accident the staged publish exists to prevent. There is no third option to fall back TO: the
    /// driver speaks Put/Copy/Get/Head/Delete and has no multipart path.</para>
    /// </summary>
    [Fact]
    public async Task An_object_above_the_simple_copy_ceiling_is_refused_as_unsupported_before_a_byte_is_staged()
    {
        await using var driver = await CreateDriverAsync();
        await using var input = new LengthOnlyStream(AliyunOssArtifactStorageDriver.StagedPutCeilingBytes + 1);

        var result = await driver.PutAsync(new ArtifactStoragePutRequest("oversized/value", input), CancellationToken.None);

        // 5 GiB is Aliyun's documented single-PutObject ceiling: https://www.alibabacloud.com/help/en/oss/product-overview/limits (retrieved 2026-09-11).
        result.Error!.Code.ShouldBe(ArtifactStorageErrorCode.Unsupported,
            "no repair and no retry makes this destination able to publish an object that large, so the code must be the one the CAS plane reads as final rather than a provider blip");
        result.Error.IsRetryable.ShouldBeFalse();
        result.Error.Message.ShouldContain(AliyunOssArtifactStorageDriver.StagedPutCeilingBytes.ToString(CultureInfo.InvariantCulture), Case.Sensitive, "an operator has to be told the limit, not just that one was hit");
        _oss.Calls.ShouldBeEmpty("a refusal reached after the stage has already uploaded the payload is not a refusal an operator gets for free");
        _oss.Keys.ShouldBeEmpty("neither the destination key nor a staging object may exist after a write the driver refused outright");
    }

    /// <summary>
    /// The same ceiling as the destination itself enforces it. The client-side guard above is a floor and cannot be
    /// the only one: the real service owns the true limit, it may be narrower than the documented one for a bucket,
    /// region or storage class, and it moves without this code changing — so the refusal has to survive arriving on
    /// the wire, at the one step that has no retry and no alternative.
    ///
    /// <para>What must hold is that a refused COPY is a refused WRITE: a typed, non-retryable error, the destination
    /// key still empty, the staging object cleaned up, and — the half a fallback would break — no second PUT anywhere
    /// near the destination key.</para>
    /// </summary>
    [Fact]
    public async Task A_publish_the_destination_refuses_as_too_large_places_nothing_and_never_re_uploads()
    {
        await using var driver = await CreateDriverAsync();
        _oss.CopyRejection = (HttpStatusCode.BadRequest, "EntityTooLarge");
        await using var input = new MemoryStream(Encoding.UTF8.GetBytes("bytes the destination would stage but not copy"), writable: false);

        var result = await driver.PutAsync(new ArtifactStoragePutRequest("oversized/refused", input) { Condition = ArtifactStorageWriteCondition.CreateOnly }, CancellationToken.None);

        result.IsSuccess.ShouldBeFalse();
        result.Error!.ProviderCode.ShouldBe("EntityTooLarge", "the provider's own token is the only thing that tells an operator this was a size ceiling rather than any other 400");
        result.Error.Code.ShouldBe(ArtifactStorageErrorCode.Unsupported, "a same-fact refusal must carry the same CAS code as the client-side ceiling guard, not the generic InvalidRequest every other 400 gets");
        result.Error.IsRetryable.ShouldBeFalse("a copy the destination refuses on the object's size refuses it again every time; retrying only burns the upload again");
        _oss.Keys.ShouldBeEmpty("the destination key must stay empty and the staging upload must be discarded — a half-published write is the one outcome worse than a failed one");
        _oss.Copies.ShouldHaveSingleItem("one copy was attempted and refused; repeating it would only burn the upload again");
        _oss.Calls.Count(call => call.StartsWith("PUT /codespace/objects/", StringComparison.Ordinal)).ShouldBe(_oss.Copies.Count,
            "every write at the published-object area must BE that copy: answering a refused publish with a direct upload would place bytes at a content-addressed key with nothing having verified them");
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
        _oss.Rfc822Dates.ShouldAllBe(value => !string.IsNullOrWhiteSpace(value), "the official SDK emits the standard Date header alongside x-oss-date");
    }

    [Fact]
    public async Task Caller_cancellation_is_not_reclassified_as_a_retryable_network_failure_by_the_sdk_boundary()
    {
        await using var driver = await CreateDriverAsync();
        using var cancellation = new CancellationTokenSource();
        _oss.BlockEveryRequest = true;

        var pending = driver.ProbeAsync(new ArtifactStorageProbeRequest(), cancellation.Token).AsTask();
        await _oss.RequestStarted.Task;
        cancellation.Cancel();

        await Should.ThrowAsync<OperationCanceledException>(pending);
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
        head.Error.IsRetryable.ShouldBeFalse("retrying does not bring a deleted bucket back — and this bit is what separates it from a transient 5xx wearing the same code, which is the entire safety of corroborated abandonment");
        _oss.Calls.ShouldBe(["HEAD /codespace/objects/absent/value", "GET /?list-type=2&encoding-type=url&prefix=codespace%2F&max-keys=1"], "the HEAD carries no body to classify, so exactly one prefix-scoped re-ask must supply the token");
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

    /// <summary>
    /// The listing names the profile's own prefix, because Aliyun RAM expresses a prefix-scoped grant as a BUCKET
    /// resource plus an <c>oss:Prefix</c> condition. A listing that names no prefix fails that condition, so a
    /// credential holding the least-privilege policy Aliyun's own documentation recommends answered AccessDenied to a
    /// probe of a destination whose every read and write it could perform.
    /// </summary>
    [Fact]
    public async Task The_read_probe_lists_only_the_prefix_this_profile_writes_to()
    {
        await using var driver = await CreateDriverAsync();
        _oss.Calls.Clear();

        var probe = await driver.ProbeAsync(new ArtifactStorageProbeRequest(), CancellationToken.None);

        probe.Status.ShouldBe(ArtifactStorageProbeStatus.Available, probe.Error?.Message);
        _oss.Calls.ShouldHaveSingleItem().ShouldBe("GET /?list-type=2&encoding-type=url&prefix=codespace%2F&max-keys=1");
    }

    /// <summary>
    /// The theory's second row is the negative control: without it, a fixture that quietly authorized every listing
    /// would let the first row pass whether or not the driver names its prefix at all.
    /// </summary>
    [Theory]
    [InlineData("codespace/", ArtifactStorageProbeStatus.Available)]
    [InlineData("someone-else/", ArtifactStorageProbeStatus.Unavailable)]
    public async Task A_key_granted_listing_only_under_one_prefix_can_still_qualify_its_own_destination(string grantedPrefix, ArtifactStorageProbeStatus expected)
    {
        await using var driver = await CreateDriverAsync();
        _oss.ListGrantedOnlyForPrefix = grantedPrefix;

        var probe = await driver.ProbeAsync(new ArtifactStorageProbeRequest { VerifyWriteAccess = true }, CancellationToken.None);

        probe.Status.ShouldBe(expected, probe.Error?.Message);
    }

    public void Dispose() => _oss.Dispose();

    protected override async ValueTask<IArtifactStorageDriver> CreateDriverAsync()
    {
        var factory = new AliyunOssArtifactStorageDriverFactory(_oss);
        using var credential = AliyunOssTestProfile.Credential();
        return await factory.CreateAsync(new ArtifactStorageDriverCreateRequest(AliyunOssTestProfile.Snapshot()) { CredentialHandle = credential }, CancellationToken.None);
    }

    /// <summary>A profile naming a bucket the endpoint does not host - exactly what a mistyped <c>BucketName</c> produces.</summary>
    protected override async ValueTask<IArtifactStorageDriver?> CreateDriverOverAbsentDestinationAsync()
    {
        var factory = new AliyunOssArtifactStorageDriverFactory(_oss);
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

    /// <summary>
    /// A payload that exists only as a length. Nothing is allocated and nothing can be read, so it can declare a
    /// multi-gibibyte object on a laptop — and a guard that stopped checking the declared length would be caught here
    /// by the read it is not allowed to reach rather than by a soft assertion.
    /// </summary>
    private sealed class LengthOnlyStream(long length) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => length;
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException("An object above the publish ceiling must be refused without its payload being touched.");
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
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
