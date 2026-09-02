using System.Diagnostics;
using CodeSpace.Core.Services.Workflows.Artifacts.Profiles;
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
            return Result(new ProbeResultContext(request.ProfileId, request.ProfileRevision, null, request.VerifyWriteAccess, stopwatch, Provisions(request)), StorageProbeVerdict.Cancelled(StorageProfileProbeFailureCodeValue.CancelledProfileResolution));
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            return Result(new ProbeResultContext(request.ProfileId, request.ProfileRevision, null, request.VerifyWriteAccess, stopwatch, Provisions(request)), StorageProbeVerdict.Unavailable(StorageProfileProbeFailureStageValue.Profile, StorageProfileProbeFailureCodeValue.ProfileResolutionFailed, true));
        }

        if (target == null)
            return Result(new ProbeResultContext(request.ProfileId, request.ProfileRevision, null, request.VerifyWriteAccess, stopwatch, Provisions(request)), StorageProbeVerdict.Unavailable(StorageProfileProbeFailureStageValue.Profile, StorageProfileProbeFailureCodeValue.ProfileMissing, false));

        var revision = target.ProfileRevision;
        var context = new ProbeResultContext(request.ProfileId, revision, target.ProviderTypeKey, request.VerifyWriteAccess, stopwatch, Provisions(request));
        if (revision <= 0)
            return Result(context, StorageProbeVerdict.Unavailable(StorageProfileProbeFailureStageValue.Profile, StorageProfileProbeFailureCodeValue.ProfileRevisionInvalid, false));

        StorageRuntimeDriverResolution resolution;
        try
        {
            resolution = await _broker.OpenAsync(new StorageRuntimeDriverRequest(request.TeamId, request.ProfileId, revision, Eligibility(request)), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Result(context, StorageProbeVerdict.Cancelled(StorageProfileProbeFailureCodeValue.CancelledDriverInitialization));
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            return Result(context, StorageProbeVerdict.Unavailable(StorageProfileProbeFailureStageValue.DriverInitialization, StorageProfileProbeFailureCodeValue.DriverProviderFailure, true));
        }

        if (resolution is not StorageRuntimeDriverResolution.Ready ready) return Result(context, StorageProbeVerdict.FromResolution(resolution));

        var probed = await StorageProbeRun.ExecuteAsync(ready.Lease, context.WriteAccessRequested, context.Initialize, cancellationToken).ConfigureAwait(false);

        return Result(context, probed);
    }

    /// <summary>
    /// What the probe is asking the profile FOR, which is exactly what it is about to verify.
    ///
    /// <para>Verifying a write asks whether NEW bytes will land — a lifecycle question, refused unless the profile is
    /// Active. Verifying no write asks only whether the destination still answers, which is the same question a read
    /// of already-stored bytes asks, so <see cref="StorageProfileRules.Admits"/> admits it through Disabled and through
    /// terminal Retired. Hardcoded Write, a probe of a non-Active profile never opened a driver at all: the answer it
    /// recorded restated <c>storage_profile.state</c> instead of observing the destination every one of that profile's
    /// stored objects still lives on.</para>
    /// </summary>
    private static StorageProfileEligibility Eligibility(StorageProfileProbeRequest request) =>
        request.VerifyWriteAccess ? StorageProfileEligibility.Write : StorageProfileEligibility.Read;

    /// <summary>
    /// Whether this probe may CREATE what is missing: you may only create a destination you are about to prove you
    /// can write to.
    ///
    /// <para>Provisioning is a write concern, so it is honoured only where the resolved eligibility is Write — which
    /// already implies an Active profile. That lifecycle gate used to enforce this by accident: with eligibility
    /// hardcoded Write, no non-Active profile ever reached a driver, so no read could provision one. Resolving a read
    /// to Read eligibility removes the accident, and without this clause it also reopens provisioning-by-probe — an
    /// operator's read-only Test of a Disabled destination would recreate a vanished root, which is exactly the
    /// liveness corroboration <c>ArtifactLocationVerifier</c> demotes every placement underneath on.</para>
    /// </summary>
    private static bool Provisions(StorageProfileProbeRequest request) => request.Initialize && Eligibility(request) == StorageProfileEligibility.Write;

    private static StorageProfileProbeResult Result(ProbeResultContext context, StorageProbeVerdict verdict) => new()
    {
        ProfileId = context.ProfileId,
        ProfileRevision = context.ProfileRevision,
        ProviderTypeKey = context.ProviderTypeKey,
        WriteAccessRequested = context.WriteAccessRequested,
        Status = verdict.Status,
        LatencyMilliseconds = Math.Max(0, context.Stopwatch.ElapsedMilliseconds),
        Failure = verdict.Failure,
    };

    private static bool IsRecoverable(Exception exception) => exception is not OutOfMemoryException and not AccessViolationException;

    private sealed record ProbeResultContext(Guid ProfileId, int? ProfileRevision, string? ProviderTypeKey, bool WriteAccessRequested, Stopwatch Stopwatch, bool Initialize = false);
}
