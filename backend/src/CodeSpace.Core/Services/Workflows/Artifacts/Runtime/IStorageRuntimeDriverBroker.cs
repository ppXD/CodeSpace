using System.Text.Json.Serialization;
using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Services.Workflows.Artifacts.Providers;

namespace CodeSpace.Core.Services.Workflows.Artifacts.Runtime;

/// <summary>
/// Request-scoped activation boundary for one exact team/profile/revision pin. It resolves immutable control-plane
/// state into a short-lived driver lease without exposing credential material or changing any existing artifact path.
/// </summary>
public interface IStorageRuntimeDriverBroker : IScopedDependency
{
    ValueTask<StorageRuntimeDriverResolution> OpenAsync(StorageRuntimeDriverRequest request, CancellationToken cancellationToken);
}

public sealed record StorageRuntimeDriverRequest(Guid TeamId, Guid ProfileId, int ProfileRevision);

/// <summary>Closed, secret-free runtime activation result.</summary>
public abstract record StorageRuntimeDriverResolution
{
    private StorageRuntimeDriverResolution() { }

    public sealed record Ready : StorageRuntimeDriverResolution
    {
        public Ready(StorageRuntimeDriverLease lease) => Lease = lease ?? throw new ArgumentNullException(nameof(lease));
        [JsonIgnore]
        public StorageRuntimeDriverLease Lease { get; }
    }

    public sealed record ProfileUnavailable(StorageRuntimeProfileFailureReason Reason) : StorageRuntimeDriverResolution;
    public sealed record CredentialUnavailable(StorageRuntimeCredentialFailureReason Reason) : StorageRuntimeDriverResolution;
    public sealed record ProviderUnavailable(StorageRuntimeProviderFailureReason Reason) : StorageRuntimeDriverResolution;
    public sealed record ConfigurationInvalid(StorageRuntimeConfigurationFailureReason Reason) : StorageRuntimeDriverResolution;
    public sealed record Cancelled(StorageRuntimeCancellationStage Stage) : StorageRuntimeDriverResolution;
    public sealed record DriverInitializationFailed(StorageRuntimeDriverInitializationFailureReason Reason) : StorageRuntimeDriverResolution;
}

public enum StorageRuntimeProfileFailureReason
{
    Missing,
    NotActive,
    RevisionMissing,
    ResolutionFailed,
}

public enum StorageRuntimeCredentialFailureReason
{
    Missing,
    NotActive,
    RevisionMissing,
    ProviderMismatch,
    ProviderUnavailable,
    InvalidEnvelope,
    InvalidReference,
    InvalidSecret,
    ResolutionFailed,
}

public enum StorageRuntimeProviderFailureReason
{
    ModuleMissing,
    FactoryMissing,
    FactoryMismatch,
    CatalogFailure,
}

public enum StorageRuntimeConfigurationFailureReason
{
    InvalidConfiguration,
    UnsupportedSchemaVersion,
    SnapshotIdentityMismatch,
    InvalidProviderTypeKey,
    FactoryRejectedConfiguration,
}

public enum StorageRuntimeCancellationStage
{
    ProfileResolution,
    CredentialResolution,
    DriverInitialization,
}

public enum StorageRuntimeDriverInitializationFailureReason
{
    NullDriver,
    ProviderCanceled,
    ProviderFailure,
    CleanupFailure,
}

/// <summary>
/// Non-serializable ownership boundary for an activated driver. The caller owns the lease and must dispose it exactly
/// once (repeated disposal is harmless); no operation may retain or use <see cref="Driver"/> after disposal.
/// </summary>
public sealed class StorageRuntimeDriverLease : IAsyncDisposable, IJsonOnSerializing
{
    private readonly object _gate = new();
    private IArtifactStorageDriver? _driver;
    private Task? _disposeTask;

    internal StorageRuntimeDriverLease(IArtifactStorageDriver driver) => _driver = driver ?? throw new ArgumentNullException(nameof(driver));

    [JsonIgnore]
    public IArtifactStorageDriver Driver
    {
        get
        {
            lock (_gate) return _driver ?? throw new ObjectDisposedException(nameof(StorageRuntimeDriverLease));
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposeTask != null) return new ValueTask(_disposeTask);
            var driver = _driver;
            _driver = null;
            if (driver == null) return ValueTask.CompletedTask;
            try { _disposeTask = driver.DisposeAsync().AsTask(); }
            catch (Exception exception) { _disposeTask = Task.FromException(exception); }
            return new ValueTask(_disposeTask);
        }
    }

    public void OnSerializing() => throw new NotSupportedException("Runtime storage driver leases cannot be serialized.");
    public override string ToString()
    {
        lock (_gate) return $"StorageRuntimeDriverLease {{ State = {(_driver == null ? "Disposed" : "Active")} }}";
    }
}
