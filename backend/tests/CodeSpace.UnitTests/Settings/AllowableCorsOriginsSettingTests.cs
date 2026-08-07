using CodeSpace.Core.Settings.CorsPolicy;
using Microsoft.Extensions.Configuration;
using Shouldly;

namespace CodeSpace.UnitTests.Settings;

/// <summary>
/// 🟢 Unit: the CORS allow-list reads BOTH shapes the configuration pipeline can deliver. A JSON file naturally
/// expresses a list as an array; a ConfigMap, a Helm value or a plain environment variable can realistically only
/// express it as one comma-separated string. Reading only the array form would leave such a deployment with an empty
/// allow-list — every cross-origin call failing a preflight, with nothing in the logs pointing at the configuration.
/// </summary>
[Trait("Category", "Unit")]
public class AllowableCorsOriginsSettingTests
{
    [Fact]
    public void Reads_the_json_array_form()
    {
        var setting = Build(new Dictionary<string, string?>
        {
            ["Cors:AllowedOrigins:0"] = "https://app.example.com",
            ["Cors:AllowedOrigins:1"] = "https://admin.example.com",
        });

        setting.Value.ShouldBe(new[] { "https://app.example.com", "https://admin.example.com" });
    }

    [Fact]
    public void Reads_the_comma_separated_form_a_configmap_can_express()
    {
        var setting = Build(new Dictionary<string, string?>
        {
            ["Cors:AllowedOrigins"] = "https://app.example.com, https://admin.example.com",
        });

        setting.Value.ShouldBe(new[] { "https://app.example.com", "https://admin.example.com" },
            customMessage: "a ConfigMap or Helm value delivers a list as one string; ignoring that form empties the allow-list");
    }

    [Fact]
    public void Trims_a_trailing_slash_that_would_never_match()
    {
        // A browser sends a bare scheme+host+port as the Origin, so "https://app.example.com/" matches nothing.
        // Silently failing every preflight over one character is not a lesson an operator should have to learn.
        Build(new Dictionary<string, string?> { ["Cors:AllowedOrigins"] = "https://app.example.com/" })
            .Value.ShouldBe(new[] { "https://app.example.com" });
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ,  ,")]
    public void An_unset_or_blank_value_is_an_empty_allow_list(string? raw)
    {
        // Empty means "no cross-origin caller is allowed", which is the correct closed default for a deployment that
        // serves its SPA same-origin — never a crash, and never a silent wildcard.
        Build(new Dictionary<string, string?> { ["Cors:AllowedOrigins"] = raw }).Value.ShouldBeEmpty();
    }

    private static AllowableCorsOriginsSetting Build(Dictionary<string, string?> values) =>
        new(new ConfigurationBuilder().AddInMemoryCollection(values).Build());
}
