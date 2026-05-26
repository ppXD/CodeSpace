using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Credentials;
using CodeSpace.Core.Settings.Database;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Credentials;
using Npgsql;

namespace CodeSpace.Core.Services.OAuth;

public sealed class CredentialPayloadWriter : ICredentialPayloadWriter, IScopedDependency
{
    private readonly IPayloadEncryptor _encryptor;
    private readonly ICredentialPayloadSerializer _serializer;
    private readonly CodeSpaceConnectionString _connectionString;

    public CredentialPayloadWriter(IPayloadEncryptor encryptor, ICredentialPayloadSerializer serializer, CodeSpaceConnectionString connectionString)
    {
        _encryptor = encryptor;
        _serializer = serializer;
        _connectionString = connectionString;
    }

    public async Task UpdatePayloadAsync(Credential credential, CredentialPayload newPayload, CancellationToken cancellationToken)
    {
        var json = _serializer.Serialize(newPayload);
        var encrypted = _encryptor.Encrypt(json);
        DateTimeOffset? expiresDate = newPayload is OAuthPayload oauth ? oauth.ExpiresAt : credential.ExpiresDate;
        var now = DateTimeOffset.UtcNow;

        // Detached Npgsql connection — must NOT join an outer EF transaction. Refresh-token
        // rotation invalidates the prior token on the provider side; if we let an outer
        // rollback discard the new token we'd be locked out.
        await using var conn = new NpgsqlConnection(_connectionString.Value);
        await conn.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE credential SET encrypted_payload = @enc, expires_date = @exp, last_modified_date = @now, last_modified_by = @user WHERE id = @id";
        cmd.Parameters.AddWithValue("@enc", encrypted);
        cmd.Parameters.AddWithValue("@exp", (object?)expiresDate ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@now", now);
        cmd.Parameters.AddWithValue("@user", SystemUsers.SeederId);
        cmd.Parameters.AddWithValue("@id", credential.Id);

        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        // Mutate caller's tracked entity so any subsequent read in the same scope reflects
        // the new token. EF doesn't see this as a change; the row in the DB is already updated.
        credential.EncryptedPayload = encrypted;
        credential.ExpiresDate = expiresDate;
        credential.LastModifiedDate = now;
        credential.LastModifiedBy = SystemUsers.SeederId;
    }
}
