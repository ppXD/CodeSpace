using CodeSpace.Core.Services.Agents.Sandbox.Isolation;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

/// <summary>
/// Pins the egress allowlist composition (B3.3b): the model-API host (custom gateway BaseUrl, else the provider's
/// default endpoint) + each repo's git host (http(s) or scp-style) + operator extra hosts, de-duped + lower-cased,
/// unparseable URLs skipped. This is what a restricted run is allowed to reach.
/// </summary>
[Trait("Category", "Unit")]
public class EgressAllowlistBuilderTests
{
    [Fact]
    public void Model_host_comes_from_a_custom_gateway_base_url()
    {
        var hosts = EgressAllowlistBuilder.Build("https://Gateway.Example.com/v1", "Anthropic", Array.Empty<string>(), null);

        hosts.ShouldBe(new[] { "gateway.example.com" }, "the custom gateway host is the model host, lower-cased");
    }

    [Theory]
    [InlineData("Anthropic", "api.anthropic.com")]
    [InlineData("OpenAI", "api.openai.com")]
    [InlineData("OpenRouter", "openrouter.ai")]
    public void Model_host_falls_back_to_the_provider_default_when_no_base_url(string provider, string expected)
    {
        var hosts = EgressAllowlistBuilder.Build(null, provider, Array.Empty<string>(), null);

        hosts.ShouldBe(new[] { expected });
    }

    [Fact]
    public void An_unknown_provider_with_no_base_url_yields_no_model_host()
    {
        EgressAllowlistBuilder.Build(null, "MysteryCo", Array.Empty<string>(), null).ShouldBeEmpty();
        EgressAllowlistBuilder.Build(null, null, Array.Empty<string>(), null).ShouldBeEmpty();
    }

    [Fact]
    public void Git_host_comes_from_an_https_clone_url()
    {
        var hosts = EgressAllowlistBuilder.Build(null, null, new[] { "https://github.com/owner/repo.git" }, null);

        hosts.ShouldBe(new[] { "github.com" });
    }

    [Fact]
    public void Git_host_comes_from_an_scp_style_remote()
    {
        var hosts = EgressAllowlistBuilder.Build(null, null, new[] { "git@gitlab.com:owner/repo.git" }, null);

        hosts.ShouldBe(new[] { "gitlab.com" }, "an scp-style git remote has no scheme, so the host is parsed between @ and :");
    }

    [Fact]
    public void Extra_hosts_are_unioned_in()
    {
        var hosts = EgressAllowlistBuilder.Build(null, "Anthropic", Array.Empty<string>(), new[] { "registry.npmjs.org", "pypi.org" });

        hosts.ShouldBe(new[] { "api.anthropic.com", "registry.npmjs.org", "pypi.org" });
    }

    [Fact]
    public void All_three_sources_combine_and_are_de_duped()
    {
        // The model gateway and the git host are the SAME host (a self-hosted gateway + git on one box) — de-duped to one.
        var hosts = EgressAllowlistBuilder.Build("https://dev.internal/v1", "Anthropic", new[] { "https://dev.internal/team/repo.git" }, new[] { "dev.internal" });

        hosts.ShouldBe(new[] { "dev.internal" }, "identical hosts from different sources collapse to one entry");
    }

    [Fact]
    public void An_unparseable_url_is_skipped_not_thrown()
    {
        var hosts = EgressAllowlistBuilder.Build(null, null, new[] { "not a url", "", "https://ok.example.com/r.git" }, null);

        hosts.ShouldBe(new[] { "ok.example.com" });
    }

    [Theory]
    [InlineData("Anthropic", "api.anthropic.com")]
    [InlineData("OpenAI", "api.openai.com")]
    [InlineData("OpenRouter", "openrouter.ai")]
    public void Provider_default_hosts_are_pinned(string provider, string host)
    {
        // A restricted run on the default endpoint can't reach its model if this host is wrong — hard-pin it (Rule 8).
        EgressAllowlistBuilder.ProviderDefaultHosts[provider].ShouldBe(host);
    }
}
