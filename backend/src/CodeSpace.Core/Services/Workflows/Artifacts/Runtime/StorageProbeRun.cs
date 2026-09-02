using CodeSpace.Core.Services.Workflows.Artifacts.Providers;
using CodeSpace.Messages.Dtos.Storage;

namespace CodeSpace.Core.Services.Workflows.Artifacts.Runtime;

/// <summary>
/// Asks an opened destination the one question a probe exists to ask, and gives the lease back whatever the answer
/// was.
///
/// <para>Shared by both probe entry points because the observation is identical once a driver is open - a saved
/// profile revision and configuration an operator has not saved yet are the same destination once addressed. The
/// cleanup rule is the part worth sharing: a lease that will not dispose OUTRANKS a healthy answer, because a driver
/// still holding a connection is a fault the operator has to see even though the bucket answered.</para>
/// </summary>
internal static class StorageProbeRun
{
    internal static async Task<StorageProbeVerdict> ExecuteAsync(StorageRuntimeDriverLease lease, bool verifyWriteAccess, bool initialize, CancellationToken cancellationToken)
    {
        StorageProbeVerdict? observed = null;
        StorageProbeVerdict? cleanupFailure = null;
        try
        {
            observed = await ObserveAsync(lease, verifyWriteAccess, initialize, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            try
            {
                await lease.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception exception) when (IsRecoverable(exception))
            {
                cleanupFailure = StorageProbeVerdict.Unavailable(StorageProfileProbeFailureStageValue.DriverCleanup, StorageProfileProbeFailureCodeValue.DriverCleanupFailure, true);
            }
        }

        return cleanupFailure ?? observed ?? StorageProbeVerdict.Unavailable(StorageProfileProbeFailureStageValue.Probe, StorageProfileProbeFailureCodeValue.ProbeProviderFailure, true);
    }

    private static async Task<StorageProbeVerdict> ObserveAsync(StorageRuntimeDriverLease lease, bool verifyWriteAccess, bool initialize, CancellationToken cancellationToken)
    {
        try
        {
            var probe = await lease.Driver.ProbeAsync(new ArtifactStorageProbeRequest { VerifyWriteAccess = verifyWriteAccess, Initialize = initialize }, cancellationToken).ConfigureAwait(false);
            return StorageProbeVerdict.FromProbe(probe);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return StorageProbeVerdict.Cancelled(StorageProfileProbeFailureCodeValue.CancelledProbe);
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            return StorageProbeVerdict.Unavailable(StorageProfileProbeFailureStageValue.Probe, StorageProfileProbeFailureCodeValue.ProbeProviderFailure, true);
        }
    }

    private static bool IsRecoverable(Exception exception) => exception is not OutOfMemoryException and not AccessViolationException;
}
