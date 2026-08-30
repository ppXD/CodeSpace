using System.Buffers;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

namespace CodeSpace.Core.Services.Workflows.Artifacts.Providers.Local;

/// <summary>
/// Factory for the profile-driven local RWX contract. A team that routes a data class at a profile of this provider
/// reaches this driver through the CAS runtime — including <see cref="ArtifactStore"/>, whose offloaded writes resolve
/// the <c>workflow-artifact/v1</c> route before they place any bytes.
/// The configured root is trusted operator infrastructure and must not be writable by untrusted tenants (including
/// the ability to introduce symlinks); object keys are still lexically contained beneath that root.
/// </summary>
public sealed class LocalRwxArtifactStorageDriverFactory : IArtifactStorageDriverFactory
{
    public const string TypeKey = "local-rwx/v1";

    public string ProviderTypeKey => TypeKey;

    public ValueTask<IArtifactStorageDriver> CreateAsync(ArtifactStorageDriverCreateRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var profile = request.Profile ?? throw new ArgumentException("A storage profile snapshot is required.", nameof(request));
        if (profile.SchemaVersion != StorageProfileSnapshot.CurrentSchemaVersion)
            throw new NotSupportedException($"Storage profile schema version '{profile.SchemaVersion}' is not supported by {TypeKey}.");
        if (profile.ProfileId == Guid.Empty || profile.ProfileRevision <= 0)
            throw new ArgumentException("A persisted storage profile identity and positive revision are required.", nameof(request));
        if (!string.Equals(profile.ProviderTypeKey, TypeKey, StringComparison.Ordinal))
            throw new ArgumentException($"Storage profile provider '{profile.ProviderTypeKey}' cannot be opened by factory '{TypeKey}'.", nameof(request));
        if (profile.Configuration.ValueKind != JsonValueKind.Object || !profile.Configuration.TryGetProperty("rootPath", out var rootElement) || rootElement.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(rootElement.GetString()))
            throw new ArgumentException("Local RWX profile configuration requires a non-empty rootPath.", nameof(request));

        return ValueTask.FromResult<IArtifactStorageDriver>(new LocalRwxArtifactStorageDriver(rootElement.GetString()!));
    }
}

internal sealed class LocalRwxArtifactStorageDriver : IArtifactStorageDriver
{
    private const int BufferSize = 128 * 1024;
    private static readonly StorageProviderCapabilities SupportedCapabilities = StorageProviderCapabilities.StreamingWrite
        | StorageProviderCapabilities.StreamingRead
        | StorageProviderCapabilities.RangeRead
        | StorageProviderCapabilities.ConditionalCreate
        | StorageProviderCapabilities.Delete
        | StorageProviderCapabilities.HealthProbe;
    private readonly string _root;

    public LocalRwxArtifactStorageDriver(string root)
    {
        _root = Path.GetFullPath(root);
    }

    public StorageProviderCapabilities Capabilities => SupportedCapabilities;

    public async ValueTask<ArtifactStoragePutResult> PutAsync(ArtifactStoragePutRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var invalid = ValidatePut(request);
        if (invalid != null) return ArtifactStoragePutResult.Failed(invalid);
        if (!TryResolveObjectPath(request.ObjectKey, out var path, out var pathError)) return ArtifactStoragePutResult.Failed(pathError!);
        if (request.Condition == ArtifactStorageWriteCondition.CreateOnly && File.Exists(path))
            return ArtifactStoragePutResult.Failed(Error(ArtifactStorageErrorCode.AlreadyExists, $"Object '{request.ObjectKey}' already exists."));

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporaryPath = path + ".upload-" + Guid.NewGuid().ToString("N");

        try
        {
            var copied = await CopyAndHashAsync(request.Content, temporaryPath, cancellationToken).ConfigureAwait(false);
            if (request.ContentLength is { } expectedLength && copied.Length != expectedLength)
                return FailAndDelete(temporaryPath, ArtifactStorageErrorCode.IntegrityMismatch, $"Content length mismatch for object '{request.ObjectKey}'.");
            if (request.ExpectedSha256 != null && !string.Equals(request.ExpectedSha256, copied.Sha256, StringComparison.OrdinalIgnoreCase))
                return FailAndDelete(temporaryPath, ArtifactStorageErrorCode.IntegrityMismatch, $"SHA-256 mismatch for object '{request.ObjectKey}'.");

            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (request.Condition == ArtifactStorageWriteCondition.CreateOnly)
                {
                    var placementError = await PlaceCreateOnlyAsync(request.ObjectKey, temporaryPath, path, cancellationToken).ConfigureAwait(false);
                    if (placementError != null)
                    {
                        TryDelete(temporaryPath);
                        return ArtifactStoragePutResult.Failed(placementError);
                    }
                }
                else
                {
                    File.Move(temporaryPath, path, overwrite: true);
                }
            }
            catch (IOException) when (request.Condition == ArtifactStorageWriteCondition.CreateOnly && File.Exists(path))
            {
                TryDelete(temporaryPath);
                return ArtifactStoragePutResult.Failed(Error(ArtifactStorageErrorCode.AlreadyExists, $"Object '{request.ObjectKey}' already exists."));
            }

            return ArtifactStoragePutResult.Stored(Metadata(request.ObjectKey, path, copied.Length, copied.Sha256, request.ContentType, request.Metadata));
        }
        catch (OperationCanceledException)
        {
            TryDelete(temporaryPath);
            throw;
        }
        catch (UnauthorizedAccessException ex)
        {
            TryDelete(temporaryPath);
            return ArtifactStoragePutResult.Failed(Error(ArtifactStorageErrorCode.Forbidden, ex.Message));
        }
        catch (IOException ex)
        {
            TryDelete(temporaryPath);
            return ArtifactStoragePutResult.Failed(Error(ArtifactStorageErrorCode.ProviderFailure, ex.Message, isRetryable: true));
        }
        catch
        {
            TryDelete(temporaryPath);
            throw;
        }
    }

    public ValueTask<ArtifactStorageHeadResult> HeadAsync(ArtifactStorageHeadRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (!TryResolveObjectPath(request.ObjectKey, out var path, out var pathError)) return ValueTask.FromResult(ArtifactStorageHeadResult.Failed(pathError!));
        if (!File.Exists(path)) return ValueTask.FromResult(ArtifactStorageHeadResult.Failed(Missing(request.ObjectKey)));

        try
        {
            return ValueTask.FromResult(ArtifactStorageHeadResult.Found(Metadata(request.ObjectKey, new FileInfo(path))));
        }
        catch (FileNotFoundException)
        {
            return ValueTask.FromResult(ArtifactStorageHeadResult.Failed(Missing(request.ObjectKey)));
        }
        catch (IOException ex)
        {
            return ValueTask.FromResult(ArtifactStorageHeadResult.Failed(Error(ArtifactStorageErrorCode.ProviderFailure, ex.Message, isRetryable: true)));
        }
    }

    public async ValueTask<ArtifactStorageReadResult> OpenReadAsync(ArtifactStorageReadRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (request.Range is { Offset: < 0 } || request.Range is { Length: < 0 })
            return ArtifactStorageReadResult.Failed(Error(ArtifactStorageErrorCode.InvalidRequest, "Byte ranges require non-negative offset and length."));
        if (!TryResolveObjectPath(request.ObjectKey, out var path, out var pathError)) return ArtifactStorageReadResult.Failed(pathError!);

        var head = await HeadAsync(new ArtifactStorageHeadRequest(request.ObjectKey), cancellationToken).ConfigureAwait(false);
        if (!head.IsSuccess) return ArtifactStorageReadResult.Failed(head.Error!);
        var metadata = head.Metadata!;
        if (request.ExpectedETag != null && !string.Equals(request.ExpectedETag, metadata.ETag, StringComparison.Ordinal))
            return ArtifactStorageReadResult.Failed(Error(ArtifactStorageErrorCode.ConditionNotMet, $"ETag condition was not met for object '{request.ObjectKey}'."));
        if (request.ExpectedVersion != null)
            return ArtifactStorageReadResult.Failed(Error(ArtifactStorageErrorCode.Unsupported, "Local RWX v1 does not provide durable object versions."));

        var offset = request.Range?.Offset ?? 0;
        if (offset > metadata.Length)
            return ArtifactStorageReadResult.Failed(Error(ArtifactStorageErrorCode.InvalidRequest, $"Byte range starts beyond object '{request.ObjectKey}'."));
        var contentLength = Math.Min(request.Range?.Length ?? metadata.Length - offset, metadata.Length - offset);

        try
        {
            var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
            stream.Seek(offset, SeekOrigin.Begin);
            Stream content = contentLength == metadata.Length ? stream : new BoundedReadStream(stream, contentLength);
            return ArtifactStorageReadResult.Opened(content, contentLength, metadata.Length, metadata);
        }
        catch (FileNotFoundException)
        {
            return ArtifactStorageReadResult.Failed(Missing(request.ObjectKey));
        }
        catch (IOException ex)
        {
            return ArtifactStorageReadResult.Failed(Error(ArtifactStorageErrorCode.ProviderFailure, ex.Message, isRetryable: true));
        }
    }

    public async ValueTask<ArtifactStorageDeleteResult> DeleteAsync(ArtifactStorageDeleteRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (!TryResolveObjectPath(request.ObjectKey, out var path, out var pathError)) return ArtifactStorageDeleteResult.Failed(pathError!);

        if (request.ExpectedVersion != null)
            return ArtifactStorageDeleteResult.Failed(Error(ArtifactStorageErrorCode.Unsupported, "Local RWX v1 does not provide durable object versions."));

        if (request.ExpectedETag != null)
        {
            var head = await HeadAsync(new ArtifactStorageHeadRequest(request.ObjectKey), cancellationToken).ConfigureAwait(false);
            if (!head.IsSuccess) return ArtifactStorageDeleteResult.Failed(head.Error!);
            if (request.ExpectedETag != null && !string.Equals(request.ExpectedETag, head.Metadata!.ETag, StringComparison.Ordinal))
                return ArtifactStorageDeleteResult.Failed(Error(ArtifactStorageErrorCode.ConditionNotMet, $"ETag condition was not met for object '{request.ObjectKey}'."));
        }

        try
        {
            if (!File.Exists(path)) return ArtifactStorageDeleteResult.Failed(Missing(request.ObjectKey));
            File.Delete(path);
            return ArtifactStorageDeleteResult.Removed();
        }
        catch (FileNotFoundException)
        {
            return ArtifactStorageDeleteResult.Failed(Missing(request.ObjectKey));
        }
        catch (IOException ex)
        {
            return ArtifactStorageDeleteResult.Failed(Error(ArtifactStorageErrorCode.ProviderFailure, ex.Message, isRetryable: true));
        }
    }

    public async ValueTask<ArtifactStorageProbeResult> ProbeAsync(ArtifactStorageProbeRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var stopwatch = Stopwatch.StartNew();

        try
        {
            // A probe must never MAKE the destination healthy — neither arm. Creating the root answered "yes,
            // reachable" for a volume that had vanished, which is the one answer that must never be invented: the
            // verifier asks this exact question to decide whether an absent object is evidence the object is gone,
            // and a recreated empty root turns "the mount is missing" into "every object under it was deleted".
            //
            // Exempting the write arm did not make it safe, it made it the SOURCE: the health sweep probes
            // write-verified every fifteen minutes, so it recreated the vanished root, went green on the card, and
            // then handed the hourly verifier the corroboration it needed to demote every placement underneath.
            // A destination the operator has not provisioned is unavailable, and saying so is the whole job —
            // provisioning it is a different request, and only a caller that IS provisioning may make it.
            if (request.Initialize) Directory.CreateDirectory(_root);

            if (!Directory.Exists(_root))
                return new ArtifactStorageProbeResult
                {
                    Status = ArtifactStorageProbeStatus.Unavailable, Latency = stopwatch.Elapsed,
                    Error = Error(ArtifactStorageErrorCode.Unavailable, $"Local storage root '{_root}' does not exist.", isRetryable: true),
                };

            if (request.VerifyWriteAccess)
            {
                var probePath = Path.Combine(_root, ".codespace-probe-" + Guid.NewGuid().ToString("N"));
                try
                {
                    await using var probe = new FileStream(probePath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.Asynchronous);
                    await probe.FlushAsync(cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    TryDelete(probePath);
                }
            }

            return new ArtifactStorageProbeResult { Status = ArtifactStorageProbeStatus.Available, Latency = stopwatch.Elapsed };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (UnauthorizedAccessException ex)
        {
            return new ArtifactStorageProbeResult { Status = ArtifactStorageProbeStatus.ReadOnly, Latency = stopwatch.Elapsed, Error = Error(ArtifactStorageErrorCode.Forbidden, ex.Message) };
        }
        catch (IOException ex)
        {
            return new ArtifactStorageProbeResult { Status = ArtifactStorageProbeStatus.Unavailable, Latency = stopwatch.Elapsed, Error = Error(ArtifactStorageErrorCode.Unavailable, ex.Message, isRetryable: true) };
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static ArtifactStorageError? ValidatePut(ArtifactStoragePutRequest request)
    {
        if (!request.Content.CanRead) return Error(ArtifactStorageErrorCode.InvalidRequest, "Artifact content stream must be readable.");
        if (request.ContentLength < 0) return Error(ArtifactStorageErrorCode.InvalidRequest, "Content length cannot be negative.");
        if (request.ExpectedSha256 != null && !IsSha256(request.ExpectedSha256)) return Error(ArtifactStorageErrorCode.InvalidRequest, "ExpectedSha256 must be a 64-character hexadecimal digest.");
        if (request.Condition == ArtifactStorageWriteCondition.MatchETag) return Error(ArtifactStorageErrorCode.Unsupported, "Local RWX v1 supports atomic create-only placement but not atomic ETag replacement.");
        if (request.ExpectedETag != null && request.Condition != ArtifactStorageWriteCondition.MatchETag) return Error(ArtifactStorageErrorCode.InvalidRequest, "ExpectedETag requires the MatchETag condition.");
        return null;
    }

    private bool TryResolveObjectPath(string objectKey, out string path, out ArtifactStorageError? error)
    {
        path = string.Empty;
        error = null;
        if (string.IsNullOrWhiteSpace(objectKey) || objectKey.IndexOf('\0') >= 0 || Path.IsPathRooted(objectKey))
        {
            error = Error(ArtifactStorageErrorCode.InvalidRequest, "ObjectKey must be a non-empty relative key.");
            return false;
        }

        var segments = objectKey.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0 || segments.Any(segment => segment is "." or ".."))
        {
            error = Error(ArtifactStorageErrorCode.InvalidRequest, "ObjectKey cannot contain traversal segments.");
            return false;
        }

        path = Path.GetFullPath(Path.Combine([_root, "objects", .. segments]));
        var objectRoot = Path.GetFullPath(Path.Combine(_root, "objects")) + Path.DirectorySeparatorChar;
        if (!path.StartsWith(objectRoot, StringComparison.Ordinal))
        {
            error = Error(ArtifactStorageErrorCode.InvalidRequest, "ObjectKey resolves outside the storage profile root.");
            return false;
        }

        return true;
    }

    private static async Task<(long Length, string Sha256)> CopyAndHashAsync(Stream source, string destination, CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
            long length = 0;
            while (true)
            {
                var read = await source.ReadAsync(buffer.AsMemory(0, BufferSize), cancellationToken).ConfigureAwait(false);
                if (read == 0) break;
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                hash.AppendData(buffer, 0, read);
                length += read;
            }
            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            return (length, Convert.ToHexStringLower(hash.GetHashAndReset()));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private async Task<ArtifactStorageError?> PlaceCreateOnlyAsync(string objectKey, string temporaryPath, string destinationPath, CancellationToken cancellationToken)
    {
        var lockDirectory = Path.Combine(_root, ".codespace", "create-locks");
        Directory.CreateDirectory(lockDirectory);
        var lockName = Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(objectKey)));
        var lockPath = Path.Combine(lockDirectory, lockName);
        var deadline = Stopwatch.GetTimestamp() + (long)(Stopwatch.Frequency * 5d);

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            FileStream? placementLock = null;
            try
            {
                placementLock = new FileStream(lockPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.None);
                if (File.Exists(destinationPath)) return Error(ArtifactStorageErrorCode.AlreadyExists, $"Object '{objectKey}' already exists.");
                File.Move(temporaryPath, destinationPath, overwrite: false);
                return null;
            }
            catch (IOException) when (File.Exists(destinationPath))
            {
                return Error(ArtifactStorageErrorCode.AlreadyExists, $"Object '{objectKey}' already exists.");
            }
            catch (IOException) when (placementLock == null && Stopwatch.GetTimestamp() < deadline)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(10), cancellationToken).ConfigureAwait(false);
            }
            catch (IOException ex) when (placementLock == null)
            {
                return Error(ArtifactStorageErrorCode.Unavailable, $"Timed out acquiring atomic placement for object '{objectKey}': {ex.Message}", isRetryable: true);
            }
            finally
            {
                if (placementLock != null)
                {
                    await placementLock.DisposeAsync().ConfigureAwait(false);
                    TryDelete(lockPath);
                }
            }
        }
    }

    private static ArtifactStorageObjectMetadata Metadata(string objectKey, string path, long length, string sha256, string? contentType, IReadOnlyDictionary<string, string> metadata)
    {
        var info = new FileInfo(path);
        return new ArtifactStorageObjectMetadata
        {
            ObjectKey = objectKey,
            Length = length,
            Sha256 = sha256,
            ETag = ETag(info),
            Version = null,
            ContentType = contentType,
            LastModifiedAt = info.LastWriteTimeUtc,
            Metadata = new Dictionary<string, string>(metadata, StringComparer.Ordinal)
        };
    }

    private static ArtifactStorageObjectMetadata Metadata(string objectKey, FileInfo info) => new()
    {
        ObjectKey = objectKey,
        Length = info.Length,
        Sha256 = null,
        ETag = ETag(info),
        Version = null,
        LastModifiedAt = info.LastWriteTimeUtc
    };

    private static string ETag(FileInfo info) => $"W/\"local-{info.Length:x}-{info.LastWriteTimeUtc.Ticks:x}\"";

    private static ArtifactStoragePutResult FailAndDelete(string path, ArtifactStorageErrorCode code, string message)
    {
        TryDelete(path);
        return ArtifactStoragePutResult.Failed(Error(code, message));
    }

    private static ArtifactStorageError Missing(string objectKey) => Error(ArtifactStorageErrorCode.Missing, $"Object '{objectKey}' does not exist.");
    private static ArtifactStorageError Error(ArtifactStorageErrorCode code, string message, bool isRetryable = false) => new(code, message, isRetryable);
    private static bool IsSha256(string value) => value.Length == 64 && value.All(Uri.IsHexDigit);

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    private sealed class BoundedReadStream : Stream
    {
        private readonly Stream _inner;
        private long _remaining;

        public BoundedReadStream(Stream inner, long length)
        {
            _inner = inner;
            _remaining = length;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_remaining == 0) return 0;
            var read = _inner.Read(buffer, offset, (int)Math.Min(count, _remaining));
            _remaining -= read;
            return read;
        }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_remaining == 0) return 0;
            var read = await _inner.ReadAsync(buffer[..(int)Math.Min(buffer.Length, _remaining)], cancellationToken).ConfigureAwait(false);
            _remaining -= read;
            return read;
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing) _inner.Dispose();
            base.Dispose(disposing);
        }
        public override async ValueTask DisposeAsync()
        {
            await _inner.DisposeAsync().ConfigureAwait(false);
            GC.SuppressFinalize(this);
        }
    }
}
