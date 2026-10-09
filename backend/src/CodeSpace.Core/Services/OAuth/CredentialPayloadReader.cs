using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Settings.Database;
using Npgsql;

namespace CodeSpace.Core.Services.OAuth;

public sealed class CredentialPayloadReader : ICredentialPayloadReader, IScopedDependency
{
    private readonly CodeSpaceConnectionString _connectionString;

    public CredentialPayloadReader(CodeSpaceConnectionString connectionString) { _connectionString = connectionString; }

    public async Task<string> ReadEncryptedPayloadAsync(Guid credentialId, CancellationToken cancellationToken)
    {
        await using var conn = new NpgsqlConnection(_connectionString.Value);
        await conn.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT encrypted_payload FROM credential WHERE id = @id";
        cmd.Parameters.AddWithValue("@id", credentialId);

        return await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string
            ?? throw new InvalidOperationException($"Credential {credentialId} no longer exists");
    }
}
