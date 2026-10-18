// PINNED COPY of CredentialPayloadWriter as built at 203682baf — the build before the change that keeps a rotated OAuth pair. It exists so a
// test can run a pod "on the previous build" next to one on this build, as a rolling deploy or a rollback does. Only
// the names, the namespace and the DI marker differ from the original. It is a historical artifact, not a mirror of
// production: it never tracks the current source, and it is deleted once the build it stands for is no longer
// deployable alongside this one.
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.OAuth;
using CodeSpace.Core.Services.Credentials;
using CodeSpace.Core.Settings.Database;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Credentials;
using Npgsql;

namespace CodeSpace.StorageTestWorker.PreviousBuild;

public sealed class PreviousBuildCredentialPayloadWriter : ICredentialPayloadWriter
{
    private readonly IPayloadEncryptor _encryptor;
    private readonly ICredentialPayloadSerializer _serializer;
    private readonly CodeSpaceConnectionString _connectionString;

    public PreviousBuildCredentialPayloadWriter(IPayloadEncryptor encryptor, ICredentialPayloadSerializer serializer, CodeSpaceConnectionString connectionString)
    {
        _encryptor = encryptor;
        _serializer = serializer;
        _connectionString = connectionString;
    }

    public async Task<bool> UpdatePayloadAsync(Credential credential, string expectedEncryptedPayload, CredentialPayload newPayload, CancellationToken cancellationToken)
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
        cmd.CommandText = "UPDATE credential SET encrypted_payload = @enc, expires_date = @exp, last_modified_date = @now, last_modified_by = @user WHERE id = @id AND encrypted_payload = @expected";
        cmd.Parameters.AddWithValue("@enc", encrypted);
        cmd.Parameters.AddWithValue("@exp", (object?)expiresDate ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@now", now);
        cmd.Parameters.AddWithValue("@user", SystemUsers.SeederId);
        cmd.Parameters.AddWithValue("@id", credential.Id);
        cmd.Parameters.AddWithValue("@expected", expectedEncryptedPayload);

        if (await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 0) return false;

        // Mutate caller's tracked entity so any subsequent read in the same scope reflects
        // the new token. EF doesn't see this as a change; the row in the DB is already updated.
        credential.EncryptedPayload = encrypted;
        credential.ExpiresDate = expiresDate;
        credential.LastModifiedDate = now;
        credential.LastModifiedBy = SystemUsers.SeederId;

        return true;
    }
}
