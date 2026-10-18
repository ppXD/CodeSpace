using CodeSpace.Messages.Credentials;

namespace CodeSpace.Core.Services.Credentials;

/// <summary>
/// Writes a re-authorization into the credential row — the payload and everything that says the credential is alive again —
/// in one compare-and-swap statement on a connection of its own, like <c>ICredentialPayloadWriter</c> does for a refresh and
/// for the same reason: the new token comes from a one-time authorization code the provider has already spent, so it must
/// not be lost to a rollback of whatever unit of work the caller is in.
/// </summary>
public interface ICredentialReauthorizationWriter
{
    /// <summary>
    /// Replaces the row's payload, auth type, scopes and expiry, sets it Active with its last error cleared and
    /// <c>LastValidatedDate</c> now, and stamps the modifier — but only while the row is live and still holds
    /// <see cref="CredentialReplacement.ExpectedEncryptedPayload"/>. Returns whether it did. False means the row changed
    /// under the caller (a Disconnect blanked it; a retirement soft-deleted it), and nothing was written.
    /// </summary>
    Task<bool> TryReplaceAsync(CredentialReplacement replacement, CancellationToken cancellationToken);
}
