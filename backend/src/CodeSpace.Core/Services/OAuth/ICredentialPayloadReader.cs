namespace CodeSpace.Core.Services.OAuth;

/// <summary>
/// Reads a credential's encrypted payload as the database holds it now. Token refresh reads through this once it holds
/// the refresh lock: the caller's credential entity was loaded before the lock, so after another run or pod rotated the
/// token it still carries the consumed refresh token. Like <see cref="ICredentialPayloadWriter"/>, the implementation
/// uses a detached database connection, so the read sees the latest committed row whatever transaction or DbContext
/// the caller is in.
/// </summary>
public interface ICredentialPayloadReader
{
    Task<string> ReadEncryptedPayloadAsync(Guid credentialId, CancellationToken cancellationToken);

    /// <summary>
    /// True once the credential is soft-deleted — a retirement: a connection delete, a member's departure or a relink,
    /// each of which blanks the token and takes the row out of the team's list — or gone. False while the row is listed,
    /// a Disconnected one included: a Disconnect blanks the token and leaves the row Revoked in the list.
    /// </summary>
    Task<bool> IsRetiredAsync(Guid credentialId, CancellationToken cancellationToken);
}
