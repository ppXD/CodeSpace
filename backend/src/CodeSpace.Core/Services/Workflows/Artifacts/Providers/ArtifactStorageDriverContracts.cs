using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodeSpace.Core.Services.Workflows.Artifacts.Providers;

/// <summary>
/// Immutable, versioned input used to create a storage driver. It contains only non-secret configuration and an
/// opaque reference to credentials; plaintext provider secrets must never be embedded in or serialized with it.
/// </summary>
public sealed record StorageProfileSnapshot
{
    private JsonElement _configuration;

    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    public required Guid ProfileId { get; init; }
    public required int ProfileRevision { get; init; }
    public required string ProviderTypeKey { get; init; }
    public required JsonElement Configuration { get => _configuration; init => _configuration = value.Clone(); }
    public StorageSecretReference? SecretReference { get; init; }
}

/// <summary>Opaque locator resolved by the provider factory's trusted credential source at runtime.</summary>
public sealed record StorageSecretReference(string SecretStoreType, string SecretId, string? SecretVersion = null);

/// <summary>
/// Ephemeral, in-process credential activation handle. A factory may synchronously materialize its provider SDK
/// credential through <see cref="UseSecret{T}"/> while <c>CreateAsync</c> is running, but must never retain this handle or
/// the supplied JSON element. The runtime broker owns and disposes it as soon as driver creation completes.
/// </summary>
public sealed class StorageCredentialHandle : IDisposable, IJsonOnSerializing
{
    private readonly object _gate = new();
    private JsonElement _secret;
    private bool _disposed;

    internal StorageCredentialHandle(JsonElement secret)
    {
        if (secret.ValueKind != JsonValueKind.Object) throw new ArgumentException("A storage credential secret must be a JSON object.", nameof(secret));
        _secret = secret.Clone();
    }

    public T UseSecret<T>(Func<JsonElement, T> materialize)
    {
        ArgumentNullException.ThrowIfNull(materialize);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return materialize(_secret);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _secret = default;
            _disposed = true;
        }
    }

    public void OnSerializing() => throw new NotSupportedException("Runtime storage credential handles cannot be serialized.");
    public override string ToString() => "StorageCredentialHandle { Secret = [REDACTED] }";
}

public sealed record ArtifactStorageDriverCreateRequest(StorageProfileSnapshot Profile)
{
    [JsonIgnore]
    public StorageCredentialHandle? CredentialHandle { get; init; }
}

public sealed record ArtifactStoragePutRequest(string ObjectKey, Stream Content)
{
    public long? ContentLength { get; init; }
    public string? ExpectedSha256 { get; init; }
    public string? ContentType { get; init; }
    public ArtifactStorageWriteCondition Condition { get; init; }
    public string? ExpectedETag { get; init; }
    public IReadOnlyDictionary<string, string> Metadata { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);
}

public sealed record ArtifactStorageHeadRequest(string ObjectKey);

public sealed record ArtifactStorageReadRequest(string ObjectKey)
{
    public ArtifactStorageByteRange? Range { get; init; }
    public string? ExpectedETag { get; init; }
    public string? ExpectedVersion { get; init; }
}

public sealed record ArtifactStorageDeleteRequest(string ObjectKey)
{
    public string? ExpectedETag { get; init; }
    public string? ExpectedVersion { get; init; }
}

public sealed record ArtifactStorageProbeRequest
{
    public bool VerifyWriteAccess { get; init; }

    /// <summary>
    /// Whether this probe is part of PROVISIONING the destination, and may therefore create what is missing.
    ///
    /// <para>Separate from <see cref="VerifyWriteAccess"/> on purpose. Write-verification is a question, and letting
    /// it also be an action is what let a monitoring sweep recreate a vanished mount, report it healthy, and then
    /// supply the integrity verifier with the corroboration it needed to demote every placement underneath. An
    /// operator adopting a destination is provisioning it; a sweep watching one never is.</para>
    /// </summary>
    public bool Initialize { get; init; }
}

public readonly record struct ArtifactStorageByteRange(long Offset, long? Length = null);

public enum ArtifactStorageWriteCondition
{
    None = 0,
    CreateOnly = 1,
    MatchETag = 2,
}

public enum ArtifactStorageErrorCode
{
    InvalidRequest = 0,
    Missing = 1,
    AlreadyExists = 2,
    ConditionNotMet = 3,
    IntegrityMismatch = 4,
    Corrupt = 5,
    Unauthorized = 6,
    Forbidden = 7,
    Throttled = 8,
    Unavailable = 9,
    Unsupported = 10,
    ProviderFailure = 11,
}

public sealed record ArtifactStorageError(ArtifactStorageErrorCode Code, string Message, bool IsRetryable = false, string? ProviderCode = null);

/// <summary>
/// Optional thrown-failure contract for a provider operation that could not return its normal typed result. Drivers
/// should prefer <see cref="ArtifactStorageError"/>; this marker lets plugin transports classify an exceptional path
/// without exposing exception text. Unmarked programming exceptions remain programming faults and are rethrown.
/// </summary>
public interface IArtifactStorageOperationalException
{
    ArtifactStorageErrorCode Code { get; }
    bool IsRetryable { get; }
}

public sealed record ArtifactStorageObjectMetadata
{
    public required string ObjectKey { get; init; }
    public required long Length { get; init; }
    public string? Sha256 { get; init; }
    public string? ETag { get; init; }
    public string? Version { get; init; }
    public string? ContentType { get; init; }
    public DateTimeOffset? LastModifiedAt { get; init; }
    public IReadOnlyDictionary<string, string> Metadata { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);
}

public sealed class ArtifactStoragePutResult
{
    public bool IsSuccess => Error == null;
    public ArtifactStorageObjectMetadata? Metadata { get; }
    public ArtifactStorageError? Error { get; }

    private ArtifactStoragePutResult(ArtifactStorageObjectMetadata? metadata, ArtifactStorageError? error)
    {
        Metadata = metadata;
        Error = error;
    }

    public static ArtifactStoragePutResult Stored(ArtifactStorageObjectMetadata metadata) => new(ArtifactStorageResultGuard.RequireMetadata(metadata), null);
    public static ArtifactStoragePutResult Failed(ArtifactStorageError error) => new(null, ArtifactStorageResultGuard.RequireError(error));
}

public sealed class ArtifactStorageHeadResult
{
    public bool IsSuccess => Error == null;
    public ArtifactStorageObjectMetadata? Metadata { get; }
    public ArtifactStorageError? Error { get; }

    private ArtifactStorageHeadResult(ArtifactStorageObjectMetadata? metadata, ArtifactStorageError? error)
    {
        Metadata = metadata;
        Error = error;
    }

    public static ArtifactStorageHeadResult Found(ArtifactStorageObjectMetadata metadata) => new(ArtifactStorageResultGuard.RequireMetadata(metadata), null);
    public static ArtifactStorageHeadResult Failed(ArtifactStorageError error) => new(null, ArtifactStorageResultGuard.RequireError(error));
}

/// <summary>
/// Typed open handle. A successful result exclusively owns <see cref="Content"/>; the caller must dispose that
/// stream after use. A failed result can never carry a stream or metadata payload.
/// </summary>
public sealed class ArtifactStorageReadResult
{
    public bool IsSuccess => Error == null;
    public Stream? Content { get; }
    public long ContentLength { get; }
    public long TotalLength { get; }
    public ArtifactStorageObjectMetadata? Metadata { get; }
    public ArtifactStorageError? Error { get; }

    private ArtifactStorageReadResult(Stream? content, long contentLength, long totalLength, ArtifactStorageObjectMetadata? metadata, ArtifactStorageError? error)
    {
        Content = content;
        ContentLength = contentLength;
        TotalLength = totalLength;
        Metadata = metadata;
        Error = error;
    }

    public static ArtifactStorageReadResult Opened(Stream content, long contentLength, long totalLength, ArtifactStorageObjectMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (!content.CanRead) throw new ArgumentException("An opened artifact stream must be readable.", nameof(content));
        if (contentLength < 0 || totalLength < 0 || contentLength > totalLength)
            throw new ArgumentOutOfRangeException(nameof(contentLength), "Artifact read lengths must satisfy 0 <= contentLength <= totalLength.");
        return new ArtifactStorageReadResult(content, contentLength, totalLength, ArtifactStorageResultGuard.RequireMetadata(metadata), null);
    }

    public static ArtifactStorageReadResult Failed(ArtifactStorageError error) => new(null, 0, 0, null, ArtifactStorageResultGuard.RequireError(error));
}

public sealed class ArtifactStorageDeleteResult
{
    public bool IsSuccess => Error == null;
    public bool Deleted { get; }
    public ArtifactStorageError? Error { get; }

    private ArtifactStorageDeleteResult(bool deleted, ArtifactStorageError? error)
    {
        Deleted = deleted;
        Error = error;
    }

    public static ArtifactStorageDeleteResult Removed() => new(true, null);
    public static ArtifactStorageDeleteResult Failed(ArtifactStorageError error) => new(false, ArtifactStorageResultGuard.RequireError(error));
}

public enum ArtifactStorageProbeStatus
{
    Available = 0,
    ReadOnly = 1,
    Degraded = 2,
    Unavailable = 3,
}

public sealed record ArtifactStorageProbeResult
{
    public required ArtifactStorageProbeStatus Status { get; init; }
    public required TimeSpan Latency { get; init; }
    public ArtifactStorageError? Error { get; init; }
}

file static class ArtifactStorageResultGuard
{
    public static ArtifactStorageObjectMetadata RequireMetadata(ArtifactStorageObjectMetadata? metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        if (string.IsNullOrWhiteSpace(metadata.ObjectKey)) throw new ArgumentException("Artifact metadata requires an object key.", nameof(metadata));
        if (metadata.Length < 0) throw new ArgumentOutOfRangeException(nameof(metadata), "Artifact metadata length cannot be negative.");
        return metadata;
    }

    public static ArtifactStorageError RequireError(ArtifactStorageError? error)
    {
        ArgumentNullException.ThrowIfNull(error);
        if (string.IsNullOrWhiteSpace(error.Message)) throw new ArgumentException("Artifact errors require a message.", nameof(error));
        return error;
    }
}
