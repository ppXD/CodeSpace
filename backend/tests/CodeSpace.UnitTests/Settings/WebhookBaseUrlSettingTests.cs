using System;
using System.Collections.Generic;
using CodeSpace.Core.Settings.Webhooks;
using Microsoft.Extensions.Configuration;
using Shouldly;

namespace CodeSpace.UnitTests.Settings;

/// <summary>
/// 🟢 Unit: the webhook base URL refuses to fall back outside Development, the same posture <c>App:PublicBaseUrl</c>
/// takes. The loopback default cannot be reached by GitHub or GitLab, so a deployment that forgot the setting used
/// to register hooks that looked healthy on both sides and delivered nothing — the kind of failure that surfaces
/// weeks later as "pushes stopped triggering runs", with no error anywhere pointing at the configuration.
/// </summary>
[Trait("Category", "Unit")]
public class WebhookBaseUrlSettingTests
{
    [Fact]
    public void Reads_the_key_it_claims()
    {
        // The literal is the operator's contract — the live deployment sets Webhooks__BaseUrl, so a rename that
        // "looks harmless" silently drops that pod back to the default and now throws instead.
        WebhookBaseUrlSetting.ConfigurationKey.ShouldBe("Webhooks:BaseUrl");

        Resolve("https://codespace-api.example.com", environment: "Production").ShouldBe("https://codespace-api.example.com");
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    [InlineData("production")]
    public void Refuses_to_fall_back_outside_development(string environment)
    {
        var error = Should.Throw<InvalidOperationException>(() => Resolve(configured: null, environment),
            customMessage: "an unconfigured non-Development host must refuse rather than register loopback hooks the provider can never deliver to");

        error.Message.ShouldContain("Webhooks:BaseUrl");
        error.Message.ShouldContain("Webhooks__BaseUrl", Case.Sensitive);
    }

    [Theory]
    [InlineData("Development")]
    [InlineData("development")]
    [InlineData(null)]
    public void Falls_back_to_loopback_in_development(string? environment)
    {
        // An unset environment is the plain `dotnet test` / `dotnet run` case, which is a developer's machine.
        Resolve(configured: null, environment).ShouldBe("https://localhost");
    }

    [Fact]
    public void Reads_the_environment_from_the_non_web_host_variable_too()
    {
        // A worker started as a generic host is told its environment by DOTNET_ENVIRONMENT; reading only the
        // ASPNETCORE_ one would let that process fall back to loopback in production.
        var configuration = Build(new Dictionary<string, string?> { ["DOTNET_ENVIRONMENT"] = "Production" });

        Should.Throw<InvalidOperationException>(() => new WebhookBaseUrlSetting(configuration));
    }

    [Fact]
    public void Trims_a_trailing_slash_that_would_double_up_in_the_callback_path()
    {
        // The registered callback is $"{Value}/api/webhooks/{id}"; a trailing slash here registers a "//api/..." URL.
        Resolve("https://codespace-api.example.com/", environment: "Production").ShouldBe("https://codespace-api.example.com");
    }

    private static string Resolve(string? configured, string? environment) =>
        new WebhookBaseUrlSetting(Build(new Dictionary<string, string?>
        {
            ["Webhooks:BaseUrl"] = configured,
            ["ASPNETCORE_ENVIRONMENT"] = environment,
        })).Value;

    private static IConfiguration Build(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    /// <summary>
    /// The guard is only a guard if the shipped configuration cannot satisfy it.
    ///
    /// <para>Both of these settings once carried a value in <c>appsettings.json</c>, which is loaded
    /// non-optionally — so <c>configured</c> was never blank on any host, the fail-outside-Development branch
    /// was unreachable, and a production deployment that never set the key still started happily on the
    /// development default. That is the exact failure both guards were written to prevent, and each of them
    /// disarmed itself.</para>
    /// </summary>
    [Theory]
    [InlineData("Webhooks", "BaseUrl")]
    [InlineData("App", "PublicBaseUrl")]
    public void The_shipped_configuration_does_not_disarm_the_guard(string section, string key)
    {
        var shipped = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(FindRepoRoot(), "backend", "src", "CodeSpace.Api", "appsettings.json"))
            .Build();

        shipped[$"{section}:{key}"].ShouldBeNullOrWhiteSpace(
            $"appsettings.json must not ship a value for {section}:{key} — it is loaded non-optionally, so a value "
          + "here is indistinguishable from a configured one and the guard can never fire");
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);

        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "backend"))) dir = dir.Parent;

        return dir?.FullName ?? throw new InvalidOperationException("repo root not found");
    }
}
