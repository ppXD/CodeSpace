using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Xml.Linq;

namespace CodeSpace.Core.Services.Workflows.Artifacts.Providers.AliyunOss;

/// <summary>
/// The wire half of the driver: request construction, V4 signing, OSS header projection, and the two pass-through
/// streams that keep both directions streaming rather than buffered.
/// </summary>
internal sealed partial class AliyunOssArtifactStorageDriver
{
    private const string MetadataHeaderPrefix = "x-oss-meta-";
    private const string CopySourceHeader = "x-oss-copy-source";
    private const string ForbidOverwriteHeader = "x-oss-forbid-overwrite";
    private const int CopyResultLimitBytes = 8 * 1024;

    private async Task<StagedObject> StageAsync(ArtifactStoragePutRequest request, string stagingKey, long length, CancellationToken cancellationToken)
    {
        using var message = NewRequest(HttpMethod.Put, stagingKey);
        await using var hashing = new HashingReadStream(request.Content);
        message.Content = new StreamContent(hashing);
        message.Content.Headers.ContentLength = length;
        if (!string.IsNullOrWhiteSpace(request.ContentType)) message.Content.Headers.TryAddWithoutValidation("Content-Type", request.ContentType);
        foreach (var entry in request.Metadata) message.Headers.TryAddWithoutValidation(MetadataHeaderPrefix + entry.Key, entry.Value);

        var sent = await SendAsync(message, stagingKey, request.ObjectKey, cancellationToken).ConfigureAwait(false);
        if (sent.Error != null) return new StagedObject(0, string.Empty, sent.Error);

        using var response = sent.Response!;
        if (!response.IsSuccessStatusCode) return new StagedObject(0, string.Empty, await AliyunOssErrors.FromResponseAsync(response, request.ObjectKey, cancellationToken).ConfigureAwait(false));

        return new StagedObject(hashing.BytesRead, hashing.Digest(), null);
    }

    private async Task<ArtifactStoragePutResult> PublishAsync(ArtifactStoragePutRequest request, string stagingKey, string ossKey, StagedObject staged, CancellationToken cancellationToken)
    {
        using var message = NewRequest(HttpMethod.Put, ossKey);
        message.Headers.TryAddWithoutValidation(CopySourceHeader, AliyunOssV4Signer.Encode(_target.ResourcePath(stagingKey), escapeSlash: false));
        if (request.Condition == ArtifactStorageWriteCondition.CreateOnly) message.Headers.TryAddWithoutValidation(ForbidOverwriteHeader, "true");

        var sent = await SendAsync(message, ossKey, request.ObjectKey, cancellationToken).ConfigureAwait(false);
        if (sent.Error != null) return ArtifactStoragePutResult.Failed(sent.Error);

        using var response = sent.Response!;
        if (!response.IsSuccessStatusCode) return ArtifactStoragePutResult.Failed(await AliyunOssErrors.FromResponseAsync(response, request.ObjectKey, cancellationToken).ConfigureAwait(false));

        var copied = await ReadCopyResultAsync(response, request.ObjectKey, cancellationToken).ConfigureAwait(false);
        return copied.ETag == null
            ? ArtifactStoragePutResult.Failed(copied.Error!)
            : ArtifactStoragePutResult.Stored(Published(request, staged, copied));
    }

    private async ValueTask<ArtifactStorageReadResult> OpenRangeAsync(ArtifactStorageReadRequest request, string ossKey, CancellationToken cancellationToken)
    {
        using var message = NewRequest(HttpMethod.Get, ossKey);
        if (request.ExpectedETag != null) message.Headers.TryAddWithoutValidation("If-Match", request.ExpectedETag);
        if (request.Range is { } range) message.Headers.TryAddWithoutValidation("Range", RangeHeader(range));

        var sent = await SendAsync(message, ossKey, request.ObjectKey, cancellationToken).ConfigureAwait(false);
        if (sent.Error != null) return ArtifactStorageReadResult.Failed(sent.Error);

        var response = sent.Response!;
        if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            response.Dispose();
            return await OpenEmptyAsync(request, cancellationToken).ConfigureAwait(false);
        }

        if (!response.IsSuccessStatusCode)
        {
            var error = await AliyunOssErrors.FromResponseAsync(response, request.ObjectKey, cancellationToken).ConfigureAwait(false);
            response.Dispose();
            return ArtifactStorageReadResult.Failed(error);
        }

        return await OpenedAsync(response, request.ObjectKey, cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<ArtifactStorageReadResult> OpenedAsync(HttpResponseMessage response, string objectKey, CancellationToken cancellationToken)
    {
        var total = TotalLengthOf(response);
        var content = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);

        return ArtifactStorageReadResult.Opened(new HttpResponseStream(content, response), ContentLengthOf(response), total, Describe(response, objectKey, total));
    }

    /// <summary>Best-effort cleanup of a staging or probe upload. It must never mask the caller's own outcome.</summary>
    private async ValueTask DiscardAsync(string ossKey)
    {
        try
        {
            using var message = NewRequest(HttpMethod.Delete, ossKey);
            var sent = await SendAsync(message, ossKey, ossKey, CancellationToken.None).ConfigureAwait(false);
            sent.Response?.Dispose();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
        }
    }

    private HttpRequestMessage NewRequest(HttpMethod method, string ossKey) => new(method, _target.ObjectUri(ossKey));

    private async Task<SendOutcome> SendAsync(HttpRequestMessage message, string ossKey, string objectKey, CancellationToken cancellationToken)
    {
        Sign(message, ossKey);

        try
        {
            return new SendOutcome(await _http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false), null);
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException)
        {
            return new SendOutcome(null, AliyunOssErrors.Transport(exception, objectKey));
        }
    }

    private void Sign(HttpRequestMessage message, string ossKey)
    {
        var identity = _identity ?? throw new ObjectDisposedException(nameof(AliyunOssArtifactStorageDriver));
        var timestamp = _clock.GetUtcNow();
        message.Headers.TryAddWithoutValidation(AliyunOssV4Signer.DateHeader, AliyunOssV4Signer.Timestamp(timestamp));
        message.Headers.TryAddWithoutValidation(AliyunOssV4Signer.ContentSha256Header, AliyunOssV4Signer.UnsignedPayload);
        if (identity.SecurityToken != null) message.Headers.TryAddWithoutValidation(AliyunOssV4Signer.SecurityTokenHeader, identity.SecurityToken);

        var signing = new AliyunOssSigningRequest
        {
            Method = message.Method.Method,
            ResourcePath = _target.ResourcePath(ossKey),
            Query = QueryOf(message.RequestUri!),
            Headers = HeadersOf(message),
            Timestamp = timestamp
        };
        message.Headers.TryAddWithoutValidation("Authorization", AliyunOssV4Signer.Authorization(signing, identity));
    }

    private static Dictionary<string, string> HeadersOf(HttpRequestMessage message)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var header in message.Headers) headers[header.Key] = string.Join(',', header.Value);
        if (message.Content != null)
        {
            foreach (var header in message.Content.Headers) headers[header.Key] = string.Join(',', header.Value);
        }

        return headers;
    }

    private static Dictionary<string, string> QueryOf(Uri uri)
    {
        var query = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = pair.IndexOf('=');
            var name = separator < 0 ? pair : pair[..separator];
            query[Uri.UnescapeDataString(name)] = separator < 0 ? string.Empty : Uri.UnescapeDataString(pair[(separator + 1)..]);
        }

        return query;
    }

    private static string RangeHeader(ArtifactStorageByteRange range) => range.Length is { } length
        ? $"bytes={range.Offset}-{range.Offset + length - 1}"
        : $"bytes={range.Offset}-";

    /// <summary>
    /// Projects an OSS response into driver metadata. <c>Version</c> is null in both projections even when the bucket
    /// has versioning enabled and returns <c>x-oss-version-id</c>: the module does not declare <c>ObjectVersioning</c>,
    /// and both <c>OpenReadAsync</c> and <c>DeleteAsync</c> refuse an <c>ExpectedVersion</c>. Callers round-trip this
    /// field straight back as a read condition, so reporting a version the driver rejects on the way back would make
    /// every write unverifiable and every subsequent read permanently unavailable.
    /// </summary>
    private static ArtifactStorageObjectMetadata Describe(HttpResponseMessage response, string objectKey, long length) => new()
    {
        ObjectKey = objectKey,
        Length = length,
        Sha256 = null,
        ETag = Header(response, "ETag"),
        Version = null,
        ContentType = response.Content.Headers.ContentType?.ToString(),
        LastModifiedAt = response.Content.Headers.LastModified ?? ParseDate(Header(response, "Last-Modified")),
        Metadata = UserMetadata(response)
    };

    private static ArtifactStorageObjectMetadata Published(ArtifactStoragePutRequest request, StagedObject staged, CopyResult copied) => new()
    {
        ObjectKey = request.ObjectKey,
        Length = staged.Length,
        Sha256 = staged.Sha256,
        ETag = copied.ETag,
        Version = null,
        ContentType = request.ContentType,
        LastModifiedAt = copied.LastModifiedAt,
        Metadata = new Dictionary<string, string>(request.Metadata, StringComparer.Ordinal)
    };

    /// <summary>OSS can report a copy failure inside a 200 body, so the XML root decides success, not the status line.</summary>
    private static async ValueTask<CopyResult> ReadCopyResultAsync(HttpResponseMessage response, string objectKey, CancellationToken cancellationToken)
    {
        try
        {
            await using var body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var buffer = new byte[CopyResultLimitBytes];
            var read = await body.ReadAtLeastAsync(buffer, CopyResultLimitBytes, throwOnEndOfStream: false, cancellationToken).ConfigureAwait(false);
            var root = XDocument.Parse(System.Text.Encoding.UTF8.GetString(buffer, 0, read)).Root;
            var tag = root?.Name.LocalName == "CopyObjectResult" ? root.Element("ETag")?.Value : null;

            return string.IsNullOrWhiteSpace(tag)
                ? new CopyResult(null, null, Failure(ArtifactStorageErrorCode.ProviderFailure, $"Aliyun OSS did not confirm the publish of object '{objectKey}'.", isRetryable: true))
                : new CopyResult(tag, ParseDate(root!.Element("LastModified")?.Value), null);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new CopyResult(null, null, Failure(ArtifactStorageErrorCode.ProviderFailure, $"Aliyun OSS returned an unreadable publish confirmation for object '{objectKey}'.", isRetryable: true));
        }
    }

    private static long TotalLengthOf(HttpResponseMessage response)
    {
        var range = response.Content.Headers.ContentRange;
        return range is { HasLength: true, Length: { } total } ? total : ContentLengthOf(response);
    }

    private static Dictionary<string, string> UserMetadata(HttpResponseMessage response) => response.Headers
        .Where(header => header.Key.StartsWith(MetadataHeaderPrefix, StringComparison.OrdinalIgnoreCase))
        .ToDictionary(header => header.Key[MetadataHeaderPrefix.Length..], header => string.Join(',', header.Value), StringComparer.Ordinal);

    private static string? Header(HttpResponseMessage response, string name) => response.Headers.TryGetValues(name, out var values) ? values.FirstOrDefault() : null;

    private static DateTimeOffset? ParseDate(string? value) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsed) ? parsed : null;

    private readonly record struct CopyResult(string? ETag, DateTimeOffset? LastModifiedAt, ArtifactStorageError? Error);

    /// <summary>Counts and digests bytes as they flow to the socket, so an upload is never buffered to measure it.</summary>
    private sealed class HashingReadStream : Stream
    {
        private readonly Stream _inner;
        private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        private string? _digest;

        public HashingReadStream(Stream inner) => _inner = inner;

        public long BytesRead { get; private set; }

        /// <summary>HttpClient disposes the request content once the send completes, so the digest is latched first.</summary>
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

        /// <summary>Disposes only the hash: the artifact content stream belongs to the caller.</summary>
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

    /// <summary>Hands the caller the live response body and ties the response's lifetime to that stream.</summary>
    private sealed class HttpResponseStream : Stream
    {
        private readonly Stream _inner;
        private readonly HttpResponseMessage _response;

        public HttpResponseStream(Stream inner, HttpResponseMessage response)
        {
            _inner = inner;
            _response = response;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => _inner.ReadAsync(buffer, cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
                _response.Dispose();
            }

            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            await _inner.DisposeAsync().ConfigureAwait(false);
            _response.Dispose();
            GC.SuppressFinalize(this);
        }
    }
}
