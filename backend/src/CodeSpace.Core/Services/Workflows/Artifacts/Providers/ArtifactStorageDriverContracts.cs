using System.Text.Json;

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
/// Ephemeral authorization handle passed to a factory after a trusted secret broker authorizes the reference. The
/// handle is an identifier, not credential material; provider SDK secrets remain owned by the broker/factory scope.
/// </summary>
public sealed record StorageCredentialHandle(string HandleId, StorageSecretReference SecretReference, DateTimeOffset? ExpiresAt = null);

public sealed record ArtifactStorageDriverCreateRequest(StorageProfileSnapshot Profile)
{
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
