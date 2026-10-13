using CodeSpace.Messages.Commands.Credentials;

namespace CodeSpace.Core.Services.OAuth;

/// <summary>
/// Browser-driven OAuth flow for credential acquisition:
///   1. <see cref="InitAsync"/> creates a one-time pending-state row and returns the
///      provider's authorize URL the SPA opens in a popup.
///   2. <see cref="CompleteAsync"/> runs from the callback redirect — anonymous, with the
///      state token and the starting browser's nonce as proof — consumes the state,
///      exchanges the auth code, and persists a Credential. Without the nonce it consumes
///      nothing and hands the callback to the SPA's completion page instead.
///   3. <see cref="CompleteAsInitiatorAsync"/> is that page finishing it: the same consume,
///      exchange and persist, with the signed-in caller — who must have started the
///      connect — as the proof in place of the nonce.
/// </summary>
public interface IOAuthFlowService
{
    Task<InitCredentialOAuthResult> InitAsync(Guid providerInstanceId, string displayName, Guid? intendedOwnerUserId, string? returnUrl, IReadOnlyList<string>? scopes, CancellationToken cancellationToken);
    Task<CompleteCredentialOAuthResult> CompleteAsync(string state, string code, string? browserNonce, CancellationToken cancellationToken);
    Task<CompleteCredentialOAuthResult> CompleteAsInitiatorAsync(string state, string code, CancellationToken cancellationToken);
}
