using System.Diagnostics;
using CodeSpace.Core.Services.Workflows.Artifacts.Providers;
using CodeSpace.Messages.Dtos.Storage;

namespace CodeSpace.Core.Services.Workflows.Artifacts.Runtime;

/// <summary>
/// Qualifies the exact runtime path selected by Settings. Provider-owned text and codes remain below this boundary;
/// callers receive only closed CodeSpace vocabulary, wall latency and retryability.
/// </summary>
public sealed class StorageProfileProbeService : IStorageProfileProbeService
{
    private readonly IStorageProfileProbeTargetResolver _targets;
    private readonly IStorageRuntimeDriverBroker _broker;

    public StorageProfileProbeService(IStorageProfileProbeTargetResolver targets, IStorageRuntimeDriverBroker broker)
    {
        _targets = targets;
        _broker = broker;
    }

    public async Task<StorageProfileProbeResult> ProbeAsync(StorageProfileProbeRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var stopwatch = Stopwatch.StartNew();
        StorageProfileProbeTarget? target;
        try
        {
            target = await _targets.ResolveAsync(new StorageProfileProbeTargetRequest(request.TeamId, request.ProfileId, request.ProfileRevision), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Result(new ProbeResultContext(request.ProfileId, request.ProfileRevision, null, request.VerifyWriteAccess, stopwatch), StorageProfileProbeStatusValue.Cancelled, Failure(StorageProfileProbeFailureStageValue.Cancellation, StorageProfileProbeFailureCodeValue.CancelledProfileResolution, true));
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            return Result(new ProbeResultContext(request.ProfileId, request.ProfileRevision, null, request.VerifyWriteAccess, stopwatch), StorageProfileProbeStatusValue.Unavailable, Failure(StorageProfileProbeFailureStageValue.Profile, StorageProfileProbeFailureCodeValue.ProfileResolutionFailed, true));
        }

        if (target == null)
            return Result(new ProbeResultContext(request.ProfileId, request.ProfileRevision, null, request.VerifyWriteAccess, stopwatch), StorageProfileProbeStatusValue.Unavailable, Failure(StorageProfileProbeFailureStageValue.Profile, StorageProfileProbeFailureCodeValue.ProfileMissing, false));

        var revision = target.ProfileRevision;
        var context = new ProbeResultContext(request.ProfileId, revision, target.ProviderTypeKey, request.VerifyWriteAccess, stopwatch);
        if (revision <= 0)
            return Result(context, StorageProfileProbeStatusValue.Unavailable, Failure(StorageProfileProbeFailureStageValue.Profile, StorageProfileProbeFailureCodeValue.ProfileRevisionInvalid, false));

        StorageRuntimeDriverResolution resolution;
        try
        {
            resolution = await _broker.OpenAsync(new StorageRuntimeDriverRequest(request.TeamId, request.ProfileId, revision), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Result(context, StorageProfileProbeStatusValue.Cancelled, Failure(StorageProfileProbeFailureStageValue.Cancellation, StorageProfileProbeFailureCodeValue.CancelledDriverInitialization, true));
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            return Result(context, StorageProfileProbeStatusValue.Unavailable, Failure(StorageProfileProbeFailureStageValue.DriverInitialization, StorageProfileProbeFailureCodeValue.DriverProviderFailure, true));
        }

        if (resolution is not StorageRuntimeDriverResolution.Ready ready) return MapResolution(context, resolution);
        StorageProfileProbeResult? result = null;
        StorageProfileProbeFailure? cleanupFailure = null;
        try
        {
            result = await ProbeAsync(context, ready.Lease, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            try
            {
                await ready.Lease.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception exception) when (IsRecoverable(exception))
            {
                cleanupFailure = Failure(StorageProfileProbeFailureStageValue.DriverCleanup, StorageProfileProbeFailureCodeValue.DriverCleanupFailure, true);
            }
        }
        return cleanupFailure == null
            ? result ?? Result(context, StorageProfileProbeStatusValue.Unavailable, Failure(StorageProfileProbeFailureStageValue.Probe, StorageProfileProbeFailureCodeValue.ProbeProviderFailure, true))
            : Result(context, StorageProfileProbeStatusValue.Unavailable, cleanupFailure);
    }

    private static async Task<StorageProfileProbeResult> ProbeAsync(ProbeResultContext context, StorageRuntimeDriverLease lease, CancellationToken cancellationToken)
    {
        try
        {
            var probe = await lease.Driver.ProbeAsync(new ArtifactStorageProbeRequest { VerifyWriteAccess = context.WriteAccessRequested }, cancellationToken).ConfigureAwait(false);
            return MapProbe(context, probe);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Result(context, StorageProfileProbeStatusValue.Cancelled, Failure(StorageProfileProbeFailureStageValue.Cancellation, StorageProfileProbeFailureCodeValue.CancelledProbe, true));
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            return Result(context, StorageProfileProbeStatusValue.Unavailable, Failure(StorageProfileProbeFailureStageValue.Probe, StorageProfileProbeFailureCodeValue.ProbeProviderFailure, true));
        }
    }

    private static StorageProfileProbeResult MapResolution(ProbeResultContext context, StorageRuntimeDriverResolution resolution) => resolution switch
    {
        StorageRuntimeDriverResolution.ProfileUnavailable value => Result(context, StorageProfileProbeStatusValue.Unavailable, ProfileFailure(value.Reason)),
        StorageRuntimeDriverResolution.CredentialUnavailable value => Result(context, StorageProfileProbeStatusValue.Unavailable, CredentialFailure(value.Reason)),
        StorageRuntimeDriverResolution.ProviderUnavailable value => Result(context, StorageProfileProbeStatusValue.Unavailable, ProviderFailure(value.Reason)),
        StorageRuntimeDriverResolution.ConfigurationInvalid value => Result(context, StorageProfileProbeStatusValue.Unavailable, ConfigurationFailure(value.Reason)),
        StorageRuntimeDriverResolution.Cancelled value => Result(context, StorageProfileProbeStatusValue.Cancelled, CancellationFailure(value.Stage)),
        StorageRuntimeDriverResolution.DriverInitializationFailed value => Result(context, StorageProfileProbeStatusValue.Unavailable, DriverFailure(value.Reason)),
        _ => Result(context, StorageProfileProbeStatusValue.Unavailable, Failure(StorageProfileProbeFailureStageValue.DriverInitialization, StorageProfileProbeFailureCodeValue.DriverProviderFailure, true)),
    };

    private static StorageProfileProbeResult MapProbe(ProbeResultContext context, ArtifactStorageProbeResult? probe)
    {
        if (probe == null)
            return Result(context, StorageProfileProbeStatusValue.Unavailable, Failure(StorageProfileProbeFailureStageValue.Probe, StorageProfileProbeFailureCodeValue.ProbeProviderFailure, true));

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
        var failure = probe.Error == null ? DefaultProbeFailure(probe.Status) : ProbeFailure(probe.Error);
        return Result(context, status, failure);
    }

    private static StorageProfileProbeFailure? DefaultProbeFailure(ArtifactStorageProbeStatus status) => status switch
    {
        ArtifactStorageProbeStatus.Available => null,
        ArtifactStorageProbeStatus.ReadOnly => Failure(StorageProfileProbeFailureStageValue.Probe, StorageProfileProbeFailureCodeValue.ProbeForbidden, false),
        ArtifactStorageProbeStatus.Degraded or ArtifactStorageProbeStatus.Unavailable => Failure(StorageProfileProbeFailureStageValue.Probe, StorageProfileProbeFailureCodeValue.ProbeUnavailable, true),
        _ => Failure(StorageProfileProbeFailureStageValue.Probe, StorageProfileProbeFailureCodeValue.ProbeProviderFailure, false),
    };

    private static StorageProfileProbeFailure ProfileFailure(StorageRuntimeProfileFailureReason reason) => reason switch
    {
        StorageRuntimeProfileFailureReason.Missing => Failure(StorageProfileProbeFailureStageValue.Profile, StorageProfileProbeFailureCodeValue.ProfileMissing, false),
        StorageRuntimeProfileFailureReason.NotActive => Failure(StorageProfileProbeFailureStageValue.Profile, StorageProfileProbeFailureCodeValue.ProfileNotActive, false),
        StorageRuntimeProfileFailureReason.RevisionMissing => Failure(StorageProfileProbeFailureStageValue.Profile, StorageProfileProbeFailureCodeValue.ProfileRevisionMissing, false),
        _ => Failure(StorageProfileProbeFailureStageValue.Profile, StorageProfileProbeFailureCodeValue.ProfileResolutionFailed, true),
    };

    private static StorageProfileProbeFailure CredentialFailure(StorageRuntimeCredentialFailureReason reason) => reason switch
    {
        StorageRuntimeCredentialFailureReason.Missing => Failure(StorageProfileProbeFailureStageValue.Credential, StorageProfileProbeFailureCodeValue.CredentialMissing, false),
        StorageRuntimeCredentialFailureReason.NotActive => Failure(StorageProfileProbeFailureStageValue.Credential, StorageProfileProbeFailureCodeValue.CredentialNotActive, false),
        StorageRuntimeCredentialFailureReason.RevisionMissing => Failure(StorageProfileProbeFailureStageValue.Credential, StorageProfileProbeFailureCodeValue.CredentialRevisionMissing, false),
        StorageRuntimeCredentialFailureReason.ProviderMismatch => Failure(StorageProfileProbeFailureStageValue.Credential, StorageProfileProbeFailureCodeValue.CredentialProviderMismatch, false),
        StorageRuntimeCredentialFailureReason.ProviderUnavailable => Failure(StorageProfileProbeFailureStageValue.Credential, StorageProfileProbeFailureCodeValue.CredentialProviderUnavailable, true),
        StorageRuntimeCredentialFailureReason.InvalidEnvelope => Failure(StorageProfileProbeFailureStageValue.Credential, StorageProfileProbeFailureCodeValue.CredentialEnvelopeInvalid, false),
        StorageRuntimeCredentialFailureReason.InvalidReference => Failure(StorageProfileProbeFailureStageValue.Credential, StorageProfileProbeFailureCodeValue.CredentialReferenceInvalid, false),
        StorageRuntimeCredentialFailureReason.InvalidSecret => Failure(StorageProfileProbeFailureStageValue.Credential, StorageProfileProbeFailureCodeValue.CredentialSecretInvalid, false),
        _ => Failure(StorageProfileProbeFailureStageValue.Credential, StorageProfileProbeFailureCodeValue.CredentialResolutionFailed, true),
    };

    private static StorageProfileProbeFailure ProviderFailure(StorageRuntimeProviderFailureReason reason) => reason switch
    {
        StorageRuntimeProviderFailureReason.ModuleMissing => Failure(StorageProfileProbeFailureStageValue.Provider, StorageProfileProbeFailureCodeValue.ProviderModuleMissing, false),
        StorageRuntimeProviderFailureReason.FactoryMissing => Failure(StorageProfileProbeFailureStageValue.Provider, StorageProfileProbeFailureCodeValue.ProviderFactoryMissing, false),
        StorageRuntimeProviderFailureReason.FactoryMismatch => Failure(StorageProfileProbeFailureStageValue.Provider, StorageProfileProbeFailureCodeValue.ProviderFactoryMismatch, false),
        _ => Failure(StorageProfileProbeFailureStageValue.Provider, StorageProfileProbeFailureCodeValue.ProviderCatalogFailure, false),
    };

    private static StorageProfileProbeFailure ConfigurationFailure(StorageRuntimeConfigurationFailureReason reason) => reason switch
    {
        StorageRuntimeConfigurationFailureReason.InvalidConfiguration => Failure(StorageProfileProbeFailureStageValue.Configuration, StorageProfileProbeFailureCodeValue.ConfigurationInvalid, false),
        StorageRuntimeConfigurationFailureReason.UnsupportedSchemaVersion => Failure(StorageProfileProbeFailureStageValue.Configuration, StorageProfileProbeFailureCodeValue.ConfigurationSchemaUnsupported, false),
        StorageRuntimeConfigurationFailureReason.SnapshotIdentityMismatch => Failure(StorageProfileProbeFailureStageValue.Configuration, StorageProfileProbeFailureCodeValue.SnapshotIdentityMismatch, false),
        StorageRuntimeConfigurationFailureReason.InvalidProviderTypeKey => Failure(StorageProfileProbeFailureStageValue.Configuration, StorageProfileProbeFailureCodeValue.ProviderTypeKeyInvalid, false),
        _ => Failure(StorageProfileProbeFailureStageValue.Configuration, StorageProfileProbeFailureCodeValue.FactoryRejectedConfiguration, false),
    };

    private static StorageProfileProbeFailure DriverFailure(StorageRuntimeDriverInitializationFailureReason reason) => reason switch
    {
        StorageRuntimeDriverInitializationFailureReason.NullDriver => Failure(StorageProfileProbeFailureStageValue.DriverInitialization, StorageProfileProbeFailureCodeValue.DriverNull, false),
        StorageRuntimeDriverInitializationFailureReason.ProviderCanceled => Failure(StorageProfileProbeFailureStageValue.DriverInitialization, StorageProfileProbeFailureCodeValue.DriverProviderCancelled, true),
        StorageRuntimeDriverInitializationFailureReason.CleanupFailure => Failure(StorageProfileProbeFailureStageValue.DriverCleanup, StorageProfileProbeFailureCodeValue.DriverCleanupFailure, true),
        _ => Failure(StorageProfileProbeFailureStageValue.DriverInitialization, StorageProfileProbeFailureCodeValue.DriverProviderFailure, true),
    };

    private static StorageProfileProbeFailure CancellationFailure(StorageRuntimeCancellationStage stage) => stage switch
    {
        StorageRuntimeCancellationStage.ProfileResolution => Failure(StorageProfileProbeFailureStageValue.Cancellation, StorageProfileProbeFailureCodeValue.CancelledProfileResolution, true),
        StorageRuntimeCancellationStage.CredentialResolution => Failure(StorageProfileProbeFailureStageValue.Cancellation, StorageProfileProbeFailureCodeValue.CancelledCredentialResolution, true),
        _ => Failure(StorageProfileProbeFailureStageValue.Cancellation, StorageProfileProbeFailureCodeValue.CancelledDriverInitialization, true),
    };

    private static StorageProfileProbeFailure ProbeFailure(ArtifactStorageError error) => Failure(StorageProfileProbeFailureStageValue.Probe, error.Code switch
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
    }, error.IsRetryable);

    private static StorageProfileProbeFailure Failure(StorageProfileProbeFailureStageValue stage, StorageProfileProbeFailureCodeValue code, bool retryable) => new()
    {
        Stage = stage,
        Code = code,
        Retryable = retryable,
    };

    private static StorageProfileProbeResult Result(ProbeResultContext context, StorageProfileProbeStatusValue status, StorageProfileProbeFailure? failure) => new()
    {
        ProfileId = context.ProfileId,
        ProfileRevision = context.ProfileRevision,
        ProviderTypeKey = context.ProviderTypeKey,
        WriteAccessRequested = context.WriteAccessRequested,
        Status = status,
        LatencyMilliseconds = Math.Max(0, context.Stopwatch.ElapsedMilliseconds),
        Failure = failure,
    };

    private static bool IsRecoverable(Exception exception) => exception is not OutOfMemoryException and not AccessViolationException;

    private sealed record ProbeResultContext(Guid ProfileId, int? ProfileRevision, string? ProviderTypeKey, bool WriteAccessRequested, Stopwatch Stopwatch);
}
