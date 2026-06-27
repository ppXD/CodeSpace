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
    [Fact]
    public void AllowedHostsEnvVar_name_is_pinned()
    {
        // Rule 8: an operator pins extra hosts via this env var; renaming it silently re-closes their configured host.
        PackHostAllowlist.AllowedHostsEnvVar.ShouldBe("CODESPACE_PACK_ALLOWED_HOSTS");
    }

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
    [InlineData("http://github.com/owner/repo", "scheme")]              // not https
    [InlineData("file:///etc/passwd", "scheme")]                        // not https
    [InlineData("ssh://git@github.com/owner/repo", "scheme")]           // not https
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
