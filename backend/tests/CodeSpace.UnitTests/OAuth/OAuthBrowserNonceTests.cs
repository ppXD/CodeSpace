using CodeSpace.Core.Services.OAuth;
using Shouldly;

namespace CodeSpace.UnitTests.OAuth;

/// <summary>
/// 🟢 Unit: the nonce that ties an OAuth connect to the browser that started it. Unguessable, cookie-safe, and
/// stored only as a digest — a database read must never yield a value that completes someone's connect.
/// </summary>
[Trait("Category", "Unit")]
public class OAuthBrowserNonceTests
{
    [Fact]
    public void A_nonce_is_256_bits_of_url_and_cookie_safe_text_and_never_repeats()
    {
        var nonces = Enumerable.Range(0, 64).Select(_ => OAuthBrowserNonce.Mint()).ToList();

        nonces.Distinct().Count().ShouldBe(nonces.Count);
        nonces.ShouldAllBe(n => n.Length == 43, "32 bytes of base64url without padding");
        nonces.ShouldAllBe(n => n.All(c => char.IsAsciiLetterOrDigit(c) || c == '-' || c == '_'), "a cookie value must need no quoting or escaping");
    }

    [Fact]
    public void Only_a_digest_is_stored_and_the_same_nonce_always_digests_the_same()
    {
        var nonce = OAuthBrowserNonce.Mint();

        var hash = OAuthBrowserNonce.Hash(nonce);

        hash.ShouldBe(OAuthBrowserNonce.Hash(nonce));
        hash.Length.ShouldBe(64, "hex SHA-256");
        hash.ShouldNotContain(nonce);
        OAuthBrowserNonce.Hash(OAuthBrowserNonce.Mint()).ShouldNotBe(hash);
    }
}
