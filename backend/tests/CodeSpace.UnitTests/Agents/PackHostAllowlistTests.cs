using CodeSpace.Core.Services.Agents;
using Shouldly;

namespace CodeSpace.UnitTests.Agents;

/// <summary>
/// Pins the pack-source egress guard: only an https URL whose host is on the allowlist (github.com + gitlab.com,
/// plus operator-configured hosts) is clonable; http / file:// / ssh / a non-allowlisted (internal) host is
/// refused with an actionable reason. This is the SSRF / internal-fetch boundary for "paste a URL → clone".
/// </summary>
[Trait("Category", "Unit")]
public class PackHostAllowlistTests
{
    [Theory]
    [InlineData("https://github.com/wshobson/agents")]
    [InlineData("https://gitlab.com/team/pack.git")]
    [InlineData("https://GitHub.com/owner/repo")]   // host match is case-insensitive
    [InlineData("https://github.com./owner/repo")]   // trailing-dot FQDN — a valid absolute DNS name git would clone
    public void Allows_https_github_and_gitlab(string url)
    {
        var allowlist = new PackHostAllowlist(rawAllowedHostsOverride: null);

        allowlist.IsAllowed(url).ShouldBeTrue();
        Should.NotThrow(() => allowlist.EnsureAllowed(url));
    }

    [Theory]
    [InlineData("http://github.com/owner/repo", "Only https")]          // not https
    [InlineData("file:///etc/passwd", "Only https")]                    // not https
    [InlineData("ssh://git@github.com/owner/repo", "Only https")]       // not https
    [InlineData("https://internal.corp/secret", "allowlist")]           // not an allowlisted host (SSRF / internal)
    [InlineData("https://169.254.169.254/latest/meta-data", "allowlist")]   // cloud metadata host
    [InlineData("not-a-url", "valid absolute URL")]
    public void Refuses_disallowed_urls_with_an_actionable_reason(string url, string reasonFragment)
    {
        var allowlist = new PackHostAllowlist(rawAllowedHostsOverride: null);

        allowlist.IsAllowed(url).ShouldBeFalse();
        var ex = Should.Throw<PackImportException>(() => allowlist.EnsureAllowed(url));
        ex.Message.ShouldContain(reasonFragment);
    }

    [Fact]
    public void A_malformed_url_is_refused_without_echoing_the_credential_it_carries()
    {
        // An unparseable URL has no userinfo to strip, so the refusal names none of it: the message reaches the API error
        // body, the UI and the mediator's error log, and the operator still has what they pasted.
        const string url = "https://x-access-token:fake-pasted-token-0123456789@github.com:notaport/owner/repo";

        var ex = Should.Throw<PackImportException>(() => new PackHostAllowlist(rawAllowedHostsOverride: null).EnsureAllowed(url));

        ex.Message.ShouldContain("valid absolute URL");
        ex.Message.ShouldNotContain("fake-pasted-token-0123456789");
    }

    [Theory]
    [InlineData("fake-pasted-token-0123456789:x-oauth-basic@github.com/owner/repo.git")]
    [InlineData("Fake-Pasted-Token-0123456789:x@gitlab.com/group/repo.git")]   // the scheme comes back lowercased
    public void A_url_pasted_without_https_is_refused_without_echoing_the_token_read_as_its_scheme(string url)
    {
        // Without "https://" the token before the colon parses as the URL's scheme, so naming the scheme names the token.
        Uri.TryCreate(url, UriKind.Absolute, out _).ShouldBeTrue("fixture check: the token parses as the scheme");

        var ex = Should.Throw<PackImportException>(() => new PackHostAllowlist(rawAllowedHostsOverride: null).EnsureAllowed(url));

        ex.Message.ShouldContain("Only https");
        ex.Message.ShouldNotContain("pasted-token-0123456789", Case.Insensitive);
    }

    [Fact]
    public void Operator_env_override_adds_hosts_to_the_defaults()
    {
        var allowlist = new PackHostAllowlist(rawAllowedHostsOverride: "git.example.com, gitea.internal");

        allowlist.IsAllowed("https://git.example.com/team/pack").ShouldBeTrue("an operator-added host is allowed");
        allowlist.IsAllowed("https://gitea.internal/x/y").ShouldBeTrue();
        allowlist.IsAllowed("https://github.com/owner/repo").ShouldBeTrue("the defaults are still allowed alongside the override");
        allowlist.IsAllowed("https://other.host/x").ShouldBeFalse("a host neither default nor configured is still refused");
    }

    [Fact]
    public void Blank_override_entries_are_ignored()
    {
        var hosts = PackHostAllowlist.BuildHosts(" , ,  ");

        hosts.ShouldBe(new[] { "github.com", "gitlab.com" }, ignoreOrder: true);
    }
}
