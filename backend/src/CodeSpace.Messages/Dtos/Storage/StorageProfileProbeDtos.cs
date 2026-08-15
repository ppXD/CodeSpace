namespace CodeSpace.Messages.Dtos.Storage;

/// <summary>Secret-free qualification result for one exact immutable storage-profile revision.</summary>
public sealed record StorageProfileProbeResult
{
    public required Guid ProfileId { get; init; }
    public int? ProfileRevision { get; init; }
    public string? ProviderTypeKey { get; init; }
    public required bool WriteAccessRequested { get; init; }
    public required StorageProfileProbeStatusValue Status { get; init; }
    public required long LatencyMilliseconds { get; init; }
    public StorageProfileProbeFailure? Failure { get; init; }
}

public sealed record StorageProfileProbeFailure
{
    public required StorageProfileProbeFailureStageValue Stage { get; init; }
    public required StorageProfileProbeFailureCodeValue Code { get; init; }
    public required bool Retryable { get; init; }
}

public enum StorageProfileProbeStatusValue
{
    Available,
    ReadOnly,
    Degraded,
    Unavailable,
    Cancelled,
}

public enum StorageProfileProbeFailureStageValue
{
    Profile,
    Credential,
    Provider,
    Configuration,
    DriverInitialization,
    Probe,
    Cancellation,
    DriverCleanup,
}

public enum StorageProfileProbeFailureCodeValue
{
    ProfileMissing,
    ProfileNotActive,
    ProfileRevisionMissing,
    ProfileRevisionInvalid,
    ProfileResolutionFailed,
    CredentialMissing,
    CredentialNotActive,
    CredentialRevisionMissing,
    CredentialProviderMismatch,
    CredentialProviderUnavailable,
    CredentialEnvelopeInvalid,
    CredentialReferenceInvalid,
    CredentialSecretInvalid,
    CredentialResolutionFailed,
    ProviderModuleMissing,
    ProviderFactoryMissing,
    ProviderFactoryMismatch,
    ProviderCatalogFailure,
    ConfigurationInvalid,
    ConfigurationSchemaUnsupported,
    SnapshotIdentityMismatch,
    ProviderTypeKeyInvalid,
    FactoryRejectedConfiguration,
    DriverNull,
    DriverProviderCancelled,
    DriverProviderFailure,
    DriverCleanupFailure,
    CancelledProfileResolution,
    CancelledCredentialResolution,
    CancelledDriverInitialization,
    CancelledProbe,
    ProbeInvalidRequest,
    ProbeMissing,
    ProbeAlreadyExists,
    ProbeConditionNotMet,
    ProbeIntegrityMismatch,
    ProbeCorrupt,
    ProbeUnauthorized,
    ProbeForbidden,
    ProbeThrottled,
    ProbeUnavailable,
    ProbeUnsupported,
    ProbeProviderFailure,
}
