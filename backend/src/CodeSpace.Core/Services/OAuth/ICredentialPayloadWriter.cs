using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Messages.Credentials;

namespace CodeSpace.Core.Services.OAuth;

/// <summary>
/// Persists an updated <see cref="CredentialPayload"/> back to the database. Used by token
/// refresh: when an OAuth strategy gets a new access/refresh token pair, it MUST be saved
/// even if the surrounding command's transaction rolls back — the provider invalidates the
/// old refresh token once used, so losing the new tokens on rollback locks the credential
/// out permanently. The implementation therefore uses a detached database connection.
/// </summary>
public interface ICredentialPayloadWriter
{
    Task UpdatePayloadAsync(Credential credential, CredentialPayload newPayload, CancellationToken cancellationToken);
}
