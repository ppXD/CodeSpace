using CodeSpace.Core.Services.Providers;
using CodeSpace.Messages.Credentials;

namespace CodeSpace.Core.Services.OAuth;

/// <summary>
/// What an OAuth credential does when the token it holds cannot be used as the row stands: its provider answered 401 to an
/// access token it was just handed, or this process holds a pair the provider issued that the row does not. A sibling of
/// <see cref="IOAuthTokenRefresher"/>, which decides from the token's expiry and so never sees an access token the provider
/// retired early — another consumer rotated the pair and stored it, or the provider's token lifetime is shorter than the
/// <c>expires_in</c> it reported. The refresher's callers resolve a token once; this is the way back when it was refused.
/// </summary>
public interface IOAuthTokenRecovery
{
    /// <summary>
    /// Under the credential's refresh locks, from the STORED row: a token other than <paramref name="rejectedAccessToken"/>
    /// is the pair another caller already replaced it with, and is adopted; the very token that was refused is rotated once,
    /// with the stored refresh token. Null when there is nothing to try: no refresh token, or the credential was rotated
    /// moments ago and the new token is refused too — the 401 is not the token's age, and rotating again would only spend
    /// refresh tokens. A rotation the provider refuses for good is <c>CredentialNeedsReconnectException</c>, as in a refresh.
    /// </summary>
    Task<OAuthPayload?> RecoverAfterRejectionAsync(ProviderContext context, string rejectedAccessToken, CancellationToken cancellationToken);

    /// <summary>
    /// Stores the pair this process holds for the credential, if it holds one: the provider rotated it and every attempt to
    /// store it failed, so the row still holds the refresh token the provider consumed. Asked before the credential is read, so
    /// a verdict another process wrote over that row — its refusal of the consumed token marked the credential <c>Error</c> — does not stand in front of a
    /// live pair; storing it lifts the verdict, as any refresh the provider accepts does. Nothing happens, and no lock is
    /// taken, when this process holds no pair for the credential or the credential is disconnected.
    /// </summary>
    Task StoreKeptPairAsync(ProviderContext context, CancellationToken cancellationToken);
}
