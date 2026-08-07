using CodeSpace.Api.Extensions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;

namespace CodeSpace.IntegrationTests.Authorization;

/// <summary>
/// Pure config-level tests for the JWT bootstrap. No DB; runs as a fast unit-style test
/// inside the IntegrationTests project because that's where the project reference to
/// CodeSpace.Api lives (UnitTests intentionally has no Api dep).
/// </summary>
[Trait("Category", "Integration")]
public class AuthenticationExtensionTests
{
    [Fact]
    public void MinKeyByteLength_constant_pinned()
    {
        // Operators tune key length expectations from this number. Pin so renames break the test.
        AuthenticationExtension.MinKeyByteLength.ShouldBe(32);
    }

    [Fact]
    public void Short_jwt_key_throws_with_min_byte_length_in_message()
    {
        var services = new ServiceCollection();
        var configuration = BuildConfiguration(jwtKey: "too-short");
        var environment = BuildEnvironment(Environments.Production);

        var act = () => services.AddCustomAuthentication(configuration, environment);

        var ex = act.ShouldThrow<InvalidOperationException>();
        ex.Message.ShouldContain("32");
        ex.Message.ShouldContain("bytes");
    }

    /// <summary>
    /// A missing key is fatal in EVERY environment, not just Production. The env escape hatch that used to let a
    /// Development host boot fully anonymous is gone — appsettings.json ships a committed dev key, so the only way to
    /// reach this path is to deliberately blank it, and that should be loud rather than a silent slide into no auth.
    /// </summary>
    [Theory]
    [InlineData("Production")]
    [InlineData("Development")]
    public void Missing_jwt_key_throws_in_every_environment(string environmentName)
    {
        var services = new ServiceCollection();
        var configuration = BuildConfiguration(jwtKey: null);
        var environment = BuildEnvironment(environmentName);

        var act = () => services.AddCustomAuthentication(configuration, environment);

        var ex = act.ShouldThrow<InvalidOperationException>();
        ex.Message.ShouldContain("Authentication:Jwt:SymmetricKey", Case.Sensitive, "the message must name the key an operator has to set");
    }

    [Fact]
    public void Valid_32_byte_key_registers_authentication_successfully()
    {
        var services = new ServiceCollection();
        var configuration = BuildConfiguration(jwtKey: new string('k', AuthenticationExtension.MinKeyByteLength));
        var environment = BuildEnvironment(Environments.Production);

        Action act = () => services.AddCustomAuthentication(configuration, environment);
        act.ShouldNotThrow();
    }

    private static IConfiguration BuildConfiguration(string? jwtKey)
    {
        var dict = new Dictionary<string, string?> { ["Authentication:Jwt:SymmetricKey"] = jwtKey };
        return new ConfigurationBuilder().AddInMemoryCollection(dict).Build();
    }

    private static IHostEnvironment BuildEnvironment(string environmentName) => new FakeHostEnvironment { EnvironmentName = environmentName };

    private sealed class FakeHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Production;
        public string ApplicationName { get; set; } = "test";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = null!;
    }
}
