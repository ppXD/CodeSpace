namespace CodeSpace.Core.Services.Workflows.Artifacts.Providers;

/// <summary>
/// Provider-native behaviours a storage policy may require before it admits a profile. This is descriptive only in
/// the first catalog slice: no current <see cref="IArtifactBlobBackend"/> consumer reads these flags, so declaring a
/// module cannot change where artifact bytes are written.
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
}
