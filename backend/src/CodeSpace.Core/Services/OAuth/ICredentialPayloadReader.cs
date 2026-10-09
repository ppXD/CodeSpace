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
}
