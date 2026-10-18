using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Settings.Database;
using Npgsql;

namespace CodeSpace.Core.Services.Health.Sessions;

/// <summary>
/// <see cref="IAdvisoryLockSessions"/> over the process's own connection string — the one <c>PostgresAdvisoryLock</c> opens
/// its connections from, so the test meets whatever sits between this process and Postgres exactly as token refresh does.
///
/// <para>Pooling is off for these connections. They are two distinct client connections for as long as they live, and
/// closing one closes the server session, which releases every lock it holds — a pooled connection would go back to the
/// pool still holding one until it was next reset.</para>
/// </summary>
public sealed class NpgsqlAdvisoryLockSessions : IAdvisoryLockSessions, ISingletonDependency
{
    private readonly CodeSpaceConnectionString _connectionString;

    public NpgsqlAdvisoryLockSessions(CodeSpaceConnectionString connectionString) { _connectionString = connectionString; }

    /// <summary>The connection string is read here, not when this is constructed: a string the driver cannot parse must surface as the self-test's "could not run", never as a failure to build the host.</summary>
    public async Task<IAdvisoryLockSession> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(_connectionString.Value) { Pooling = false }.ConnectionString);

        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        return new Session(connection);
    }

    private sealed class Session(NpgsqlConnection connection) : IAdvisoryLockSession
    {
        public async Task LockAsync(long key, CancellationToken cancellationToken) =>
            await ScalarAsync("SELECT pg_advisory_lock(@key)", key, cancellationToken).ConfigureAwait(false);

        public async Task<bool> TryLockAsync(long key, CancellationToken cancellationToken) =>
            await ScalarAsync("SELECT pg_try_advisory_lock(@key)", key, cancellationToken).ConfigureAwait(false) is true;

        public async Task<bool> UnlockAsync(long key, CancellationToken cancellationToken) =>
            await ScalarAsync("SELECT pg_advisory_unlock(@key)", key, cancellationToken).ConfigureAwait(false) is true;

        public ValueTask DisposeAsync() => connection.DisposeAsync();

        private async Task<object?> ScalarAsync(string sql, long key, CancellationToken cancellationToken)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.Parameters.AddWithValue("@key", key);

            return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}
