namespace CodeSpace.Core.Services.Credentials;

/// <summary>
/// Records what a provider said about a credential: it refused it (<c>Status=Error</c> and why), or accepted it (validated,
/// and any Error cleared). Like <see cref="OAuth.ICredentialPayloadWriter"/> every write goes straight to the row on a
/// connection of its own, never inside the caller's transaction: a verdict is a fact about the provider, and a caller that
/// rolls back must not unsay it. Every write is conditional on the row still holding the payload the caller judged — a
/// disconnect, a reconnect or a re-authorization that replaced it meanwhile is never overwritten by a verdict about the old
/// one — and returns whether it landed.
/// </summary>
public interface ICredentialHealthWriter
{
    /// <summary>Marks the credential <c>Error</c> with <paramref name="lastError"/>, if it is still Active or already Error and still holds <paramref name="expectedEncryptedPayload"/>. A repeat keeps the first reason and refreshes the date, which is when the verdict was last confirmed.</summary>
    Task<bool> MarkRejectedAsync(Guid credentialId, string expectedEncryptedPayload, string lastError, CancellationToken cancellationToken);

    /// <summary>Records <paramref name="validatedAt"/> and, when asked, the token's real expiry; clears <c>Error</c> and its text. Under the same conditions as <see cref="MarkRejectedAsync"/>.</summary>
    Task<bool> MarkValidatedAsync(Guid credentialId, string expectedEncryptedPayload, DateTimeOffset validatedAt, ExpiryRecord? expiry, CancellationToken cancellationToken);
}

/// <summary>The expiry a validation records for a static token: null means the provider says it never expires. Absent (a null <see cref="ExpiryRecord"/>) leaves the column alone, as for an OAuth credential, whose expiry is its access token's and is the refresher's to write.</summary>
public sealed record ExpiryRecord(DateTimeOffset? ExpiresAt);
