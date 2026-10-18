using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Settings.Database;
using CodeSpace.Messages.Credentials;
using Npgsql;
using NpgsqlTypes;

namespace CodeSpace.Core.Services.Credentials;

public sealed class CredentialReauthorizationWriter : ICredentialReauthorizationWriter, IScopedDependency
{
    private const string Sql = @"UPDATE credential
SET encrypted_payload = @enc, auth_type = @authType, scopes = @scopes, expires_date = @exp,
    display_name = COALESCE(@name, display_name),
    status = 'Active', last_error = NULL, last_validated_date = @now,
    last_modified_date = @now, last_modified_by = @user
WHERE id = @id AND deleted_date IS NULL AND encrypted_payload = @expected";

    private readonly CodeSpaceConnectionString _connectionString;

    public CredentialReauthorizationWriter(CodeSpaceConnectionString connectionString) { _connectionString = connectionString; }

    public async Task<bool> TryReplaceAsync(CredentialReplacement replacement, CancellationToken cancellationToken)
    {
        // Detached Npgsql connection — must NOT join an outer EF transaction. The new token is the only copy of an
        // authorization the provider will not issue again from the same code; if an outer rollback discarded it the person
        // would have to authorize again.
        await using var conn = new NpgsqlConnection(_connectionString.Value);
        await conn.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = Sql;
        cmd.Parameters.AddWithValue("@enc", replacement.EncryptedPayload);
        cmd.Parameters.AddWithValue("@authType", replacement.AuthType.ToString());
        cmd.Parameters.Add(new NpgsqlParameter("@scopes", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = replacement.Scopes?.ToArray() ?? (object)DBNull.Value });
        cmd.Parameters.Add(new NpgsqlParameter("@exp", NpgsqlDbType.TimestampTz) { Value = (object?)replacement.ExpiresDate?.ToUniversalTime() ?? DBNull.Value });
        cmd.Parameters.Add(new NpgsqlParameter("@name", NpgsqlDbType.Text) { Value = (object?)replacement.DisplayName ?? DBNull.Value });
        cmd.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow);
        cmd.Parameters.AddWithValue("@user", replacement.ModifiedBy);
        cmd.Parameters.AddWithValue("@id", replacement.CredentialId);
        cmd.Parameters.AddWithValue("@expected", replacement.ExpectedEncryptedPayload);

        return await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }
}
