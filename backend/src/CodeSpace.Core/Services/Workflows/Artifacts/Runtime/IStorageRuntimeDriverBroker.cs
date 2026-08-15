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
    private static readonly TimeSpan DisposeTimeout = TimeSpan.FromSeconds(10);
    private readonly object _gate = new();
    private readonly List<Task> _operations = [];
    private readonly List<IAsyncDisposable> _resources = [];
    private readonly Dictionary<Task, Func<Task>> _unresolvedOperations = [];
    private IArtifactStorageDriver? _driver;
    private Task? _disposeTask;
    private bool _disposeReturnsPromptly;

    internal StorageRuntimeDriverLease(IArtifactStorageDriver driver) => _driver = driver ?? throw new ArgumentNullException(nameof(driver));

    [JsonIgnore]
    public IArtifactStorageDriver Driver
    {
        get
        {
            lock (_gate) return _driver ?? throw new ObjectDisposedException(nameof(StorageRuntimeDriverLease));
        }
    }

    internal int TrackedOperationCount
    {
        get
        {
            lock (_gate) return _operations.Count;
        }
    }

    public ValueTask DisposeAsync() => DisposeAsync(waitForOperations: false);

    internal ValueTask DisposeWhenDrainedAsync() => DisposeAsync(waitForOperations: true);

    private ValueTask DisposeAsync(bool waitForOperations)
    {
        TaskCompletionSource? completion = null;
        Task[] operations = [];
        Func<Task>[] lateCleanupFactories = [];
        IAsyncDisposable[] resources = [];
        IArtifactStorageDriver? driver = null;
        Task disposal;
        bool returnsPromptly;
        lock (_gate)
        {
            if (_disposeTask != null)
            {
                disposal = _disposeTask;
                returnsPromptly = _disposeReturnsPromptly;
            }
            else
            {
                driver = _driver;
                _driver = null;
                if (driver == null) return ValueTask.CompletedTask;
                operations = _operations.ToArray();
                lateCleanupFactories = _unresolvedOperations.Values.ToArray();
                resources = _resources.ToArray();
                _operations.Clear();
                _unresolvedOperations.Clear();
                _resources.Clear();
                completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _disposeTask = completion.Task;
                _disposeReturnsPromptly = operations.Any(operation => !operation.IsCompleted) || lateCleanupFactories.Length > 0;
                disposal = _disposeTask;
                returnsPromptly = _disposeReturnsPromptly;
            }
        }
        if (completion != null) _ = CompleteDisposalAsync(completion, operations, lateCleanupFactories, resources, driver!);
        if (returnsPromptly) _ = ObserveBackgroundCleanupAsync(disposal);
        return waitForOperations || !returnsPromptly ? new ValueTask(disposal) : ValueTask.CompletedTask;
    }

    internal StorageRuntimeDriverOperation BeginOperation()
    {
        lock (_gate)
        {
            if (_disposeTask != null) throw new ObjectDisposedException(nameof(StorageRuntimeDriverLease));
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _operations.Add(completion.Task);
            return new StorageRuntimeDriverOperation(this, completion);
        }
    }

    internal Task<T> Track<T>(Task<T> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        lock (_gate)
        {
            if (_disposeTask != null) throw new ObjectDisposedException(nameof(StorageRuntimeDriverLease));
            _operations.Add(operation);
            _unresolvedOperations.Add(operation, () => CleanupAbandonedAsync(operation));
        }
        return operation;
    }

    internal void Release<T>(Task<T> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        lock (_gate)
        {
            _unresolvedOperations.Remove(operation);
            _operations.Remove(operation);
        }
    }

    internal void Abandon<T>(Task<T> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        lock (_gate)
        {
            // Dispose consumes every unresolved cleanup factory while holding this same gate. If it won the race,
            // the late result is already chained ahead of driver cleanup; otherwise Abandon adds that chain here.
            if (_unresolvedOperations.Remove(operation, out var cleanupFactory)) _operations.Add(cleanupFactory());
        }
    }

    internal void Own(IAsyncDisposable resource)
    {
        ArgumentNullException.ThrowIfNull(resource);
        lock (_gate)
        {
            if (_disposeTask != null) throw new ObjectDisposedException(nameof(StorageRuntimeDriverLease));
            _resources.Add(resource);
        }
    }

    private void CompleteOperation(TaskCompletionSource completion)
    {
        lock (_gate)
        {
            completion.TrySetResult();
            _operations.Remove(completion.Task);
        }
    }

    private static async Task DisposeAfterOperationsAsync(Task[] operations, IAsyncDisposable[] resources, IArtifactStorageDriver driver)
    {
        try { await Task.WhenAll(operations).ConfigureAwait(false); }
        catch { /* Provider outcome was already typed; exception text may contain secrets. */ }
        foreach (var resource in resources)
        {
            try { await resource.DisposeAsync().ConfigureAwait(false); }
            catch { /* Resource cleanup cannot revoke a durable outcome or expose provider text. */ }
        }
        await DisposeDriverAsync(driver).ConfigureAwait(false);
    }

    private static async Task CompleteDisposalAsync(TaskCompletionSource completion, Task[] operations, Func<Task>[] lateCleanupFactories, IAsyncDisposable[] resources, IArtifactStorageDriver driver)
    {
        try
        {
            var lateCleanups = lateCleanupFactories.Select(factory => factory()).ToArray();
            await DisposeAfterOperationsAsync(operations.Concat(lateCleanups).ToArray(), resources, driver).ConfigureAwait(false);
            completion.TrySetResult();
        }
        catch (Exception exception) { completion.TrySetException(exception); }
    }

    private static async Task CleanupAbandonedAsync<T>(Task<T> operation)
    {
        // Cleanup factories may be materialized while holding the lease gate. Always yield before provider-owned
        // disposal so arbitrary plugin code cannot run under that lock.
        await Task.Yield();
        try
        {
            var result = await operation.ConfigureAwait(false);
            if (result is ArtifactStorageReadResult { Content: { } content })
                await content.DisposeAsync().ConfigureAwait(false);
            else if (result is IAsyncDisposable disposable)
                await disposable.DisposeAsync().ConfigureAwait(false);
        }
        catch { /* Observe late faults without logging provider or secret material. */ }
    }

    private static async Task ObserveBackgroundCleanupAsync(Task cleanup)
    {
        try { await cleanup.ConfigureAwait(false); }
        catch { /* Timeout paths cannot surface late cleanup detail or dispose an in-flight driver concurrently. */ }
    }

    internal static Task DisposeDriverAsync(IArtifactStorageDriver driver) => driver.DisposeAsync().AsTask().WaitAsync(DisposeTimeout);

    public void OnSerializing() => throw new NotSupportedException("Runtime storage driver leases cannot be serialized.");
    public override string ToString()
    {
        lock (_gate) return $"StorageRuntimeDriverLease {{ State = {(_driver == null ? "Disposed" : "Active")} }}";
    }

    internal sealed class StorageRuntimeDriverOperation : IDisposable
    {
        private readonly StorageRuntimeDriverLease _owner;
        private readonly TaskCompletionSource _completion;
        private int _completed;

        internal StorageRuntimeDriverOperation(StorageRuntimeDriverLease owner, TaskCompletionSource completion)
        {
            _owner = owner;
            _completion = completion;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _completed, 1) == 0) _owner.CompleteOperation(_completion);
        }
    }
}
