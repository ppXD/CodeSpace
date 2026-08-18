using System.Security.Cryptography;
using System.Text;
using CodeSpace.Core.Services.Workflows.Artifacts.Providers;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows.Artifacts.Providers;

/// <summary>Provider-neutral suite inherited by every artifact storage driver implementation.</summary>
public abstract class ArtifactStorageDriverConformanceTests
{
    protected abstract ValueTask<IArtifactStorageDriver> CreateDriverAsync();

    [Fact]
    public async Task Conforms_for_zero_and_large_streams_without_byte_array_contracts()
    {
        await using var driver = await CreateDriverAsync();

        foreach (var bytes in new[] { Array.Empty<byte>(), RandomNumberGenerator.GetBytes(5 * 1024 * 1024 + 17) })
        {
            var key = $"objects/{Guid.NewGuid():N}";
            await using var input = new MemoryStream(bytes, writable: false);
            var put = await driver.PutAsync(new ArtifactStoragePutRequest(key, input)
            {
                ContentLength = bytes.LongLength,
                ExpectedSha256 = Sha256(bytes),
                Condition = ArtifactStorageWriteCondition.CreateOnly
            }, CancellationToken.None);

            put.IsSuccess.ShouldBeTrue(put.Error?.Message);
            put.Metadata.ShouldNotBeNull();
            put.Metadata.Length.ShouldBe(bytes.LongLength);
            put.Metadata.Sha256.ShouldBe(Sha256(bytes));
            put.Metadata.ETag.ShouldNotBeNullOrWhiteSpace();
            if ((driver.Capabilities & StorageProviderCapabilities.ObjectVersioning) != 0) put.Metadata.Version.ShouldNotBeNullOrWhiteSpace();
            else put.Metadata.Version.ShouldBeNull("a driver that does not declare ObjectVersioning must report no version: callers round-trip this field back as ExpectedVersion, so reporting one it will refuse makes every write unverifiable and every read unavailable");

            var head = await driver.HeadAsync(new ArtifactStorageHeadRequest(key), CancellationToken.None);
            head.IsSuccess.ShouldBeTrue(head.Error?.Message);
            head.Metadata!.ETag.ShouldBe(put.Metadata.ETag);
            head.Metadata.Version.ShouldBe(put.Metadata.Version);

            // Exactly the round trip ArtifactCasRuntimeCoordinator performs: whatever HEAD reports is fed straight
            // back as the read condition, so a driver may only report conditions it will honour.
            var conditional = await driver.OpenReadAsync(new ArtifactStorageReadRequest(key) { ExpectedETag = head.Metadata.ETag, ExpectedVersion = head.Metadata.Version }, CancellationToken.None);
            conditional.IsSuccess.ShouldBeTrue(conditional.Error?.Message);
            if (conditional.Content != null) await conditional.Content.DisposeAsync();

            var opened = await driver.OpenReadAsync(new ArtifactStorageReadRequest(key), CancellationToken.None);
            opened.IsSuccess.ShouldBeTrue(opened.Error?.Message);
            await using var content = opened.Content!;
            using var copy = new MemoryStream();
            await content.CopyToAsync(copy, CancellationToken.None);
            copy.ToArray().ShouldBe(bytes);
        }
    }

    [Fact]
    public async Task Supports_bounded_range_reads()
    {
        await using var driver = await CreateDriverAsync();
        await PutUtf8Async(driver, "range/value", "0123456789");

        var opened = await driver.OpenReadAsync(new ArtifactStorageReadRequest("range/value") { Range = new ArtifactStorageByteRange(3, 4) }, CancellationToken.None);

        opened.IsSuccess.ShouldBeTrue(opened.Error?.Message);
        opened.ContentLength.ShouldBe(4);
        opened.TotalLength.ShouldBe(10);
        await using var content = opened.Content!;
        using var reader = new StreamReader(content);
        (await reader.ReadToEndAsync(CancellationToken.None)).ShouldBe("3456");
    }

    [Fact]
    public async Task Conditional_create_is_atomic_and_does_not_replace_existing_bytes()
    {
        await using var driver = await CreateDriverAsync();
        await PutUtf8Async(driver, "conditional/value", "first");
        await using var replacement = new MemoryStream(Encoding.UTF8.GetBytes("second"));

        var duplicate = await driver.PutAsync(new ArtifactStoragePutRequest("conditional/value", replacement) { Condition = ArtifactStorageWriteCondition.CreateOnly }, CancellationToken.None);

        duplicate.IsSuccess.ShouldBeFalse();
        duplicate.Error!.Code.ShouldBe(ArtifactStorageErrorCode.AlreadyExists);
        (await ReadUtf8Async(driver, "conditional/value")).ShouldBe("first");
    }

    [Fact]
    public async Task Concurrent_conditional_creates_publish_exactly_one_complete_object()
    {
        await using var driver = await CreateDriverAsync();
        await using var first = new MemoryStream(Encoding.UTF8.GetBytes("first"));
        await using var second = new MemoryStream(Encoding.UTF8.GetBytes("second"));

        var results = await Task.WhenAll(
            driver.PutAsync(new ArtifactStoragePutRequest("conditional/race", first) { Condition = ArtifactStorageWriteCondition.CreateOnly }, CancellationToken.None).AsTask(),
            driver.PutAsync(new ArtifactStoragePutRequest("conditional/race", second) { Condition = ArtifactStorageWriteCondition.CreateOnly }, CancellationToken.None).AsTask());

        results.Count(result => result.IsSuccess).ShouldBe(1);
        results.Count(result => result.Error?.Code == ArtifactStorageErrorCode.AlreadyExists).ShouldBe(1);
        (await ReadUtf8Async(driver, "conditional/race")).ShouldBeOneOf("first", "second");
    }

    [Fact]
    public async Task Rejects_checksum_mismatch_without_publishing_bytes()
    {
        await using var driver = await CreateDriverAsync();
        await using var input = new MemoryStream(Encoding.UTF8.GetBytes("actual"));

        var result = await driver.PutAsync(new ArtifactStoragePutRequest("integrity/value", input)
        {
            ExpectedSha256 = new string('0', 64),
            Condition = ArtifactStorageWriteCondition.CreateOnly
        }, CancellationToken.None);

        result.IsSuccess.ShouldBeFalse();
        result.Error!.Code.ShouldBe(ArtifactStorageErrorCode.IntegrityMismatch);
        (await driver.HeadAsync(new ArtifactStorageHeadRequest("integrity/value"), CancellationToken.None)).Error!.Code.ShouldBe(ArtifactStorageErrorCode.Missing);
    }

    [Fact]
    public async Task Missing_objects_are_typed_and_probe_reports_a_usable_profile()
    {
        await using var driver = await CreateDriverAsync();

        var head = await driver.HeadAsync(new ArtifactStorageHeadRequest("missing/value"), CancellationToken.None);
        var read = await driver.OpenReadAsync(new ArtifactStorageReadRequest("missing/value"), CancellationToken.None);
        var probe = await driver.ProbeAsync(new ArtifactStorageProbeRequest { VerifyWriteAccess = true }, CancellationToken.None);

        head.Error!.Code.ShouldBe(ArtifactStorageErrorCode.Missing);
        read.Error!.Code.ShouldBe(ArtifactStorageErrorCode.Missing);
        read.Content.ShouldBeNull();
        probe.Status.ShouldBe(ArtifactStorageProbeStatus.Available);
        probe.Error.ShouldBeNull();
    }

    [Fact]
    public async Task Delete_removes_an_object_and_missing_delete_is_typed()
    {
        await using var driver = await CreateDriverAsync();
        await PutUtf8Async(driver, "delete/value", "delete me");

        (await driver.DeleteAsync(new ArtifactStorageDeleteRequest("delete/value"), CancellationToken.None)).Deleted.ShouldBeTrue();
        (await driver.DeleteAsync(new ArtifactStorageDeleteRequest("delete/value"), CancellationToken.None)).Error!.Code.ShouldBe(ArtifactStorageErrorCode.Missing);
    }

    [Fact]
    public async Task Cancellation_is_observed_before_a_write_can_publish()
    {
        await using var driver = await CreateDriverAsync();
        await using var input = new MemoryStream(RandomNumberGenerator.GetBytes(1024));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Should.ThrowAsync<OperationCanceledException>(() => driver.PutAsync(new ArtifactStoragePutRequest("cancelled/value", input), cancellation.Token).AsTask());
        (await driver.HeadAsync(new ArtifactStorageHeadRequest("cancelled/value"), CancellationToken.None)).Error!.Code.ShouldBe(ArtifactStorageErrorCode.Missing);
    }

    private static async Task PutUtf8Async(IArtifactStorageDriver driver, string key, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        await using var input = new MemoryStream(bytes, writable: false);
        var result = await driver.PutAsync(new ArtifactStoragePutRequest(key, input)
        {
            ContentLength = bytes.LongLength,
            ExpectedSha256 = Sha256(bytes),
            Condition = ArtifactStorageWriteCondition.CreateOnly
        }, CancellationToken.None);
        result.IsSuccess.ShouldBeTrue(result.Error?.Message);
    }

    private static async Task<string> ReadUtf8Async(IArtifactStorageDriver driver, string key)
    {
        var result = await driver.OpenReadAsync(new ArtifactStorageReadRequest(key), CancellationToken.None);
        result.IsSuccess.ShouldBeTrue(result.Error?.Message);
        await using var content = result.Content!;
        using var reader = new StreamReader(content);
        return await reader.ReadToEndAsync(CancellationToken.None);
    }

    private static string Sha256(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}
