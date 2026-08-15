using CodeSpace.Core.Services.Workflows.Artifacts.Providers;

namespace CodeSpace.Core.Services.Workflows.Artifacts.Runtime;

/// <summary>
/// Owns one ephemeral driver and observes every provider task. A timed-out provider that ignores cancellation is not
/// disposed concurrently with its still-running SDK call; disposal is deferred until all observed calls settle.
/// </summary>
internal sealed class ArtifactStorageDriverLease : IAsyncDisposable
{
    private static readonly TimeSpan DisposeTimeout = TimeSpan.FromSeconds(10);
    private readonly object _gate = new();
    private readonly List<Task> _operations = [];
    private readonly List<IAsyncDisposable> _resources = [];
    private int _disposeRequested;

    public ArtifactStorageDriverLease(IArtifactStorageDriver driver) => Driver = driver;

    public IArtifactStorageDriver Driver { get; }

    public Task<T> Track<T>(Task<T> operation)
    {
        lock (_gate)
        {
            if (Volatile.Read(ref _disposeRequested) != 0) throw new ObjectDisposedException(nameof(ArtifactStorageDriverLease));
            _operations.Add(operation);
        }
        return operation;
    }

    public void Abandon<T>(Task<T> operation)
    {
        var cleanup = CleanupAbandonedAsync(operation);
        lock (_gate)
        {
            // Abandon is called before the coordinator requests disposal. Keep the branch fail-safe for a race with
            // caller disposal: the cleanup task is self-rooted by its continuation even when it misses the snapshot.
            if (Volatile.Read(ref _disposeRequested) == 0) _operations.Add(cleanup);
        }
    }

    public void Own(IAsyncDisposable resource)
    {
        lock (_gate)
        {
            if (Volatile.Read(ref _disposeRequested) != 0) throw new ObjectDisposedException(nameof(ArtifactStorageDriverLease));
            _resources.Add(resource);
        }
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeRequested, 1) != 0) return ValueTask.CompletedTask;
        Task[] pending;
        IAsyncDisposable[] resources;
        lock (_gate)
        {
            pending = [.. _operations];
            resources = [.. _resources];
        }
        if (pending.Length == 0) return new ValueTask(DisposeOwnedAsync(resources, Driver));
        if (pending.All(operation => operation.IsCompleted))
            return new ValueTask(DisposeAfterOperationsAsync(pending, resources, Driver));

        _ = DisposeAfterOperationsAsync(pending, resources, Driver);
        return ValueTask.CompletedTask;
    }

    private static async Task DisposeAfterOperationsAsync(Task[] operations, IAsyncDisposable[] resources, IArtifactStorageDriver driver)
    {
        try { await Task.WhenAll(operations).ConfigureAwait(false); }
        catch { /* Provider outcome was already typed; exception text may contain secrets. */ }
        await DisposeOwnedAsync(resources, driver).ConfigureAwait(false);
    }

    private static async Task CleanupAbandonedAsync<T>(Task<T> operation)
    {
        try
        {
            var result = await operation.ConfigureAwait(false);
            if (result is ArtifactStorageReadResult { Content: { } content })
                await content.DisposeAsync().ConfigureAwait(false);
            else if (result is IAsyncDisposable disposable)
                await disposable.DisposeAsync().ConfigureAwait(false);
        }
        catch { /* Observe late faults without logging provider/secret material. */ }
    }

    private static async Task DisposeOwnedAsync(IAsyncDisposable[] resources, IArtifactStorageDriver driver)
    {
        foreach (var resource in resources)
        {
            try { await resource.DisposeAsync().ConfigureAwait(false); }
            catch { /* Cleanup cannot revoke a durable outcome; exception text may contain secrets. */ }
        }
        await DisposeDriverAsync(driver).ConfigureAwait(false);
    }

    public static async Task DisposeDriverAsync(IArtifactStorageDriver driver)
    {
        try { await driver.DisposeAsync().AsTask().WaitAsync(DisposeTimeout).ConfigureAwait(false); }
        catch { /* Cleanup cannot revoke a durable outcome; provider exception text may contain secrets. */ }
    }
}
