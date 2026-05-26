using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Settings.Database;
using Npgsql;

namespace CodeSpace.Core.Services.OAuth;

/// <summary>
/// Postgres advisory lock. Holds a dedicated connection for the duration of the lock — the
/// lock is session-scoped, so it auto-releases on connection close (covers process-crash
/// recovery). DisposeAsync issues an explicit pg_advisory_unlock so the connection returns
/// to the pool ready to be reused.
/// </summary>
public sealed class PostgresAdvisoryLock : ICrossProcessLock, IScopedDependency
{
    private readonly CodeSpaceConnectionString _connectionString;

    public PostgresAdvisoryLock(CodeSpaceConnectionString connectionString) { _connectionString = connectionString; }

    public async Task<IAsyncDisposable> AcquireAsync(long key, CancellationToken cancellationToken)
    {
        var conn = new NpgsqlConnection(_connectionString.Value);
        await conn.OpenAsync(cancellationToken).ConfigureAwait(false);

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
