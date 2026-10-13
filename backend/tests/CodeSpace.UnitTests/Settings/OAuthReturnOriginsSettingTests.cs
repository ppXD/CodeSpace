using CodeSpace.Core.Settings.OAuth;
using Microsoft.Extensions.Configuration;
using Shouldly;

namespace CodeSpace.UnitTests.Settings;

/// <summary>
/// 🟢 Unit: the origins an OAuth connect may return the browser to, as configuration states them — and never a reason to
/// refuse to serve. <c>PublicBaseUrlSetting</c> throws outside Development when its key is unset, and an OAuth service
/// that injected it answered 500 to every connect, path return or not, on the shipped docker-compose API. This setting
/// reads the same key raw, so a missing value only narrows the set.
/// </summary>
[Trait("Category", "Unit")]
public class OAuthReturnOriginsSettingTests
{
    [Fact]
    public void Outside_development_with_nothing_configured_it_is_empty_rather_than_a_failure()
    {
        var setting = Should.NotThrow(() => Build(Production()));

        setting.Value.ShouldBeEmpty();
    }

    [Fact]
    public void The_public_base_url_is_read_raw_and_trimmed()
    {
        Build(Production(("App:PublicBaseUrl", "  https://codespace.example/  "))).Value.ShouldBe(new[] { "https://codespace.example" });
    }

    [Theory]
    [InlineData("Cors:AllowedOrigins:0", "https://spa.example")]
    [InlineData("Cors:AllowedOrigins", "https://spa.example, https://alias.example")]
    public void Every_cors_origin_is_a_return_origin_in_either_configuration_shape(string key, string value)
    {
        // A direct-mode SPA calls the API cross-origin, so its origin is on this list or it could not call at all.
        Build(Production((key, value))).Value.ShouldContain("https://spa.example");
    }

    [Fact]
    public void The_callback_urls_own_origin_is_a_return_origin_and_its_path_is_not()
    {
        // The provider already sends the browser to this host; in a same-origin deployment it is where the SPA answers.
        Build(Production(("OAuth:CallbackUrl", "https://codespace.example/api/credentials/oauth/callback"))).Value.ShouldBe(new[] { "https://codespace.example" });
    }

    [Theory]
    [InlineData("not a url")]
    [InlineData("/api/credentials/oauth/callback")]
    [InlineData("ftp://files.example/callback")]
    public void A_callback_url_that_names_no_web_origin_adds_nothing_and_breaks_nothing(string callbackUrl)
    {
        Should.NotThrow(() => Build(Production(("OAuth:CallbackUrl", callbackUrl)))).Value.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("Development")]
    [InlineData("development")]
    public void Development_adds_the_dev_spa_origins_the_cors_policy_adds(string? environment)
    {
        var value = Build(new Dictionary<string, string?> { ["ASPNETCORE_ENVIRONMENT"] = environment }).Value;

        value.ShouldBe(new[] { "http://localhost:5180", "http://127.0.0.1:5180" }, ignoreOrder: true);
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    public void No_other_environment_gets_the_dev_origins(string environment)
    {
        Build(new Dictionary<string, string?> { ["ASPNETCORE_ENVIRONMENT"] = environment }).Value.ShouldBeEmpty();
    }

    [Fact]
    public void The_sources_are_merged_without_duplicates()
    {
        var value = Build(Production(("App:PublicBaseUrl", "https://codespace.example"), ("Cors:AllowedOrigins", "HTTPS://CODESPACE.EXAMPLE,https://alias.example"), ("OAuth:CallbackUrl", "https://codespace.example/api/credentials/oauth/callback"))).Value;

        value.ShouldBe(new[] { "https://codespace.example", "https://alias.example" });
    }

    private static Dictionary<string, string?> Production(params (string Key, string Value)[] entries)
    {
        var values = new Dictionary<string, string?> { ["ASPNETCORE_ENVIRONMENT"] = "Production" };

        foreach (var (key, value) in entries) values[key] = value;

        return values;
    }

    private static OAuthReturnOriginsSetting Build(Dictionary<string, string?> values) => new(new ConfigurationBuilder().AddInMemoryCollection(values).Build());
}
