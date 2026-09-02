using System.Collections.Concurrent;
using CodeSpace.Core.Services.Workflows.Artifacts.Providers;
using CodeSpace.Core.Services.Workflows.Artifacts.Providers.AliyunOss;

namespace CodeSpace.UnitTests.Workflows.Artifacts.Providers.AliyunOss;

/// <summary>
/// The SAME provider-neutral driver conformance kit the fake-backed lanes run, held against a REAL Aliyun OSS bucket.
/// It deliberately asserts no list of its own: the real service is held to exactly the contract the fake is held to,
/// so any divergence between the two IS the finding.
///
/// OPT-IN, and excluded from the normal unit lane by its <c>Category=RealBucket</c> trait. The one command:
/// <code>
/// export CODESPACE_OSS_BUCKET_NAME=my-bucket
/// export CODESPACE_OSS_ENDPOINT=oss-cn-hangzhou.aliyuncs.com
/// export CODESPACE_OSS_ACCESS_KEY_ID=LTAI...
/// read -rs CODESPACE_OSS_ACCESS_KEY_SECRET &amp;&amp; export CODESPACE_OSS_ACCESS_KEY_SECRET   # keeps the secret out of shell history
/// dotnet test backend/tests/CodeSpace.UnitTests/CodeSpace.UnitTests.csproj --filter "Category=RealBucket"
/// </code>
/// With any of the four unset, every case is a green no-op and the run says so LOUDLY - a skip is not a pass.
///
/// WHAT A PASS PROVES: one bucket, in one region, over one endpoint form, with one credential's permissions, at one
/// point in time, answered this driver the way the fake does.
///
/// WHAT IT DOES NOT PROVE: any other region or endpoint form (a <c>-internal</c> VPC or accelerate host routes and
/// signs differently); a bucket with versioning ENABLED (the driver requires it DISABLED and nothing here can tell
/// the difference - a versioned bucket keeps every staged upload as a non-current version and bills for it); a
/// narrower bucket policy than the one used (<c>ProbeAsync</c> lists the profile's own key prefix, so a credential
/// granted no listing at all still reds the probe case on permissions rather than on the driver); the 5 GiB simple-copy
/// ceiling the staged publish inherits (the kit's largest payload is 5 MiB + 17 bytes and this driver has NO
/// multipart path at all, so neither this lane nor the fake ever approaches an object OSS would refuse to copy); or
/// that any of it still holds tomorrow.
///
/// It writes only under its own dated run prefix and deletes every object key it handed the driver, pass or fail. The
/// driver discards its OWN staging and probe uploads best-effort, and this lane cannot reach those: they live outside
/// the object area every caller key is forced into. So a swallowed discard, or a hard-killed process, is what can
/// leave bytes behind - under <see cref="AliyunOssRealBucket.KeyPrefixRoot"/>, which is why the prefix names the run
/// and the date: one listing finds them and one prefix delete removes them.
/// </summary>
[Trait("Category", "RealBucket")]
public sealed class AliyunOssRealBucketConformanceTests : ArtifactStorageDriverConformanceTests, IAsyncLifetime
{
    /// <summary>One namespace per PROCESS, so the whole run's leftovers are one listing rather than one per case.</summary>
    private static readonly string RunPrefix = AliyunOssRealBucket.RunKeyPrefix(DateTimeOffset.UtcNow);

    private readonly ConcurrentBag<string> _written = [];
    private readonly AliyunOssRealBucketSettings? _settings;

    public AliyunOssRealBucketConformanceTests() : this(Environment.GetEnvironmentVariable) { }

    /// <summary>Explicit reader, so the skip path is pinned deterministically instead of depending on what the ambient environment happens to hold.</summary>
    internal AliyunOssRealBucketConformanceTests(Func<string, string?> readEnv)
    {
        _settings = AliyunOssRealBucket.TryRead(readEnv);
        if (_settings == null) AliyunOssRealBucket.ReportSkipped(AliyunOssRealBucket.Unset(readEnv));
    }

    protected override bool StoreIsReachable => _settings != null;

    public Task InitializeAsync() => Task.CompletedTask;

    /// <summary>Runs after every case, passed or failed, so a red run cleans up too.</summary>
    public async Task DisposeAsync() => await SweepAsync().ConfigureAwait(false);

    protected override async ValueTask<IArtifactStorageDriver> CreateDriverAsync() => new RecordingDriver(await OpenAsync().ConfigureAwait(false), _written);

    /// <summary>
    /// The one case here that points at a bucket OTHER than the operator's, and the only thing in this lane the fake
    /// cannot stand in for: whether a real OSS HEAD really does answer a 404 with no body, which is what makes a
    /// bucket-level and an object-level 404 indistinguishable and is the whole reason the driver re-asks. The fake
    /// models that from Aliyun's documentation; only the real service settles it.
    ///
    /// The name is a fresh GUID rather than the operator's bucket with a suffix, so it is a bucket that exists in no
    /// Aliyun account and cannot collide with one, and stays inside the 3-63 character bucket-name limit whatever the
    /// operator's own name is. Nothing is written through it - the kit's case only HEADs - so it is deliberately NOT
    /// wrapped in <c>RecordingDriver</c>: there is no key for the sweep to chase and no bucket to chase it in.
    /// </summary>
    protected override async ValueTask<IArtifactStorageDriver?> CreateDriverOverAbsentDestinationAsync()
    {
        if (_settings == null) return null;

        var absent = _settings with { BucketName = $"codespace-absent-{Guid.NewGuid():N}" };
        using var credential = AliyunOssRealBucket.Credential(absent);
        var request = new ArtifactStorageDriverCreateRequest(AliyunOssRealBucket.Profile(absent, RunPrefix)) { CredentialHandle = credential };

        return await new AliyunOssArtifactStorageDriverFactory().CreateAsync(request, CancellationToken.None).ConfigureAwait(false);
    }

    private async ValueTask<IArtifactStorageDriver> OpenAsync()
    {
        var settings = _settings ?? throw new InvalidOperationException($"The real-bucket lane must be SKIPPED, not opened, without {string.Join(", ", AliyunOssRealBucket.EnvVars)}.");
        using var credential = AliyunOssRealBucket.Credential(settings);
        var request = new ArtifactStorageDriverCreateRequest(AliyunOssRealBucket.Profile(settings, RunPrefix)) { CredentialHandle = credential };

        return await new AliyunOssArtifactStorageDriverFactory().CreateAsync(request, CancellationToken.None).ConfigureAwait(false);
    }

    private async Task SweepAsync()
    {
        if (_settings == null || _written.IsEmpty) return;

        try
        {
            await using var driver = await OpenAsync().ConfigureAwait(false);
            var survivors = await DeleteEachAsync(driver).ConfigureAwait(false);

            if (survivors.Count > 0) AliyunOssRealBucket.ReportLeftovers(RunPrefix, survivors.Count, $"the service refused {survivors.Count} delete(s)");
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // A cleanup fault must not surface as a conformance failure - that names the wrong culprit - but it must
            // not be silent either, or an abandoned prefix goes on being billed. Only the exception TYPE is reported:
            // a message could in principle carry material this lane is not allowed to publish.
            AliyunOssRealBucket.ReportLeftovers(RunPrefix, Recorded().Count, exception.GetType().Name);
        }
    }

    /// <summary>Deletes exactly the keys this case handed the driver. Missing counts as clean: a rejected checksum or a cancelled write records its key but never publishes one.</summary>
    private async Task<List<string>> DeleteEachAsync(IArtifactStorageDriver driver)
    {
        var survivors = new List<string>();

        foreach (var key in Recorded())
        {
            var removed = await driver.DeleteAsync(new ArtifactStorageDeleteRequest(key), CancellationToken.None).ConfigureAwait(false);
            if (!removed.Deleted && removed.Error?.Code != ArtifactStorageErrorCode.Missing) survivors.Add(key);
        }

        return survivors;
    }

    private List<string> Recorded() => _written.Distinct(StringComparer.Ordinal).ToList();

    /// <summary>
    /// Remembers every object key that was handed to the real bucket so the run can delete exactly what it created and
    /// nothing else. The key is recorded BEFORE the call, so a cancelled or half-published write is still swept.
    /// </summary>
    private sealed class RecordingDriver(IArtifactStorageDriver inner, ConcurrentBag<string> written) : IArtifactStorageDriver
    {
        public StorageProviderCapabilities Capabilities => inner.Capabilities;

        public ValueTask<ArtifactStoragePutResult> PutAsync(ArtifactStoragePutRequest request, CancellationToken cancellationToken)
        {
            written.Add(request.ObjectKey);

            return inner.PutAsync(request, cancellationToken);
        }

        public ValueTask<ArtifactStorageHeadResult> HeadAsync(ArtifactStorageHeadRequest request, CancellationToken cancellationToken) => inner.HeadAsync(request, cancellationToken);
        public ValueTask<ArtifactStorageReadResult> OpenReadAsync(ArtifactStorageReadRequest request, CancellationToken cancellationToken) => inner.OpenReadAsync(request, cancellationToken);
        public ValueTask<ArtifactStorageDeleteResult> DeleteAsync(ArtifactStorageDeleteRequest request, CancellationToken cancellationToken) => inner.DeleteAsync(request, cancellationToken);
        public ValueTask<ArtifactStorageProbeResult> ProbeAsync(ArtifactStorageProbeRequest request, CancellationToken cancellationToken) => inner.ProbeAsync(request, cancellationToken);
        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }
}
