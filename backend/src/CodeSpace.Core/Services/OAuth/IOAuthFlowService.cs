using CodeSpace.Messages.Commands.Credentials;

namespace CodeSpace.Core.Services.OAuth;

/// <summary>
/// Browser-driven OAuth flow for credential acquisition:
///   1. <see cref="InitAsync"/> creates a one-time pending-state row and returns the
///      provider's authorize URL the SPA opens in a popup.
///   2. <see cref="CompleteAsync"/> runs from the callback redirect — anonymous, with
///      only the state token as proof of identity — consumes the state, exchanges
///      the auth code, and persists a Credential.
/// </summary>
public interface IOAuthFlowService
{
    Task<InitCredentialOAuthResult> InitAsync(Guid providerInstanceId, string displayName, Guid? intendedOwnerUserId, string? returnUrl, IReadOnlyList<string>? scopes, CancellationToken cancellationToken);
    Task<CompleteCredentialOAuthResult> CompleteAsync(string state, string code, CancellationToken cancellationToken);
}
