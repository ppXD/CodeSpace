using System.Diagnostics;
using AlibabaCloud.OSS.V2;
using AlibabaCloud.OSS.V2.Models;

namespace CodeSpace.Core.Services.Workflows.Artifacts.Providers.AliyunOss;

/// <summary>
/// Streaming Aliyun OSS driver over Alibaba Cloud's official V2 SDK.
///
/// A write is staged, verified, then published with a server-side copy guarded by <c>x-oss-forbid-overwrite</c>. That
/// mirrors the local driver's temp-file-then-rename contract: unverified bytes never occupy the destination key, so a
/// checksum failure or a crash mid-upload cannot wedge a content-addressed key with content that does not match it.
/// The cost is a server-side copy per write, which caps a single object at the OSS simple-copy limit (5 GiB); artifact
/// payloads here are run logs and node outputs, orders of magnitude below it.
///
/// REQUIRES A VERSIONING-DISABLED BUCKET, and says so rather than failing quietly on one. The driver discards its
/// staging object with a plain DELETE, which on a versioning-enabled (or suspended) bucket inserts a delete marker and
/// KEEPS the staged bytes as a non-current version — so every write, successful or not, would retain a full second
/// copy of the payload and be billed for it forever. Aliyun also documents that <c>x-oss-forbid-overwrite</c> has no
/// effect on such a bucket, which would void the declared ConditionalCreate capability. Discarding by version id, and
/// a fixture that models delete markers, are what this driver needs before it can honestly claim either shape.
/// </summary>
internal sealed partial class AliyunOssArtifactStorageDriver : IArtifactStorageDriver
{
    private const string ObjectArea = "objects/";
    private const string StagingArea = ".codespace/staging/";
    private const string ProbeArea = ".codespace/probe/";

    private static readonly StorageProviderCapabilities SupportedCapabilities = StorageProviderCapabilities.StreamingWrite
        | StorageProviderCapabilities.StreamingRead
        | StorageProviderCapabilities.RangeRead
        | StorageProviderCapabilities.ConditionalCreate
        | StorageProviderCapabilities.Delete
        | StorageProviderCapabilities.HealthProbe
        | StorageProviderCapabilities.StableETag;

    private Client? _client;
    private readonly AliyunOssTarget _target;

    public AliyunOssArtifactStorageDriver(Client client, AliyunOssTarget target)
    {
        _client = client;
        _target = target;
    }

    public StorageProviderCapabilities Capabilities => SupportedCapabilities;

    public async ValueTask<ArtifactStoragePutResult> PutAsync(ArtifactStoragePutRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var invalid = ValidatePut(request);
        if (invalid != null) return ArtifactStoragePutResult.Failed(invalid);
        if (!_target.TryResolveKey(request.ObjectKey, ObjectArea, out var key)) return ArtifactStoragePutResult.Failed(InvalidKey(request.ObjectKey));
        if (!TryResolveContentLength(request, out var length, out var lengthError)) return ArtifactStoragePutResult.Failed(lengthError!);

        var staging = _target.KeyPrefix + StagingArea + Guid.NewGuid().ToString("N");

        try
        {
            var staged = await StageAsync(request, staging, length, cancellationToken).ConfigureAwait(false);
            if (staged.Error != null) return ArtifactStoragePutResult.Failed(staged.Error);

            var mismatch = VerifyStaged(request, staged);
            if (mismatch != null) return ArtifactStoragePutResult.Failed(mismatch);

            return await PublishAsync(request, staging, key, staged, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await DiscardAsync(staging).ConfigureAwait(false);
        }
    }

    public async ValueTask<ArtifactStorageHeadResult> HeadAsync(ArtifactStorageHeadRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (!_target.TryResolveKey(request.ObjectKey, ObjectArea, out var key)) return ArtifactStorageHeadResult.Failed(InvalidKey(request.ObjectKey));

        try
        {
            var result = await Client.HeadObjectAsync(new HeadObjectRequest { Bucket = _target.Bucket, Key = key }, cancellationToken: cancellationToken).ConfigureAwait(false);
            return ArtifactStorageHeadResult.Found(Describe(result, request.ObjectKey, result.ContentLength ?? 0));
        }
        catch (Exception exception) when (AliyunOssErrors.IsCallerCancellation(exception, cancellationToken))
        {
            throw new OperationCanceledException(cancellationToken);
        }
        catch (Exception exception) when (AliyunOssErrors.IsOperational(exception))
        {
            return ArtifactStorageHeadResult.Failed(await AttributeHeadFailureAsync(exception, request.ObjectKey, cancellationToken).ConfigureAwait(false));
        }
    }

    public async ValueTask<ArtifactStorageReadResult> OpenReadAsync(ArtifactStorageReadRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        if (request.Range is { Offset: < 0 } || request.Range is { Length: < 0 })
            return ArtifactStorageReadResult.Failed(Failure(ArtifactStorageErrorCode.InvalidRequest, "Byte ranges require non-negative offset and length."));
        if (request.ExpectedVersion != null)
            return ArtifactStorageReadResult.Failed(Failure(ArtifactStorageErrorCode.Unsupported, "Aliyun OSS v1 does not expose durable object versions."));
        if (!_target.TryResolveKey(request.ObjectKey, ObjectArea, out var key)) return ArtifactStorageReadResult.Failed(InvalidKey(request.ObjectKey));

        // HTTP has no satisfiable zero-length range, so an empty window is answered from metadata instead of the wire.
        if (request.Range is { Length: 0 }) return await OpenEmptyAsync(request, cancellationToken).ConfigureAwait(false);

        return await OpenRangeAsync(request, key, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<ArtifactStorageDeleteResult> DeleteAsync(ArtifactStorageDeleteRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        if (request.ExpectedVersion != null)
            return ArtifactStorageDeleteResult.Failed(Failure(ArtifactStorageErrorCode.Unsupported, "Aliyun OSS v1 does not expose durable object versions."));
        if (!_target.TryResolveKey(request.ObjectKey, ObjectArea, out var key)) return ArtifactStorageDeleteResult.Failed(InvalidKey(request.ObjectKey));

        // OSS deletes idempotently (204 for an absent key), so existence and any ETag guard are established first.
        var head = await HeadAsync(new ArtifactStorageHeadRequest(request.ObjectKey), cancellationToken).ConfigureAwait(false);
        if (!head.IsSuccess) return ArtifactStorageDeleteResult.Failed(head.Error!);
        if (request.ExpectedETag != null && !string.Equals(request.ExpectedETag, head.Metadata!.ETag, StringComparison.Ordinal))
            return ArtifactStorageDeleteResult.Failed(Failure(ArtifactStorageErrorCode.ConditionNotMet, $"ETag condition was not met for object '{request.ObjectKey}'."));

        try
        {
            await Client.DeleteObjectAsync(new DeleteObjectRequest { Bucket = _target.Bucket, Key = key }, cancellationToken: cancellationToken).ConfigureAwait(false);
            return ArtifactStorageDeleteResult.Removed();
        }
        catch (Exception exception) when (AliyunOssErrors.IsCallerCancellation(exception, cancellationToken))
        {
            throw new OperationCanceledException(cancellationToken);
        }
        catch (Exception exception) when (AliyunOssErrors.IsOperational(exception))
        {
            return ArtifactStorageDeleteResult.Failed(AliyunOssErrors.FromException(exception, request.ObjectKey));
        }
    }

    public async ValueTask<ArtifactStorageProbeResult> ProbeAsync(ArtifactStorageProbeRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var stopwatch = Stopwatch.StartNew();

        var readable = await ProbeReadAsync(cancellationToken).ConfigureAwait(false);
        if (readable != null) return new ArtifactStorageProbeResult { Status = ArtifactStorageProbeStatus.Unavailable, Latency = stopwatch.Elapsed, Error = readable };
        if (!request.VerifyWriteAccess) return new ArtifactStorageProbeResult { Status = ArtifactStorageProbeStatus.Available, Latency = stopwatch.Elapsed };

        var writable = await ProbeWriteAsync(cancellationToken).ConfigureAwait(false);
        if (writable == null) return new ArtifactStorageProbeResult { Status = ArtifactStorageProbeStatus.Available, Latency = stopwatch.Elapsed };

        var denied = writable.Code is ArtifactStorageErrorCode.Forbidden or ArtifactStorageErrorCode.Unauthorized;
        return new ArtifactStorageProbeResult
        {
            Status = denied ? ArtifactStorageProbeStatus.ReadOnly : ArtifactStorageProbeStatus.Degraded,
            Latency = stopwatch.Elapsed,
            Error = writable
        };
    }

    public ValueTask DisposeAsync()
    {
        Interlocked.Exchange(ref _client, null)?.Dispose();
        return ValueTask.CompletedTask;
    }

    /// <summary>Names the bucket only. Signing material must never reach a diagnostic, a log, or an exception.</summary>
    public override string ToString() => $"AliyunOssArtifactStorageDriver {{ Bucket = {_target.Bucket}, Region = {_target.Region}, Credential = [REDACTED] }}";

    private static ArtifactStorageError? ValidatePut(ArtifactStoragePutRequest request)
    {
        if (!request.Content.CanRead) return Failure(ArtifactStorageErrorCode.InvalidRequest, "Artifact content stream must be readable.");
        if (request.ContentLength < 0) return Failure(ArtifactStorageErrorCode.InvalidRequest, "Content length cannot be negative.");
        if (request.ExpectedSha256 != null && !IsSha256(request.ExpectedSha256))
            return Failure(ArtifactStorageErrorCode.InvalidRequest, "ExpectedSha256 must be a 64-character hexadecimal digest.");
        if (request.Condition == ArtifactStorageWriteCondition.MatchETag)
            return Failure(ArtifactStorageErrorCode.Unsupported, "Aliyun OSS v1 supports atomic create-only placement but not atomic ETag replacement.");
        if (request.ExpectedETag != null && request.Condition != ArtifactStorageWriteCondition.MatchETag)
            return Failure(ArtifactStorageErrorCode.InvalidRequest, "ExpectedETag requires the MatchETag condition.");

        return ValidateMetadata(request.Metadata);
    }

    private static ArtifactStorageError? ValidateMetadata(IReadOnlyDictionary<string, string> metadata)
    {
        foreach (var entry in metadata)
        {
            if (entry.Key.Length == 0 || !entry.Key.All(character => char.IsAsciiLetterOrDigit(character) || character == '-'))
                return Failure(ArtifactStorageErrorCode.InvalidRequest, "Object metadata names must be ASCII letters, digits, or hyphens.");
            if (entry.Value.Any(character => char.IsControl(character) || !char.IsAscii(character)))
                return Failure(ArtifactStorageErrorCode.InvalidRequest, $"Object metadata value for '{entry.Key}' must be printable ASCII; OSS carries user metadata in HTTP headers.");
        }

        return null;
    }

    private static bool TryResolveContentLength(ArtifactStoragePutRequest request, out long length, out ArtifactStorageError? error)
    {
        var measured = request.Content.CanSeek ? request.Content.Length - request.Content.Position : (long?)null;
        length = request.ContentLength ?? measured ?? -1;
        error = null;

        if (length < 0)
            error = Failure(ArtifactStorageErrorCode.InvalidRequest, "Aliyun OSS requires a declared ContentLength when the artifact stream cannot report its own length.");
        else if (request.ContentLength is { } declared && measured is { } actual && declared != actual)
            error = Failure(ArtifactStorageErrorCode.IntegrityMismatch, $"Content length mismatch for object '{request.ObjectKey}'.");

        return error == null;
    }

    private static ArtifactStorageError? VerifyStaged(ArtifactStoragePutRequest request, StagedObject staged)
    {
        if (request.ContentLength is { } expected && staged.Length != expected)
            return Failure(ArtifactStorageErrorCode.IntegrityMismatch, $"Content length mismatch for object '{request.ObjectKey}'.");
        if (request.ExpectedSha256 != null && !string.Equals(request.ExpectedSha256, staged.Sha256, StringComparison.OrdinalIgnoreCase))
            return Failure(ArtifactStorageErrorCode.IntegrityMismatch, $"SHA-256 mismatch for object '{request.ObjectKey}'.");

        return null;
    }

    private async ValueTask<ArtifactStorageReadResult> OpenEmptyAsync(ArtifactStorageReadRequest request, CancellationToken cancellationToken)
    {
        var head = await HeadAsync(new ArtifactStorageHeadRequest(request.ObjectKey), cancellationToken).ConfigureAwait(false);
        if (!head.IsSuccess) return ArtifactStorageReadResult.Failed(head.Error!);
        if (request.ExpectedETag != null && !string.Equals(request.ExpectedETag, head.Metadata!.ETag, StringComparison.Ordinal))
            return ArtifactStorageReadResult.Failed(Failure(ArtifactStorageErrorCode.ConditionNotMet, $"ETag condition was not met for object '{request.ObjectKey}'."));

        var offset = request.Range?.Offset ?? 0;
        if (offset > head.Metadata!.Length)
            return ArtifactStorageReadResult.Failed(Failure(ArtifactStorageErrorCode.InvalidRequest, $"Byte range starts beyond object '{request.ObjectKey}'."));

        return ArtifactStorageReadResult.Opened(Stream.Null, 0, head.Metadata.Length, head.Metadata);
    }

    /// <summary>
    /// Attributes a failed HEAD, which cannot attribute itself. OSS carries its <c>&lt;Code&gt;</c> token in the
    /// response BODY only, and HTTP forbids a body on a HEAD response, so a failed HEAD reaches the classifier as a
    /// bare status: a 404 is NoSuchKey or NoSuchBucket indistinguishably, and a 403 is a rejected credential or a
    /// policy denial indistinguishably. Aliyun's <c>x-oss-ec</c> header is documented as diagnostic only - "do not
    /// build application logic that depends on specific EC values" - so it is not a discriminator either.
    ///
    /// Left as a bare status, a 404 becomes Missing: a mistyped bucket name would report the OBJECT as absent, which
    /// the plane treats as an ordinary answer, rather than the PROFILE as unusable, which an operator must fix. So the
    /// question is re-asked with a request that can answer it - the same prefix-scoped ListObjects the health probe
    /// issues, whose failures do carry a body - and its token re-runs the HEAD's own status through the classifier.
    ///
    /// COSTS one extra small request per FAILED head, the dedup miss on a fresh upload included. It can only sharpen
    /// the answer: a bucket request that succeeds, faults, or is itself unattributable leaves the HEAD's own verdict
    /// exactly as it was. It therefore does NOT cover a credential that cannot list its own prefix at all, which
    /// answers AccessDenied to the re-ask and so still cannot tell an absent bucket from an absent object.
    /// </summary>
    private async Task<ArtifactStorageError> AttributeHeadFailureAsync(Exception exception, string objectKey, CancellationToken cancellationToken)
    {
        var error = AliyunOssErrors.FromException(exception, objectKey);
        if (error.ProviderCode != null) return error;

        var bucket = await ProbeReadAsync(cancellationToken).ConfigureAwait(false);

        return AliyunOssErrors.Reclassify(error, objectKey, bucket?.ProviderCode);
    }

    /// <summary>
    /// Asks whether the destination answers a read, scoped to the destination itself - the profile's own key prefix -
    /// rather than to the whole bucket.
    ///
    /// <para>A bucket-wide listing looks equivalent and is not, because of how Aliyun RAM expresses a prefix-scoped
    /// grant: <c>oss:ListObjects</c> is authorized against the BUCKET resource with the prefix carried as the
    /// <c>oss:Prefix</c> condition key. A request that names no prefix therefore fails that condition, so the standard
    /// least-privilege policy - the one Aliyun's own documentation recommends for a shared bucket - answered
    /// AccessDenied to this probe and reported a destination whose every read, write and delete would have succeeded
    /// as a dead one. Naming the prefix satisfies the condition, and asks the narrower question that was always the
    /// one worth asking: not whether this credential can enumerate the bucket, but whether it can reach the place
    /// this profile writes to.</para>
    ///
    /// <para>An empty prefix sends the bucket-wide listing it always did, so a profile that owns its whole bucket is
    /// unaffected. A mistyped bucket still answers NoSuchBucket either way: bucket resolution precedes prefix
    /// filtering, which is what keeps <see cref="AttributeHeadFailureAsync"/> able to tell an absent bucket from an
    /// absent object.</para>
    /// </summary>
    private async Task<ArtifactStorageError?> ProbeReadAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Client.ListObjectsV2Async(new ListObjectsV2Request { Bucket = _target.Bucket, Prefix = _target.KeyPrefix, MaxKeys = 1 }, cancellationToken: cancellationToken).ConfigureAwait(false);
            return null;
        }
        catch (Exception exception) when (AliyunOssErrors.IsCallerCancellation(exception, cancellationToken))
        {
            throw new OperationCanceledException(cancellationToken);
        }
        catch (Exception exception) when (AliyunOssErrors.IsOperational(exception))
        {
            return AliyunOssErrors.FromException(exception, _target.Bucket);
        }
    }

    private async Task<ArtifactStorageError?> ProbeWriteAsync(CancellationToken cancellationToken)
    {
        var key = _target.KeyPrefix + ProbeArea + Guid.NewGuid().ToString("N");

        try
        {
            await using var body = new MemoryStream([], writable: false);
            await Client.PutObjectAsync(new PutObjectRequest { Bucket = _target.Bucket, Key = key, Body = body, ContentLength = 0 }, cancellationToken: cancellationToken).ConfigureAwait(false);
            return null;
        }
        catch (Exception exception) when (AliyunOssErrors.IsCallerCancellation(exception, cancellationToken))
        {
            throw new OperationCanceledException(cancellationToken);
        }
        catch (Exception exception) when (AliyunOssErrors.IsOperational(exception))
        {
            return AliyunOssErrors.FromException(exception, key);
        }
        finally
        {
            await DiscardAsync(key).ConfigureAwait(false);
        }
    }

    private static ArtifactStorageError InvalidKey(string objectKey) =>
        Failure(ArtifactStorageErrorCode.InvalidRequest, $"ObjectKey '{objectKey}' must be a relative key without traversal or empty segments and must fit the OSS key length limit.");

    private static ArtifactStorageError Failure(ArtifactStorageErrorCode code, string message, bool isRetryable = false) => new(code, message, isRetryable);

    private static bool IsSha256(string value) => value.Length == 64 && value.All(Uri.IsHexDigit);

    private Client Client => _client ?? throw new ObjectDisposedException(nameof(AliyunOssArtifactStorageDriver));

    private readonly record struct StagedObject(long Length, string Sha256, ArtifactStorageError? Error);
}
