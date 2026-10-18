using CodeSpace.Messages.Credentials;

namespace CodeSpace.Core.Services.Credentials;

/// <summary>
/// Puts a new authorization into a credential row that already exists. Everything that references a credential —
/// <c>repository.credential_id</c>, <c>connection_webhook.credential_id</c>, <c>user_provider_identity.credential_id</c> —
/// references its id, and a reconnect used to mint a new row, so a whole succession subsystem had to move those bindings
/// afterwards, with edge cases for every shape of owner, instance and timing. Writing the new token into the SAME row removes
/// the cause: nothing moves, so nothing can be left behind.
///
/// <para>The row is rewritten under the lock a refresh takes, so a refresher cannot rotate the pair being replaced while it
/// is replaced, nor write its rotated pair over the new one. The new authorization must belong to the same provider account
/// the credential was connected as; one that does not is a different connection and is refused. A credential retired as its
/// person left the team or its connection was removed stays retired.</para>
///
/// <para>It is not a connect: no row is created, so what a connect does about the owner's other dead credentials — carry their
/// bindings to the new one — does not happen here. A verdict about the replaced token that lands later (a sweep's, a
/// refresher's) is a compare-and-swap on the payload it judged, which the row no longer holds, so it changes nothing; and a
/// pair this process kept for the grant that was replaced is dropped under the same lock, never stored over the new one.</para>
/// </summary>
public interface ICredentialReauthorizer
{
    /// <summary>
    /// Replaces the credential's token, auth type, scopes, expiry (a static token's as its provider reports it, so the health the
    /// list derives is of the NEW token) and (when given) display name; sets it Active with its last error cleared — a credential
    /// its provider had refused included — and <c>LastValidatedDate</c> now; revives the repositories and group hooks its dead
    /// token had broken; relinks the owner's act-as identity; and, once the caller's unit of work commits, asks the provider to
    /// revoke the tokens that were replaced — those tokens only, never the user's grant. The row's write is durable at once, on a
    /// connection of its own; the rest are tracked changes the caller's unit of work saves.
    /// </summary>
    Task<CredentialReauthorizationResult> ReauthorizeAsync(CredentialReauthorization reauthorization, CancellationToken cancellationToken);
}
