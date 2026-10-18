using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Settings.Database;
using CodeSpace.Messages.Constants;
using Npgsql;

namespace CodeSpace.Core.Services.Credentials;

public sealed class CredentialHealthWriter : ICredentialHealthWriter, IScopedDependency
{
    private readonly CodeSpaceConnectionString _connectionString;

    public CredentialHealthWriter(CodeSpaceConnectionString connectionString) { _connectionString = connectionString; }

    public async Task<bool> MarkRejectedAsync(Guid credentialId, string expectedEncryptedPayload, string lastError, CancellationToken cancellationToken)
    {
        // The first reason stays: it is the most specific one (what the refresh was refused with), and a later confirmation of
        // the same verdict only has the date to add.
        const string sql = "UPDATE credential SET status = 'Error', last_error = CASE WHEN status = 'Error' AND last_error IS NOT NULL THEN last_error ELSE @error END, last_modified_date = @now, last_modified_by = @user " + Guard;

        return await ExecuteAsync(sql, credentialId, expectedEncryptedPayload, cmd =>
        {
            cmd.Parameters.AddWithValue("@error", lastError);
            cmd.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow);
            cmd.Parameters.AddWithValue("@user", SystemUsers.SeederId);
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> MarkValidatedAsync(Guid credentialId, string expectedEncryptedPayload, DateTimeOffset validatedAt, ExpiryRecord? expiry, CancellationToken cancellationToken)
    {
        // Setting an Error credential back to Active is a change of the row, so it bumps last_modified_date (the clock a
        // recheck of an Error credential is measured from); confirming one that was fine already is not.
        const string sql = "UPDATE credential SET last_modified_date = CASE WHEN status = 'Error' THEN @now ELSE last_modified_date END, status = 'Active', last_error = NULL, last_validated_date = @validated, expires_date = CASE WHEN @recordsExpiry THEN @expires ELSE expires_date END " + Guard;

        return await ExecuteAsync(sql, credentialId, expectedEncryptedPayload, cmd =>
        {
            cmd.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow);
            cmd.Parameters.AddWithValue("@validated", validatedAt.ToUniversalTime());
            cmd.Parameters.AddWithValue("@recordsExpiry", expiry is not null);
            cmd.Parameters.AddWithValue("@expires", (object?)expiry?.ExpiresAt?.ToUniversalTime() ?? DBNull.Value);
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Only a live row that is still in service (Active, or Error awaiting a verdict) and still holds the payload the caller judged: a disconnected or retired row, and one a reconnect replaced, is not this verdict's to change.</summary>
    private const string Guard = "WHERE id = @id AND encrypted_payload = @expected AND status IN ('Active', 'Error') AND deleted_date IS NULL";

    private async Task<bool> ExecuteAsync(string sql, Guid credentialId, string expectedEncryptedPayload, Action<NpgsqlCommand> parameters, CancellationToken cancellationToken)
    {
        // Detached connection: a verdict must not join an outer EF transaction that may roll back (see ICredentialHealthWriter).
        await using var conn = new NpgsqlConnection(_connectionString.Value);
        await conn.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("@id", credentialId);
        cmd.Parameters.AddWithValue("@expected", expectedEncryptedPayload);
        parameters(cmd);

        return await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) > 0;
    }
}
