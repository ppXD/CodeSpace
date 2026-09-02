using CodeSpace.Core.Services.Workflows.Artifacts.Providers;
using CodeSpace.Messages.Dtos.Storage;

namespace CodeSpace.Core.Services.Workflows.Artifacts.Runtime;

/// <summary>
/// What a probe SAW, before anything is said about what it was probing: one status and, when the news is bad, one
/// closed stage/code/retryable triple an operator can act on.
///
/// <para>Separated from the probe result DTOs because a destination fails the same way whether the operator is
/// qualifying a saved profile revision or testing configuration they have not saved yet, and those two answers must
/// never disagree. The provider's own text is deliberately absent at this boundary - it can quote a secret - so this
/// closed vocabulary is the whole of what leaves the runtime.</para>
/// </summary>
internal readonly record struct StorageProbeVerdict(StorageProfileProbeStatusValue Status, StorageProfileProbeFailure? Failure)
{
    internal static StorageProbeVerdict Unavailable(StorageProfileProbeFailureStageValue stage, StorageProfileProbeFailureCodeValue code, bool retryable) =>
        new(StorageProfileProbeStatusValue.Unavailable, Fault(stage, code, retryable));

    internal static StorageProbeVerdict Cancelled(StorageProfileProbeFailureCodeValue code) =>
        new(StorageProfileProbeStatusValue.Cancelled, Fault(StorageProfileProbeFailureStageValue.Cancellation, code, true));

    internal static StorageProbeVerdict FromResolution(StorageRuntimeDriverResolution resolution) => resolution switch
    {
        StorageRuntimeDriverResolution.ProfileUnavailable value => new(StorageProfileProbeStatusValue.Unavailable, ProfileFailure(value.Reason)),
        StorageRuntimeDriverResolution.CredentialUnavailable value => new(StorageProfileProbeStatusValue.Unavailable, CredentialFailure(value.Reason)),
        StorageRuntimeDriverResolution.ProviderUnavailable value => new(StorageProfileProbeStatusValue.Unavailable, ProviderFailure(value.Reason)),
        StorageRuntimeDriverResolution.ConfigurationInvalid value => new(StorageProfileProbeStatusValue.Unavailable, ConfigurationFailure(value.Reason)),
        StorageRuntimeDriverResolution.Cancelled value => new(StorageProfileProbeStatusValue.Cancelled, CancellationFailure(value.Stage)),
        StorageRuntimeDriverResolution.DriverInitializationFailed value => new(StorageProfileProbeStatusValue.Unavailable, DriverFailure(value.Reason)),
        _ => Unavailable(StorageProfileProbeFailureStageValue.DriverInitialization, StorageProfileProbeFailureCodeValue.DriverProviderFailure, true),
    };

    internal static StorageProbeVerdict FromProbe(ArtifactStorageProbeResult? probe)
    {
        if (probe == null) return Unavailable(StorageProfileProbeFailureStageValue.Probe, StorageProfileProbeFailureCodeValue.ProbeProviderFailure, true);

        var status = probe.Status switch
        {
            ArtifactStorageProbeStatus.Available => StorageProfileProbeStatusValue.Available,
            ArtifactStorageProbeStatus.ReadOnly => StorageProfileProbeStatusValue.ReadOnly,
            ArtifactStorageProbeStatus.Degraded => StorageProfileProbeStatusValue.Degraded,
            ArtifactStorageProbeStatus.Unavailable => StorageProfileProbeStatusValue.Unavailable,
            _ => StorageProfileProbeStatusValue.Unavailable,
        };
        if (probe.Error != null && status == StorageProfileProbeStatusValue.Available)
            status = probe.Error.IsRetryable ? StorageProfileProbeStatusValue.Degraded : StorageProfileProbeStatusValue.Unavailable;

        return new StorageProbeVerdict(status, probe.Error == null ? DefaultProbeFailure(probe.Status) : ProbeFailure(probe.Error));
    }

    private static StorageProfileProbeFailure? DefaultProbeFailure(ArtifactStorageProbeStatus status) => status switch
    {
        ArtifactStorageProbeStatus.Available => null,
        ArtifactStorageProbeStatus.ReadOnly => Fault(StorageProfileProbeFailureStageValue.Probe, StorageProfileProbeFailureCodeValue.ProbeForbidden, false),
        ArtifactStorageProbeStatus.Degraded or ArtifactStorageProbeStatus.Unavailable => Fault(StorageProfileProbeFailureStageValue.Probe, StorageProfileProbeFailureCodeValue.ProbeUnavailable, true),
        _ => Fault(StorageProfileProbeFailureStageValue.Probe, StorageProfileProbeFailureCodeValue.ProbeProviderFailure, false),
    };

    private static StorageProfileProbeFailure ProfileFailure(StorageRuntimeProfileFailureReason reason) => reason switch
    {
        StorageRuntimeProfileFailureReason.Missing => Fault(StorageProfileProbeFailureStageValue.Profile, StorageProfileProbeFailureCodeValue.ProfileMissing, false),
        StorageRuntimeProfileFailureReason.NotActive => Fault(StorageProfileProbeFailureStageValue.Profile, StorageProfileProbeFailureCodeValue.ProfileNotActive, false),
        StorageRuntimeProfileFailureReason.RevisionMissing => Fault(StorageProfileProbeFailureStageValue.Profile, StorageProfileProbeFailureCodeValue.ProfileRevisionMissing, false),
        _ => Fault(StorageProfileProbeFailureStageValue.Profile, StorageProfileProbeFailureCodeValue.ProfileResolutionFailed, true),
    };

    private static StorageProfileProbeFailure CredentialFailure(StorageRuntimeCredentialFailureReason reason) => reason switch
    {
        StorageRuntimeCredentialFailureReason.Missing => Fault(StorageProfileProbeFailureStageValue.Credential, StorageProfileProbeFailureCodeValue.CredentialMissing, false),
        StorageRuntimeCredentialFailureReason.NotActive => Fault(StorageProfileProbeFailureStageValue.Credential, StorageProfileProbeFailureCodeValue.CredentialNotActive, false),
        StorageRuntimeCredentialFailureReason.RevisionMissing => Fault(StorageProfileProbeFailureStageValue.Credential, StorageProfileProbeFailureCodeValue.CredentialRevisionMissing, false),
        StorageRuntimeCredentialFailureReason.ProviderMismatch => Fault(StorageProfileProbeFailureStageValue.Credential, StorageProfileProbeFailureCodeValue.CredentialProviderMismatch, false),
        StorageRuntimeCredentialFailureReason.ProviderUnavailable => Fault(StorageProfileProbeFailureStageValue.Credential, StorageProfileProbeFailureCodeValue.CredentialProviderUnavailable, true),
        StorageRuntimeCredentialFailureReason.InvalidEnvelope => Fault(StorageProfileProbeFailureStageValue.Credential, StorageProfileProbeFailureCodeValue.CredentialEnvelopeInvalid, false),
        StorageRuntimeCredentialFailureReason.InvalidReference => Fault(StorageProfileProbeFailureStageValue.Credential, StorageProfileProbeFailureCodeValue.CredentialReferenceInvalid, false),
        StorageRuntimeCredentialFailureReason.InvalidSecret => Fault(StorageProfileProbeFailureStageValue.Credential, StorageProfileProbeFailureCodeValue.CredentialSecretInvalid, false),
        _ => Fault(StorageProfileProbeFailureStageValue.Credential, StorageProfileProbeFailureCodeValue.CredentialResolutionFailed, true),
    };

    private static StorageProfileProbeFailure ProviderFailure(StorageRuntimeProviderFailureReason reason) => reason switch
    {
        StorageRuntimeProviderFailureReason.ModuleMissing => Fault(StorageProfileProbeFailureStageValue.Provider, StorageProfileProbeFailureCodeValue.ProviderModuleMissing, false),
        StorageRuntimeProviderFailureReason.FactoryMissing => Fault(StorageProfileProbeFailureStageValue.Provider, StorageProfileProbeFailureCodeValue.ProviderFactoryMissing, false),
        StorageRuntimeProviderFailureReason.FactoryMismatch => Fault(StorageProfileProbeFailureStageValue.Provider, StorageProfileProbeFailureCodeValue.ProviderFactoryMismatch, false),
        _ => Fault(StorageProfileProbeFailureStageValue.Provider, StorageProfileProbeFailureCodeValue.ProviderCatalogFailure, false),
    };

    private static StorageProfileProbeFailure ConfigurationFailure(StorageRuntimeConfigurationFailureReason reason) => reason switch
    {
        StorageRuntimeConfigurationFailureReason.InvalidConfiguration => Fault(StorageProfileProbeFailureStageValue.Configuration, StorageProfileProbeFailureCodeValue.ConfigurationInvalid, false),
        StorageRuntimeConfigurationFailureReason.UnsupportedSchemaVersion => Fault(StorageProfileProbeFailureStageValue.Configuration, StorageProfileProbeFailureCodeValue.ConfigurationSchemaUnsupported, false),
        StorageRuntimeConfigurationFailureReason.SnapshotIdentityMismatch => Fault(StorageProfileProbeFailureStageValue.Configuration, StorageProfileProbeFailureCodeValue.SnapshotIdentityMismatch, false),
        StorageRuntimeConfigurationFailureReason.InvalidProviderTypeKey => Fault(StorageProfileProbeFailureStageValue.Configuration, StorageProfileProbeFailureCodeValue.ProviderTypeKeyInvalid, false),
        _ => Fault(StorageProfileProbeFailureStageValue.Configuration, StorageProfileProbeFailureCodeValue.FactoryRejectedConfiguration, false),
    };

    private static StorageProfileProbeFailure DriverFailure(StorageRuntimeDriverInitializationFailureReason reason) => reason switch
    {
        StorageRuntimeDriverInitializationFailureReason.NullDriver => Fault(StorageProfileProbeFailureStageValue.DriverInitialization, StorageProfileProbeFailureCodeValue.DriverNull, false),
        StorageRuntimeDriverInitializationFailureReason.ProviderCanceled => Fault(StorageProfileProbeFailureStageValue.DriverInitialization, StorageProfileProbeFailureCodeValue.DriverProviderCancelled, true),
        StorageRuntimeDriverInitializationFailureReason.CleanupFailure => Fault(StorageProfileProbeFailureStageValue.DriverCleanup, StorageProfileProbeFailureCodeValue.DriverCleanupFailure, true),
        _ => Fault(StorageProfileProbeFailureStageValue.DriverInitialization, StorageProfileProbeFailureCodeValue.DriverProviderFailure, true),
    };

    private static StorageProfileProbeFailure CancellationFailure(StorageRuntimeCancellationStage stage) => stage switch
    {
        StorageRuntimeCancellationStage.ProfileResolution => Fault(StorageProfileProbeFailureStageValue.Cancellation, StorageProfileProbeFailureCodeValue.CancelledProfileResolution, true),
        StorageRuntimeCancellationStage.CredentialResolution => Fault(StorageProfileProbeFailureStageValue.Cancellation, StorageProfileProbeFailureCodeValue.CancelledCredentialResolution, true),
        _ => Fault(StorageProfileProbeFailureStageValue.Cancellation, StorageProfileProbeFailureCodeValue.CancelledDriverInitialization, true),
    };

    private static StorageProfileProbeFailure ProbeFailure(ArtifactStorageError error) => Fault(StorageProfileProbeFailureStageValue.Probe, ProbeFailureCode(error), error.IsRetryable);

    private static StorageProfileProbeFailureCodeValue ProbeFailureCode(ArtifactStorageError error) => error.Reason switch
    {
        ArtifactStorageFailureReason.CredentialInvalid => StorageProfileProbeFailureCodeValue.ProbeCredentialInvalid,
        ArtifactStorageFailureReason.SignatureMismatch => StorageProfileProbeFailureCodeValue.ProbeSignatureMismatch,
        ArtifactStorageFailureReason.SecurityTokenInvalid => StorageProfileProbeFailureCodeValue.ProbeSecurityTokenInvalid,
        ArtifactStorageFailureReason.SecurityTokenExpired => StorageProfileProbeFailureCodeValue.ProbeSecurityTokenExpired,
        ArtifactStorageFailureReason.SecurityTokenMissing => StorageProfileProbeFailureCodeValue.ProbeSecurityTokenMissing,
        ArtifactStorageFailureReason.ClockSkew => StorageProfileProbeFailureCodeValue.ProbeClockSkew,
        ArtifactStorageFailureReason.DestinationMissing => StorageProfileProbeFailureCodeValue.ProbeDestinationMissing,
        ArtifactStorageFailureReason.PermissionDenied => StorageProfileProbeFailureCodeValue.ProbePermissionDenied,
        ArtifactStorageFailureReason.NetworkUnavailable => StorageProfileProbeFailureCodeValue.ProbeNetworkUnavailable,
        _ => ProbeFailureCode(error.Code),
    };

    private static StorageProfileProbeFailureCodeValue ProbeFailureCode(ArtifactStorageErrorCode code) => code switch
    {
        ArtifactStorageErrorCode.InvalidRequest => StorageProfileProbeFailureCodeValue.ProbeInvalidRequest,
        ArtifactStorageErrorCode.Missing => StorageProfileProbeFailureCodeValue.ProbeMissing,
        ArtifactStorageErrorCode.AlreadyExists => StorageProfileProbeFailureCodeValue.ProbeAlreadyExists,
        ArtifactStorageErrorCode.ConditionNotMet => StorageProfileProbeFailureCodeValue.ProbeConditionNotMet,
        ArtifactStorageErrorCode.IntegrityMismatch => StorageProfileProbeFailureCodeValue.ProbeIntegrityMismatch,
        ArtifactStorageErrorCode.Corrupt => StorageProfileProbeFailureCodeValue.ProbeCorrupt,
        ArtifactStorageErrorCode.Unauthorized => StorageProfileProbeFailureCodeValue.ProbeUnauthorized,
        ArtifactStorageErrorCode.Forbidden => StorageProfileProbeFailureCodeValue.ProbeForbidden,
        ArtifactStorageErrorCode.Throttled => StorageProfileProbeFailureCodeValue.ProbeThrottled,
        ArtifactStorageErrorCode.Unavailable => StorageProfileProbeFailureCodeValue.ProbeUnavailable,
        ArtifactStorageErrorCode.Unsupported => StorageProfileProbeFailureCodeValue.ProbeUnsupported,
        _ => StorageProfileProbeFailureCodeValue.ProbeProviderFailure,
    };

    private static StorageProfileProbeFailure Fault(StorageProfileProbeFailureStageValue stage, StorageProfileProbeFailureCodeValue code, bool retryable) => new()
    {
        Stage = stage,
        Code = code,
        Retryable = retryable,
    };
}
