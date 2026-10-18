using System.Collections.Concurrent;

namespace CodeSpace.Core.Services.OAuth;

/// <summary>
/// The lock everything that rewrites one credential's token material takes, so two of them never write at once: a refresh
/// (<see cref="OAuthTokenRefresher"/>) and a re-authorization (<c>ICredentialReauthorizer</c>) are the same kind of act —
/// each replaces the stored pair, and each is only sound against the pair it read. Two tiers, always taken in this order:
///   1. a per-credential in-process <see cref="SemaphoreSlim"/> — cheap, and blocks every sibling in this process;
///   2. the <see cref="ICrossProcessLock"/> (a Postgres advisory lock) — blocks every other pod.
/// One definition, because the semaphores are process-wide state: a second dictionary beside this one would be a second lock
/// that excludes nobody.
/// </summary>
internal static class CredentialRefreshLock
{
    private static readonly ConcurrentDictionary<Guid, SemaphoreSlim> InProcess = new();

    /// <summary>
    /// Stable int64 derived from the credential GUID. pg_advisory_lock takes int8 (bigint).
    /// We take the first 8 bytes of the GUID — uniform distribution, collision probability
    /// is the same as a 64-bit random key. Same credential id always maps to same key.
    /// </summary>
    internal static long KeyOf(Guid credentialId) => BitConverter.ToInt64(credentialId.ToByteArray(), 0);

    /// <summary>Waits for both tiers. Disposing the handle releases the advisory lock first and the semaphore after it.</summary>
    internal static async Task<IAsyncDisposable> AcquireAsync(ICrossProcessLock crossProcess, Guid credentialId, CancellationToken cancellationToken)
    {
        var semaphore = InProcess.GetOrAdd(credentialId, _ => new SemaphoreSlim(1, 1));

        await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            return new Held(semaphore, await crossProcess.AcquireAsync(KeyOf(credentialId), cancellationToken).ConfigureAwait(false));
        }
        catch
        {
            semaphore.Release();
            throw;
        }
    }

    private sealed class Held : IAsyncDisposable
    {
        private readonly SemaphoreSlim _semaphore;
        private readonly IAsyncDisposable _crossProcess;
        private bool _released;

        public Held(SemaphoreSlim semaphore, IAsyncDisposable crossProcess)
        {
            _semaphore = semaphore;
            _crossProcess = crossProcess;
        }

        public async ValueTask DisposeAsync()
        {
            if (_released) return;
            _released = true;

            try
            {
                await _crossProcess.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                _semaphore.Release();
            }
        }
    }
}
