using System.Globalization;
using System.Security.Cryptography;
using AlibabaCloud.OSS.V2.Models;

namespace CodeSpace.Core.Services.Workflows.Artifacts.Providers.AliyunOss;

/// <summary>
/// The official-SDK half of the driver. Alibaba Cloud owns endpoint resolution, V4 canonicalization, signing,
/// clock-skew correction, retry classification, response parsing, and request-id handling. CodeSpace continues to own
/// its provider-neutral CAS contract and streams bytes without buffering whole artifacts.
/// </summary>
internal sealed partial class AliyunOssArtifactStorageDriver
{
    private async Task<StagedObject> StageAsync(ArtifactStoragePutRequest request, string stagingKey, long length, CancellationToken cancellationToken)
    {
        await using var hashing = new HashingReadStream(request.Content);

        try
        {
            await Client.PutObjectAsync(new PutObjectRequest
            {
                Bucket = _target.Bucket,
                Key = stagingKey,
                Body = hashing,
                ContentLength = length,
                ContentType = string.IsNullOrWhiteSpace(request.ContentType) ? null : request.ContentType,
                Metadata = new Dictionary<string, string>(request.Metadata, StringComparer.Ordinal)
            }, cancellationToken: cancellationToken).ConfigureAwait(false);

            return new StagedObject(hashing.BytesRead, hashing.Digest(), null);
        }
        catch (Exception exception) when (AliyunOssErrors.IsCallerCancellation(exception, cancellationToken))
        {
            throw new OperationCanceledException(cancellationToken);
        }
        catch (Exception exception) when (AliyunOssErrors.IsOperational(exception))
        {
            return new StagedObject(0, string.Empty, AliyunOssErrors.FromException(exception, request.ObjectKey));
        }
    }

    private async Task<ArtifactStoragePutResult> PublishAsync(ArtifactStoragePutRequest request, string stagingKey, string ossKey, StagedObject staged, CancellationToken cancellationToken)
    {
        try
        {
            var copied = await Client.CopyObjectAsync(new CopyObjectRequest
            {
                Bucket = _target.Bucket,
                Key = ossKey,
                SourceBucket = _target.Bucket,
                SourceKey = stagingKey,
                ForbidOverwrite = request.Condition == ArtifactStorageWriteCondition.CreateOnly
            }, cancellationToken: cancellationToken).ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(copied.ETag))
                return ArtifactStoragePutResult.Failed(Failure(ArtifactStorageErrorCode.ProviderFailure, $"Aliyun OSS did not confirm the publish of object '{request.ObjectKey}'.", isRetryable: true));

            return ArtifactStoragePutResult.Stored(Published(request, staged, copied.ETag, copied.LastModified));
        }
        catch (Exception exception) when (AliyunOssErrors.IsCallerCancellation(exception, cancellationToken))
        {
            throw new OperationCanceledException(cancellationToken);
        }
        catch (Exception exception) when (AliyunOssErrors.IsOperational(exception))
        {
            return ArtifactStoragePutResult.Failed(AliyunOssErrors.FromException(exception, request.ObjectKey));
        }
    }

    private async ValueTask<ArtifactStorageReadResult> OpenRangeAsync(ArtifactStorageReadRequest request, string ossKey, CancellationToken cancellationToken)
    {
        try
        {
            var result = await Client.GetObjectAsync(new GetObjectRequest
            {
                Bucket = _target.Bucket,
                Key = ossKey,
                IfMatch = request.ExpectedETag,
                Range = request.Range is { } range ? RangeHeader(range) : null,
                RangeBehavior = request.Range == null ? null : "standard"
            }, cancellationToken: cancellationToken).ConfigureAwait(false);

            if (result.Body == null)
                return ArtifactStorageReadResult.Failed(Failure(ArtifactStorageErrorCode.ProviderFailure, $"Aliyun OSS returned no body for object '{request.ObjectKey}'.", isRetryable: true));

            var length = result.ContentLength ?? 0;
            var total = TotalLengthOf(result.ContentRange, length);
            return ArtifactStorageReadResult.Opened(result.Body, length, total, Describe(result, request.ObjectKey, total));
        }
        catch (Exception exception) when (AliyunOssErrors.IsCallerCancellation(exception, cancellationToken))
        {
            throw new OperationCanceledException(cancellationToken);
        }
        catch (Exception exception) when (AliyunOssErrors.IsOperational(exception))
        {
            var error = AliyunOssErrors.FromException(exception, request.ObjectKey);
            return error.ProviderCode == "InvalidRange"
                ? await OpenEmptyAsync(request, cancellationToken).ConfigureAwait(false)
                : ArtifactStorageReadResult.Failed(error);
        }
    }

    /// <summary>Best-effort cleanup of a staging or probe upload. It must never mask the caller's own outcome.</summary>
    private async ValueTask DiscardAsync(string ossKey)
    {
        try
        {
            await Client.DeleteObjectAsync(new DeleteObjectRequest { Bucket = _target.Bucket, Key = ossKey }, cancellationToken: CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
        }
    }

    private static string RangeHeader(ArtifactStorageByteRange range) => range.Length is { } length
        ? $"bytes={range.Offset}-{range.Offset + length - 1}"
        : $"bytes={range.Offset}-";

    /// <summary>
    /// Version remains null even if OSS reports one: this versioning-disabled provider does not declare
    /// ObjectVersioning and must not hand callers a condition it rejects on the return trip.
    /// </summary>
    private static ArtifactStorageObjectMetadata Describe(HeadObjectResult result, string objectKey, long length) => new()
    {
        ObjectKey = objectKey,
        Length = length,
        Sha256 = null,
        ETag = result.ETag,
        Version = null,
        ContentType = result.ContentType,
        LastModifiedAt = ParseDate(result.LastModified),
        Metadata = CopyMetadata(result.Metadata)
    };

    private static ArtifactStorageObjectMetadata Describe(GetObjectResult result, string objectKey, long length) => new()
    {
        ObjectKey = objectKey,
        Length = length,
        Sha256 = null,
        ETag = result.ETag,
        Version = null,
        ContentType = result.ContentType,
        LastModifiedAt = ParseDate(result.LastModified),
        Metadata = CopyMetadata(result.Metadata)
    };

    private static ArtifactStorageObjectMetadata Published(ArtifactStoragePutRequest request, StagedObject staged, string etag, DateTime? lastModified) => new()
    {
        ObjectKey = request.ObjectKey,
        Length = staged.Length,
        Sha256 = staged.Sha256,
        ETag = etag,
        Version = null,
        ContentType = request.ContentType,
        LastModifiedAt = lastModified == null ? null : new DateTimeOffset(DateTime.SpecifyKind(lastModified.Value, DateTimeKind.Utc)),
        Metadata = new Dictionary<string, string>(request.Metadata, StringComparer.Ordinal)
    };

    private static long TotalLengthOf(string? contentRange, long fallback)
    {
        if (string.IsNullOrWhiteSpace(contentRange)) return fallback;
        var separator = contentRange.LastIndexOf('/');
        return separator >= 0 && long.TryParse(contentRange[(separator + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var total) ? total : fallback;
    }

    private static Dictionary<string, string> CopyMetadata(IDictionary<string, string>? metadata) => metadata == null
        ? new Dictionary<string, string>(StringComparer.Ordinal)
        : new Dictionary<string, string>(metadata, StringComparer.Ordinal);

    private static DateTimeOffset? ParseDate(string? value) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsed) ? parsed : null;

    /// <summary>Counts and digests bytes as they flow to the SDK transport without owning the caller's stream.</summary>
    private sealed class HashingReadStream : Stream
    {
        private readonly Stream _inner;
        private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        private string? _digest;

        public HashingReadStream(Stream inner) => _inner = inner;

        public long BytesRead { get; private set; }
        public string Digest() => _digest ??= Convert.ToHexStringLower(_hash.GetCurrentHash());
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = _inner.Read(buffer, offset, count);
            if (read > 0) Append(buffer.AsSpan(offset, read));
            return read;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var read = await _inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read > 0) Append(buffer.Span[..read]);
            return read;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        private void Append(ReadOnlySpan<byte> chunk)
        {
            _hash.AppendData(chunk);
            BytesRead += chunk.Length;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                Digest();
                _hash.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
