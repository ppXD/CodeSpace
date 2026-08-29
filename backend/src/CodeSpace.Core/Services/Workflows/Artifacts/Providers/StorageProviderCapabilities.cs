namespace CodeSpace.Core.Services.Workflows.Artifacts.Providers;

/// <summary>
/// Provider-native behaviours a storage policy may require before it admits a profile. Mostly descriptive: declaring
/// a module cannot change where artifact bytes are written. <see cref="StableETag"/> is the exception — the CAS reads
/// it to decide whether a provider's ETag may be trusted as a durable identity.
/// </summary>
[Flags]
public enum StorageProviderCapabilities : long
{
    None = 0,
    StreamingWrite = 1L << 0,
    StreamingRead = 1L << 1,
    RangeRead = 1L << 2,
    MultipartUpload = 1L << 3,
    ConditionalCreate = 1L << 4,
    ProviderChecksum = 1L << 5,
    ObjectVersioning = 1L << 6,
    SignedDownload = 1L << 7,
    ServerSideEncryption = 1L << 8,
    ObjectLock = 1L << 9,
    Delete = 1L << 10,
    HealthProbe = 1L << 11,

    /// <summary>
    /// The provider's ETag is derived from the object's CONTENT, so it still identifies the same bytes long after
    /// they were written and may be recorded as a durable identity.
    ///
    /// <para>Declare this only when that is literally true. An ETag derived from a modification time, a generation
    /// counter, or anything else the destination may change while the bytes stay put is fine as a same-session
    /// conditional token and must never be persisted: comparing it back later reports intact data as corrupt, and
    /// because the same recorded value gates purge, the object becomes unreadable AND undeletable at once.</para>
    /// </summary>
    StableETag = 1L << 12,
}
