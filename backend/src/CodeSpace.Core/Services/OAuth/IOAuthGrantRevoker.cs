using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Messages.Credentials;

namespace CodeSpace.Core.Services.OAuth;

/// <summary>
/// The provider side of ending an OAuth credential: a Disconnect asks it for one credential, deleting a connection for
/// every credential connected through it.
/// </summary>
public interface IOAuthGrantRevoker
{
    /// <summary>The OAuth tokens a credential holds, or null when its stored payload does not open or does not read as OAuth tokens.</summary>
    OAuthPayload? ReadPayload(Credential credential);

    /// <summary>
    /// Ask the provider <paramref name="instance"/> names to revoke the refresh token, when there is one, then the access
    /// token, presenting the connection's OAuth app as <c>OauthClientId</c> and <paramref name="clientSecret"/>. The secret
    /// is handed in, not read off the connection, so a caller that has already blanked it can still revoke.
    /// <paramref name="wholeGrant"/> is <see cref="OAuthRevokeInput.WholeGrant"/> on every request: a Disconnect ends the
    /// user's grant to the app; a connection delete ends only these tokens, because the grant also carries the user's
    /// tokens in every other team connected through the same app. Throws what the provider answers; the caller decides
    /// that the revoke is best effort.
    /// </summary>
    Task RevokeAsync(ProviderInstance instance, string clientSecret, OAuthPayload payload, bool wholeGrant, CancellationToken cancellationToken);
}
