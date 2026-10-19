namespace CodeSpace.Core.Services.OAuth;

/// <summary>
/// A sibling of <see cref="ICrossProcessLock"/> for work that must not run twice at once and should simply be skipped, not
/// queued, when something else is already doing it: a recurring sweep whose previous pass is still going, or a credential a
/// write of its token holds (<c>CredentialRefreshLock.TryAcquireAsync</c>). Same key space as <see cref="ICrossProcessLock"/>.
/// </summary>
public interface ICrossProcessTryLock
{
    /// <summary>
    /// Takes the lock if no one holds it and returns a handle whose disposal releases it. Returns null at once, without
    /// waiting, when another holder has it.
    /// </summary>
    Task<IAsyncDisposable?> TryAcquireAsync(long key, CancellationToken cancellationToken);
}
