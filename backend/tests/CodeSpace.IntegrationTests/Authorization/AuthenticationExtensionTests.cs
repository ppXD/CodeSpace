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
public class AuthenticationExtensionTests
{
    [Fact]
    public void MinKeyByteLength_constant_pinned()
    {
        // Operators tune key length expectations from this number. Pin so renames break the test.
        AuthenticationExtension.MinKeyByteLength.ShouldBe(32);
    }

    [Fact]
    public void AllowAnonymousFallbackEnvVar_constant_pinned()
    {
        AuthenticationExtension.AllowAnonymousFallbackEnvVar.ShouldBe("CODESPACE_ALLOW_ANONYMOUS_FALLBACK");
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

    [Fact]
    public void Missing_jwt_key_in_Production_throws()
    {
        var services = new ServiceCollection();
        var configuration = BuildConfiguration(jwtKey: null);
        var environment = BuildEnvironment(Environments.Production);

        var act = () => services.AddCustomAuthentication(configuration, environment);

        var ex = act.ShouldThrow<InvalidOperationException>();
        ex.Message.ShouldContain("Production");
    }

    [Fact]
    public void Missing_jwt_key_in_Development_without_explicit_opt_in_throws()
    {
        ClearAllowAnonymousFallback();

        var services = new ServiceCollection();
        var configuration = BuildConfiguration(jwtKey: null);
        var environment = BuildEnvironment(Environments.Development);

        var act = () => services.AddCustomAuthentication(configuration, environment);

        var ex = act.ShouldThrow<InvalidOperationException>();
        ex.Message.ShouldContain(AuthenticationExtension.AllowAnonymousFallbackEnvVar);
    }

    [Fact]
    public void Missing_jwt_key_in_Development_with_opt_in_returns_silently()
    {
        Environment.SetEnvironmentVariable(AuthenticationExtension.AllowAnonymousFallbackEnvVar, "true");
        try
        {
            var services = new ServiceCollection();
            var configuration = BuildConfiguration(jwtKey: null);
            var environment = BuildEnvironment(Environments.Development);

            // Should NOT throw — operator explicitly accepted anonymous fallback.
            Action act = () => services.AddCustomAuthentication(configuration, environment);
            act.ShouldNotThrow();
        }
        finally
        {
            ClearAllowAnonymousFallback();
        }
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

    private static void ClearAllowAnonymousFallback() => Environment.SetEnvironmentVariable(AuthenticationExtension.AllowAnonymousFallbackEnvVar, null);

    private sealed class FakeHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Production;
        public string ApplicationName { get; set; } = "test";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = null!;
    }
}
