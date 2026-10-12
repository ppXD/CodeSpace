using System.Security.Cryptography;
using System.Text;

namespace CodeSpace.Core.Services.OAuth;

/// <summary>
/// What ties an OAuth connect to the browser that started it.
///
/// <para>The callback is anonymous by necessity — the provider's redirect carries no session — so on its own the
/// state token was the whole proof, and whoever's browser presented it completed the flow. An admin of one team could
/// start a connect on their own OAuth app, send the authorize link to someone else, and receive that person's provider
/// token as a credential in their team. The start of a connect therefore hands its browser a random nonce, kept in an
/// HttpOnly cookie, and the pending state stores only the nonce's hash; the callback must present the nonce.</para>
///
/// <para>Plain SHA-256, for the reason the invitation token uses it: 256 bits of entropy leave nothing to brute-force,
/// and what matters is that the database never holds a value that would satisfy the check.</para>
/// </summary>
public static class OAuthBrowserNonce
{
    public static string Mint() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static string Hash(string nonce) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(nonce)));
}
