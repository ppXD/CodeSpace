using System.Diagnostics;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Settings;
using CodeSpace.Messages.Agents;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Shouldly;

namespace CodeSpace.IntegrationTests.Settings;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SandboxConfigurationStartupCollection
{
    public const string Name = "SandboxConfigurationStartup";
}

[Trait("Category", "Integration")]
[Collection(SandboxConfigurationStartupCollection.Name)]
public sealed class SandboxConfigurationStartupTests : IDisposable
{
    private readonly RuntimeSettings _original = RuntimeSettings.Current;

    [Theory]
    [InlineData("Api", "Sandbox:MaxAutonomy", "Unlesahed")]
    [InlineData("Worker", "Sandbox:MaxAutonomy", "Unlesahed")]
    [InlineData("Api", "Sandbox:RequireConfinement", "flase")]
    [InlineData("Worker", "Sandbox:RequireConfinement", "flase")]
    [InlineData("Api", "Sandbox:AgentMemoryCeilingMb", "0")]
    [InlineData("Worker", "Sandbox:AgentMemoryCeilingMb", "0")]
    public void The_real_host_factory_rejects_invalid_sandbox_configuration_during_container_build(string role, string key, string value)
    {
        var configuration = Configuration(role);
        configuration[key] = value;

        var exception = Should.Throw<Exception>(() => BuildHost(configuration)).GetBaseException();

        exception.GetType().Name.ShouldBe("SandboxConfigurationException");
        exception.Message.ShouldContain(key);
    }

    [Theory]
    [InlineData("Api")]
    [InlineData("Worker")]
    public void Valid_settings_reach_the_real_host_and_deployment_policy_without_relaxation(string role)
    {
        var configuration = Configuration(role);
        configuration["Sandbox:MaxAutonomy"] = "Confined";
        configuration["Sandbox:RequireConfinement"] = "true";
        configuration["Sandbox:AgentMemoryCeilingMb"] = "512";

        using var host = BuildHost(configuration);

        RuntimeSettings.Current.RequireSandboxConfinement.ShouldBeTrue();
        RuntimeSettings.Current.AgentMemoryCeilingMb.ShouldBe(512);
        AgentAutonomyPolicy.DeploymentCeiling.ShouldBe(AgentAutonomyLevel.Confined);
        AgentAutonomyPolicy.Ceilings(AgentAutonomyLevel.Unleashed, RuntimeSettings.Current.AgentMemoryCeilingMb).MemoryMb.ShouldBe(512);
    }

    [Theory]
    [InlineData("Api")]
    [InlineData("Worker")]
    public void Missing_sandbox_settings_do_not_prevent_the_real_host_from_building(string role)
    {
        using var host = BuildHost(Configuration(role));

        RuntimeSettings.Current.RequireSandboxConfinement.ShouldBeFalse();
        RuntimeSettings.Current.AgentMemoryCeilingMb.ShouldBeNull();
        AgentAutonomyPolicy.DeploymentCeiling.ShouldBe(AgentAutonomyLevel.Unleashed);
    }

    [Fact]
    public void A_failed_host_build_does_not_replace_an_existing_restrictive_configuration()
    {
        var valid = Configuration("Worker");
        valid[RuntimeSettings.MaxAutonomyKey] = "Confined";
        using var host = BuildHost(valid);
        var bound = RuntimeSettings.Current;
        var invalid = Configuration("Api");
        invalid[RuntimeSettings.MaxAutonomyKey] = "Unlesahed";

        Should.Throw<Exception>(() => BuildHost(invalid)).GetBaseException().ShouldBeOfType<SandboxConfigurationException>();

        RuntimeSettings.Current.ShouldBeSameAs(bound);
        AgentAutonomyPolicy.DeploymentCeiling.ShouldBe(AgentAutonomyLevel.Confined);
    }

    [Theory]
    [InlineData("Api")]
    [InlineData("Worker")]
    public async Task The_process_entry_point_fails_before_migrations_or_host_start_on_a_bad_ceiling(string role)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"codespace-invalid-sandbox-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var testAssembly = typeof(SandboxConfigurationStartupTests).Assembly.Location;
            var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
            {
                WorkingDirectory = directory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            start.ArgumentList.Add("exec");
            start.ArgumentList.Add("--runtimeconfig");
            start.ArgumentList.Add(Path.ChangeExtension(testAssembly, "runtimeconfig.json"));
            start.ArgumentList.Add("--depsfile");
            start.ArgumentList.Add(Path.ChangeExtension(testAssembly, "deps.json"));
            start.ArgumentList.Add(typeof(CodeSpace.Api.Program).Assembly.Location);
            foreach (var key in start.Environment.Keys.Where(key => key.StartsWith("Sandbox__", StringComparison.OrdinalIgnoreCase)).ToArray()) start.Environment.Remove(key);
            start.Environment["ASPNETCORE_ENVIRONMENT"] = "Production";
            start.Environment["HangfireHosting"] = role;
            // Main resolves JSON beside the assembly. Override it through its actual final environment provider,
            // rather than writing an unused appsettings file in the child's working directory.
            start.Environment["Sandbox__MaxAutonomy"] = "Unlesahed";

            using var process = Process.Start(start).ShouldNotBeNull();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var stdout = process.StandardOutput.ReadToEndAsync(deadline.Token);
            var stderr = process.StandardError.ReadToEndAsync(deadline.Token);
            try
            {
                await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
            }
            finally
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
            }

            var output = await stdout.ConfigureAwait(false) + await stderr.ConfigureAwait(false);
            output.ShouldContain("SandboxConfigurationException");
            process.ExitCode.ShouldNotBe(0);
            output.ShouldContain(RuntimeSettings.MaxAutonomyKey);
            output.ShouldNotContain("Starting CodeSpace.Api host");
            output.ShouldNotContain("DbUpRunner");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static IHost BuildHost(Dictionary<string, string?> configuration) => CodeSpace.Api.Program.CreateHostBuilder([]).UseEnvironment("Production").ConfigureAppConfiguration((_, builder) =>
    {
        builder.Sources.Clear();
        builder.AddInMemoryCollection(configuration);
    }).Build();

    private static Dictionary<string, string?> Configuration(string role) => new()
    {
        ["ASPNETCORE_ENVIRONMENT"] = "Production",
        ["HangfireHosting"] = role,
        ["Variables:MasterKey"] = "AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8=",
        ["CodeSpaceStore:ConnectionString"] = "Host=127.0.0.1;Port=1;Database=unused;Timeout=1",
        ["Authentication:Jwt:SymmetricKey"] = "test-only-key-do-not-use-in-prod-minimum-32-chars",
        ["App:PublicBaseUrl"] = "https://codespace.example"
    };

    public void Dispose()
    {
        // Bind is deployment-global. This collection cannot overlap other tests, and restores every bound value
        // through the public configuration path instead of leaking a restrictive test ceiling into another suite.
        RuntimeSettings.Bind(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Sandbox:RequireConfinement"] = _original.RequireSandboxConfinement.ToString(),
            ["Sandbox:CgroupRoot"] = _original.AgentCgroupRoot,
            ["Sandbox:AgentMemoryCeilingMb"] = _original.AgentMemoryCeilingMb?.ToString(),
            [RuntimeSettings.MaxAutonomyKey] = _original.MaxAutonomy,
            ["Agents:RunSpoolDirectory"] = _original.AgentRunSpoolDirectory,
            ["Artifacts:StoreDirectory"] = _original.ArtifactStoreDirectory,
            ["Artifacts:LocalRwxShared"] = _original.ArtifactLocalRwxShared.ToString(),
            ["Shutdown:DrainSeconds"] = _original.ShutdownDrainSeconds.ToString(),
            ["Variables:MasterKey"] = _original.VariableMasterKey,
            ["ModelCredentials:OperatorKeys:Anthropic"] = _original.AnthropicOperatorApiKey,
            ["ModelCredentials:OperatorKeys:OpenAI"] = _original.OpenAIOperatorApiKey,
            ["Agents:PackAllowedHosts"] = _original.PackAllowedHosts
        }).Build());
    }
}
