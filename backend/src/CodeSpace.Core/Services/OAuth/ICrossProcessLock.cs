namespace CodeSpace.Core.Services.OAuth;

/// <summary>
/// Lock that coordinates across multiple API instances (typical implementation:
/// pg_advisory_lock). Used to serialize OAuth refresh per credential so two instances don't
/// both call the provider with the same refresh_token (token rotation would invalidate the
/// loser's response). Unit tests substitute a no-op implementation.
/// </summary>
public interface ICrossProcessLock
{
    /// <summary>
    /// Acquires the lock, waiting if necessary. Returns a handle whose disposal releases
    /// the lock. The key namespace is shared across the whole database — callers should
    /// derive a stable, well-distributed int64 from the entity id they're locking.
    /// </summary>
    Task<IAsyncDisposable> AcquireAsync(long key, CancellationToken cancellationToken);
}
