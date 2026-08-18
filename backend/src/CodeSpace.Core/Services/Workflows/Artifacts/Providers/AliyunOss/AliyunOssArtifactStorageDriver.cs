using System.Diagnostics;

namespace CodeSpace.Core.Services.Workflows.Artifacts.Providers.AliyunOss;

/// <summary>
/// Streaming Aliyun OSS driver spoken directly over the signed REST API.
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
        | StorageProviderCapabilities.HealthProbe;

    private readonly HttpClient _http;
    private readonly AliyunOssTarget _target;
    private readonly TimeProvider _clock;
    private AliyunOssSigningIdentity? _identity;

    public AliyunOssArtifactStorageDriver(HttpClient http, AliyunOssTarget target, AliyunOssSigningIdentity identity, TimeProvider clock)
    {
        _http = http;
        _target = target;
        _identity = identity;
        _clock = clock;
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

        using var message = NewRequest(HttpMethod.Head, key);
        var sent = await SendAsync(message, key, request.ObjectKey, cancellationToken).ConfigureAwait(false);
        if (sent.Error != null) return ArtifactStorageHeadResult.Failed(sent.Error);

        using var response = sent.Response!;
        if (!response.IsSuccessStatusCode) return ArtifactStorageHeadResult.Failed(await AliyunOssErrors.FromResponseAsync(response, request.ObjectKey, cancellationToken).ConfigureAwait(false));

        return ArtifactStorageHeadResult.Found(Describe(response, request.ObjectKey, ContentLengthOf(response)));
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

        using var message = NewRequest(HttpMethod.Delete, key);
        var sent = await SendAsync(message, key, request.ObjectKey, cancellationToken).ConfigureAwait(false);
        if (sent.Error != null) return ArtifactStorageDeleteResult.Failed(sent.Error);

        using var response = sent.Response!;
        return response.IsSuccessStatusCode
            ? ArtifactStorageDeleteResult.Removed()
            : ArtifactStorageDeleteResult.Failed(await AliyunOssErrors.FromResponseAsync(response, request.ObjectKey, cancellationToken).ConfigureAwait(false));
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
        var identity = Interlocked.Exchange(ref _identity, null);
        if (identity != null) System.Security.Cryptography.CryptographicOperations.ZeroMemory(identity.SigningKeySeed);
        _http.Dispose();
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

    private async Task<ArtifactStorageError?> ProbeReadAsync(CancellationToken cancellationToken)
    {
        using var message = new HttpRequestMessage(HttpMethod.Get, _target.ObjectUri(string.Empty, "?list-type=2&max-keys=0"));
        var sent = await SendAsync(message, string.Empty, _target.Bucket, cancellationToken).ConfigureAwait(false);
        if (sent.Error != null) return sent.Error;

        using var response = sent.Response!;
        return response.IsSuccessStatusCode ? null : await AliyunOssErrors.FromResponseAsync(response, _target.Bucket, cancellationToken).ConfigureAwait(false);
    }

    private async Task<ArtifactStorageError?> ProbeWriteAsync(CancellationToken cancellationToken)
    {
        var key = _target.KeyPrefix + ProbeArea + Guid.NewGuid().ToString("N");

        try
        {
            using var message = NewRequest(HttpMethod.Put, key);
            message.Content = new ByteArrayContent([]);
            message.Content.Headers.ContentLength = 0;

            var sent = await SendAsync(message, key, key, cancellationToken).ConfigureAwait(false);
            if (sent.Error != null) return sent.Error;

            using var response = sent.Response!;
            return response.IsSuccessStatusCode ? null : await AliyunOssErrors.FromResponseAsync(response, key, cancellationToken).ConfigureAwait(false);
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

    private static long ContentLengthOf(HttpResponseMessage response) => response.Content.Headers.ContentLength ?? 0;

    private readonly record struct StagedObject(long Length, string Sha256, ArtifactStorageError? Error);

    private readonly record struct SendOutcome(HttpResponseMessage? Response, ArtifactStorageError? Error);
}
