using CodeSpace.Core.Services.OAuth;
using Shouldly;

namespace CodeSpace.UnitTests.OAuth;

/// <summary>
/// 🟢 Unit: where a finished OAuth connect may send the browser. The address comes from whoever starts the connect,
/// and the browser that follows it has just left a consent screen that made the whole trip look legitimate — so only
/// a path on this site, or an address on its public origin, is ever followed.
/// </summary>
[Trait("Category", "Unit")]
public class OAuthReturnUrlTests
{
    private const string PublicOrigin = "https://codespace.example";

    [Theory]
    [InlineData("/")]
    [InlineData("/oauth-callback")]
    [InlineData("/settings/credentials?tab=personal")]
    [InlineData("https://codespace.example/oauth-callback")]
    [InlineData("HTTPS://CODESPACE.EXAMPLE/oauth-callback")]
    [InlineData("https://codespace.example:443/x")]
    public void A_path_on_this_site_or_an_address_on_its_public_origin_is_allowed(string returnUrl)
    {
        OAuthReturnUrl.IsAllowed(returnUrl, PublicOrigin).ShouldBeTrue();
    }

    [Theory]
    [InlineData("https://attacker.example/landing")]
    [InlineData("//attacker.example/x")]                      // scheme-relative: another host to a browser
    [InlineData("/\\attacker.example/x")]                     // '\' is '/' to a browser
    [InlineData("/\t/attacker.example/x")]                    // browsers strip the tab, leaving '//'
    [InlineData("javascript:alert(1)")]
    [InlineData("http://codespace.example/x")]                // right host, wrong scheme
    [InlineData("https://codespace.example:8443/x")]          // right host, wrong port
    [InlineData("https://codespace.example.attacker.example/x")]
    [InlineData("https://user@codespace.example/x")]          // user-info: nobody's return address carries one
    [InlineData("settings")]
    [InlineData("")]
    public void Anything_else_is_refused(string returnUrl)
    {
        OAuthReturnUrl.IsAllowed(returnUrl, PublicOrigin).ShouldBeFalse();
    }

    [Theory]
    [InlineData(null, "/")]
    [InlineData("https://attacker.example/landing", "/")]
    [InlineData("/oauth-callback", "/oauth-callback")]
    public void A_refused_or_missing_address_falls_back_to_the_root(string? returnUrl, string landing)
    {
        OAuthReturnUrl.AllowedOrFallback(returnUrl, PublicOrigin).ShouldBe(landing);
    }
}
