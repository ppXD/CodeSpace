using CodeSpace.Core.Services.OAuth;
using Shouldly;

namespace CodeSpace.UnitTests.OAuth;

/// <summary>
/// 🟢 Unit: where a finished OAuth connect may send the browser. The address comes from whoever starts the connect,
/// and the browser that follows it has just left a consent screen that made the whole trip look legitimate — so only
/// a path on this site, or an address on an origin this deployment serves its web app from, is ever followed.
/// </summary>
[Trait("Category", "Unit")]
public class OAuthReturnUrlTests
{
    private static readonly string[] Origins = { "https://codespace.example", "https://alias.example:8443" };

    [Theory]
    [InlineData("/")]
    [InlineData("/oauth-callback")]
    [InlineData("/settings/credentials?tab=personal")]
    [InlineData("https://codespace.example/oauth-callback")]
    [InlineData("HTTPS://CODESPACE.EXAMPLE/oauth-callback")]
    [InlineData("https://codespace.example:443/x")]
    [InlineData("https://alias.example:8443/oauth-callback")]
    public void A_path_on_this_site_or_an_address_on_any_of_its_origins_is_allowed(string returnUrl)
    {
        OAuthReturnUrl.IsAllowed(returnUrl, Origins).ShouldBeTrue();
    }

    [Theory]
    [InlineData("https://attacker.example/landing")]
    [InlineData("//attacker.example/x")]                      // scheme-relative: another host to a browser
    [InlineData("/\\attacker.example/x")]                     // '\' is '/' to a browser
    [InlineData("/\t/attacker.example/x")]                    // browsers strip the tab, leaving '//'
    [InlineData("javascript:alert(1)")]
    [InlineData("http://codespace.example/x")]                // right host, wrong scheme
    [InlineData("https://codespace.example:8443/x")]          // right host, wrong port
    [InlineData("https://alias.example/x")]                   // the alias, on a port it does not answer on
    [InlineData("https://codespace.example.attacker.example/x")]
    [InlineData("https://user@codespace.example/x")]          // user-info: nobody's return address carries one
    [InlineData("settings")]
    [InlineData("")]
    public void Anything_else_is_refused(string returnUrl)
    {
        OAuthReturnUrl.IsAllowed(returnUrl, Origins).ShouldBeFalse();
    }

    [Theory]
    [InlineData("/oauth-callback", true)]
    [InlineData("https://codespace.example/oauth-callback", false)]
    public void A_path_needs_no_configured_origin_and_an_absolute_address_needs_one(string returnUrl, bool allowed)
    {
        // A deployment that configured no origin at all still connects with a path; it cannot be sent anywhere absolute.
        OAuthReturnUrl.IsAllowed(returnUrl, Array.Empty<string>()).ShouldBe(allowed);
    }

    [Theory]
    [InlineData(null, "/")]
    [InlineData("https://attacker.example/landing", "/")]
    [InlineData("/oauth-callback", "/oauth-callback")]
    [InlineData("https://alias.example:8443/oauth-callback", "https://alias.example:8443/oauth-callback")]
    public void A_refused_or_missing_address_falls_back_to_the_root(string? returnUrl, string landing)
    {
        OAuthReturnUrl.AllowedOrFallback(returnUrl, Origins).ShouldBe(landing);
    }

    /// <summary>
    /// A connect handed to the SPA to finish lands on the SPA's own completion route — on the origin the return address
    /// named when that origin passes, or on this site when it named a path, nothing, or somewhere that no longer passes.
    /// The code travels with it, so it never lands on an origin outside the set.
    /// </summary>
    [Theory]
    [InlineData("https://alias.example:8443/settings?tab=x", "https://alias.example:8443/oauth-callback")]
    [InlineData("HTTPS://CODESPACE.EXAMPLE/oauth-callback", "https://codespace.example/oauth-callback")]
    [InlineData("/settings/credentials", "/oauth-callback")]
    [InlineData(null, "/oauth-callback")]
    [InlineData("https://attacker.example/oauth-callback", "/oauth-callback")]
    public void A_handed_off_connect_finishes_on_the_completion_page_of_an_allowed_origin(string? returnUrl, string completionPage)
    {
        OAuthReturnUrl.CompletionPageFor(returnUrl, Origins).ShouldBe(completionPage);
    }

    [Fact]
    public void A_hand_off_carries_the_code_and_state_escaped_in_the_fragment_only()
    {
        var handOff = OAuthReturnUrl.HandOff("https://spa.example/oauth-callback", "a/b+c d", "st&ate");

        handOff.ShouldBe("https://spa.example/oauth-callback#oauthCode=a%2Fb%2Bc%20d&oauthState=st%26ate");
    }

    /// <summary>
    /// Drift detector across the language seam: the callback writes the hand-off, the SPA's completion page reads it, and
    /// no compiler sees both. Renaming a key, moving the route, or reading the query instead of the fragment on either
    /// side would hand every cross-site connect to a page that finds nothing — so the page is read here as it ships.
    /// </summary>
    [Fact]
    public void The_completion_page_reads_the_hand_off_where_the_callback_writes_it()
    {
        var page = File.ReadAllText(Path.Combine(FindRepoRoot(), "frontend", "src", "routes", "oauth-callback.tsx"));

        page.ShouldContain($"createFileRoute(\"{OAuthReturnUrl.CompletionPage}\")");
        page.ShouldContain($"get(\"{OAuthReturnUrl.HandOffCodeKey}\")");
        page.ShouldContain($"get(\"{OAuthReturnUrl.HandOffStateKey}\")");
        page.ShouldContain("window.location.hash", customMessage: "the code arrives in the fragment; a page reading the query would find nothing");
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);

        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "frontend"))) dir = dir.Parent;

        return dir?.FullName ?? throw new InvalidOperationException("repo root not found");
    }
}
