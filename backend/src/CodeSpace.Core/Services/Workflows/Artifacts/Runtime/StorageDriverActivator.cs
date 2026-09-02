using CodeSpace.Core.Services.Workflows.Artifacts.Providers;
using Microsoft.Extensions.Logging;

namespace CodeSpace.Core.Services.Workflows.Artifacts.Runtime;

public sealed class StorageDriverActivator : IStorageDriverActivator
{
    private readonly ILogger<StorageDriverActivator> _logger;

    public StorageDriverActivator(ILogger<StorageDriverActivator> logger) { _logger = logger; }

    public async ValueTask<StorageRuntimeDriverResolution> ActivateAsync(IArtifactStorageDriverFactory factory, StorageProfileSnapshot snapshot, StorageCredentialHandle? credential, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(snapshot);

        if (cancellationToken.IsCancellationRequested)
        {
            credential?.Dispose();
            return Cancelled();
        }

        var created = await CreateAsync(factory, snapshot, credential, cancellationToken).ConfigureAwait(false);

        return created.Failure ?? await LeaseAsync(created.Driver, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<DriverCreation> CreateAsync(IArtifactStorageDriverFactory factory, StorageProfileSnapshot snapshot, StorageCredentialHandle? credential, CancellationToken cancellationToken)
    {
        try
        {
            return new DriverCreation(await factory.CreateAsync(new ArtifactStorageDriverCreateRequest(snapshot) { CredentialHandle = credential }, cancellationToken).ConfigureAwait(false), null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new DriverCreation(null, Cancelled());
        }
        catch (OperationCanceledException)
        {
            return new DriverCreation(null, new StorageRuntimeDriverResolution.DriverInitializationFailed(StorageRuntimeDriverInitializationFailureReason.ProviderCanceled));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        {
            if (cancellationToken.IsCancellationRequested) return new DriverCreation(null, Cancelled());

            LogConfigurationRefusal(snapshot, exception);
            return new DriverCreation(null, new StorageRuntimeDriverResolution.ConfigurationInvalid(StorageRuntimeConfigurationFailureReason.FactoryRejectedConfiguration));
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            if (cancellationToken.IsCancellationRequested) return new DriverCreation(null, Cancelled());

            return new DriverCreation(null, new StorageRuntimeDriverResolution.DriverInitializationFailed(StorageRuntimeDriverInitializationFailureReason.ProviderFailure));
        }
        finally
        {
            credential?.Dispose();
        }
    }

    private static async ValueTask<StorageRuntimeDriverResolution> LeaseAsync(IArtifactStorageDriver? driver, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
            return driver == null ? Cancelled() : await DisposeCancelledDriverAsync(driver).ConfigureAwait(false);
        if (driver == null) return new StorageRuntimeDriverResolution.DriverInitializationFailed(StorageRuntimeDriverInitializationFailureReason.NullDriver);

        return new StorageRuntimeDriverResolution.Ready(new StorageRuntimeDriverLease(driver));
    }

    /// <summary>
    /// The provider's own refusal text is the only copy that says which field is wrong and what to put there, and the
    /// resolution this method returns deliberately carries none of it - provider text may quote a secret, so the caller
    /// gets a closed reason code instead. Writing it here is therefore what keeps the refusal readable at all; without
    /// this line the operator sees a reason code with no way back to the field that produced it.
    /// </summary>
    private void LogConfigurationRefusal(StorageProfileSnapshot snapshot, Exception exception) =>
        _logger.LogWarning(exception, "Storage provider {ProviderTypeKey} refused the configuration of storage profile {StorageProfileId} revision {StorageProfileRevision}.", snapshot.ProviderTypeKey, snapshot.ProfileId, snapshot.ProfileRevision);

    private static async ValueTask<StorageRuntimeDriverResolution> DisposeCancelledDriverAsync(IArtifactStorageDriver driver)
    {
        try
        {
            await StorageRuntimeDriverLease.DisposeDriverAsync(driver).ConfigureAwait(false);
            return Cancelled();
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            return new StorageRuntimeDriverResolution.DriverInitializationFailed(StorageRuntimeDriverInitializationFailureReason.CleanupFailure);
        }
    }

    private static StorageRuntimeDriverResolution Cancelled() => new StorageRuntimeDriverResolution.Cancelled(StorageRuntimeCancellationStage.DriverInitialization);

    private static bool IsRecoverable(Exception exception) => exception is not OutOfMemoryException and not AccessViolationException;

    private sealed record DriverCreation(IArtifactStorageDriver? Driver, StorageRuntimeDriverResolution? Failure);
}
