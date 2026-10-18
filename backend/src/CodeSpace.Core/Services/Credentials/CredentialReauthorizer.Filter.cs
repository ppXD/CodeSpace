using CodeSpace.Messages.Credentials;

namespace CodeSpace.Core.Services.Credentials;

public sealed partial class CredentialReauthorizer
{
    public enum AccountMatch
    {
        /// <summary>Nothing both sides know to compare — the provider gave no id and no name, or the credential has none on record.</summary>
        Unknown,
        Same,
        Different
    }

    /// <summary>
    /// Whether the account that signed in is the account on record. The provider's own id is authoritative where both sides
    /// have one — handles are renamed, ids are not — and the handle, compared without regard to case as the providers do, is
    /// the fallback when either side lacks an id.
    /// </summary>
    internal static AccountMatch Compare(string? knownId, string? knownName, string? presentedId, string? presentedName)
    {
        if (!string.IsNullOrWhiteSpace(knownId) && !string.IsNullOrWhiteSpace(presentedId)) return knownId == presentedId ? AccountMatch.Same : AccountMatch.Different;

        if (!string.IsNullOrWhiteSpace(knownName) && !string.IsNullOrWhiteSpace(presentedName)) return string.Equals(knownName, presentedName, StringComparison.OrdinalIgnoreCase) ? AccountMatch.Same : AccountMatch.Different;

        return AccountMatch.Unknown;
    }

    /// <summary>
    /// The OAuth tokens the re-authorization replaced and the provider should be asked to end, or null when there is nothing to
    /// end: the row held no OAuth tokens, or the provider handed the new authorization the very tokens it already held — one
    /// grant reused for a second sign-in, as GitLab through 15.4 does — and revoking those would revoke the new ones.
    /// </summary>
    internal static OAuthPayload? ReplacedPairToRevoke(CredentialPayload? replaced, CredentialPayload next)
    {
        if (replaced is not OAuthPayload pair) return null;

        if (next is not OAuthPayload fresh) return pair;

        var sharesAToken = pair.AccessToken == fresh.AccessToken || (!string.IsNullOrEmpty(pair.RefreshToken) && pair.RefreshToken == fresh.RefreshToken);

        return sharesAToken ? null : pair;
    }
}
