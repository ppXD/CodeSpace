using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Settings.Database;
using Npgsql;

namespace CodeSpace.Core.Services.OAuth;

/// <summary>
/// Postgres advisory lock — waited for (<see cref="ICrossProcessLock"/>) or tried and skipped
/// (<see cref="ICrossProcessTryLock"/>). Holds a dedicated connection for the duration of the lock — the
/// lock is session-scoped, so it auto-releases when the session ends (covers process-crash
/// recovery). DisposeAsync issues an explicit pg_advisory_unlock and then closes the connection.
///
/// <para>That connection is never pooled. Disposing a pooled connection only hands it back to the pool, still holding
/// any lock it holds, and a lock acquired the instant its caller was cancelled — the webhook path gives up at five
/// seconds — would sit on an idle pooled connection until the pool pruned it, queueing every later caller for that key
/// behind a handle nobody owns. A connection of its own makes closing it what ends the session, so the server releases
/// the lock however the acquire or the unlock went. Multiplexing, which requires pooling, is turned off with it: the
/// configured string is otherwise used as it is.</para>
/// </summary>
public sealed class PostgresAdvisoryLock : ICrossProcessLock, ICrossProcessTryLock, IScopedDependency
{
    private readonly CodeSpaceConnectionString _connectionString;

    public PostgresAdvisoryLock(CodeSpaceConnectionString connectionString) { _connectionString = connectionString; }

    public async Task<IAsyncDisposable> AcquireAsync(long key, CancellationToken cancellationToken)
    {
        var conn = await OpenSessionAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT pg_advisory_lock(@key)";
            cmd.Parameters.AddWithValue("@key", key);
            await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await conn.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        return new Handle(conn, key);
    }

    public async Task<IAsyncDisposable?> TryAcquireAsync(long key, CancellationToken cancellationToken)
    {
        var conn = await OpenSessionAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT pg_try_advisory_lock(@key)";
            cmd.Parameters.AddWithValue("@key", key);

            if (await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is true) return new Handle(conn, key);
        }
        catch
        {
            await conn.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        await conn.DisposeAsync().ConfigureAwait(false);

        return null;
    }

    private async Task<NpgsqlConnection> OpenSessionAsync(CancellationToken cancellationToken)
    {
        var conn = new NpgsqlConnection(SessionConnectionString());

        await conn.OpenAsync(cancellationToken).ConfigureAwait(false);

        return conn;
    }

    /// <summary>Multiplexing needs the pool this session must not use, so a deployed string that sets it would otherwise fail every acquire.</summary>
    private string SessionConnectionString() => new NpgsqlConnectionStringBuilder(_connectionString.Value) { Pooling = false, Multiplexing = false }.ConnectionString;

    private sealed class Handle : IAsyncDisposable
    {
        private readonly NpgsqlConnection _conn;
        private readonly long _key;
        private bool _disposed;

        public Handle(NpgsqlConnection conn, long key) { _conn = conn; _key = key; }

        public async ValueTask DisposeAsync()
        {
            if (_disposed) return;
            _disposed = true;

            try
            {
                if (_conn.State == System.Data.ConnectionState.Open)
                {
                    await using var cmd = _conn.CreateCommand();
                    cmd.CommandText = "SELECT pg_advisory_unlock(@key)";
                    cmd.Parameters.AddWithValue("@key", _key);
                    await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
                }
            }
            catch
            {
                // Closing the connection releases the lock anyway — swallow.
            }
            finally
            {
                await _conn.DisposeAsync().ConfigureAwait(false);
            }
        }
    }
}
