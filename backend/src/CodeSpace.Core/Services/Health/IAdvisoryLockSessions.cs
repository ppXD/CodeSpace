namespace CodeSpace.Core.Services.Health;

/// <summary>
/// Opens independent Postgres sessions for the refresh-lock self-test, the way <c>PostgresAdvisoryLock</c> opens its own:
/// one connection per session, from the process's connection string. Narrow on purpose — the test needs to take a key, try
/// it from another session, and let go, and nothing else.
/// </summary>
public interface IAdvisoryLockSessions
{
    Task<IAdvisoryLockSession> OpenAsync(CancellationToken cancellationToken);
}

/// <summary>One Postgres session. Advisory locks belong to a server session, which is what makes them re-entrant for it and exclusive against any other.</summary>
public interface IAdvisoryLockSession : IAsyncDisposable
{
    /// <summary><c>pg_advisory_lock</c>: blocks until this session holds <paramref name="key"/>. On a key nobody holds, it returns at once.</summary>
    Task LockAsync(long key, CancellationToken cancellationToken);

    /// <summary><c>pg_try_advisory_lock</c>: true when this session now holds <paramref name="key"/> — including when it already did, since a session may take its own lock again; false when another session holds it.</summary>
    Task<bool> TryLockAsync(long key, CancellationToken cancellationToken);

    /// <summary>
    /// <c>pg_advisory_unlock</c> for one hold of <paramref name="key"/>: true when this session held it and has let one hold go,
    /// false when it did not hold it. Straight to a server, a session that took the key and then unlocks it always gets true; a
    /// false is the server saying the statement ran on a session that never took it — the signature of a pooler that serves one
    /// connection's statements from different server sessions.
    /// </summary>
    Task<bool> UnlockAsync(long key, CancellationToken cancellationToken);
}
