using System.Text.Json;
using CodeSpace.Core.Services.Agents.Commands;
using CodeSpace.Core.Services.Agents.Sandbox;
using CodeSpace.Core.Services.Agents.Sandbox.Runners;
using CodeSpace.Core.Services.Workflows.Nodes;
using CodeSpace.Core.Services.Workflows.Nodes.Builtin;
using CodeSpace.Core.Settings;
using CodeSpace.Messages.Agents;
using Microsoft.Extensions.Configuration;
using Shouldly;

namespace CodeSpace.UnitTests.Agents;

/// <summary>
/// The deployment-default sandbox runner kind: the one shared constant every caller resolves by, the
/// configuration key that overrides it, and every operator-facing node schema that promises the override exists.
/// </summary>
[Trait("Category", "Unit")]
public class DefaultRunnerKindTests
{
    [Fact]
    public void The_local_runner_kind_is_the_literal_local()
    {
        // A LITERAL pin, not a tautology: every persisted runner_kind value and every authored node config
        // already carries this exact string, so changing it is a data migration, not a refactor.
        SandboxKinds.Local.ShouldBe("local");
        LocalProcessRunner.LocalKind.ShouldBe(SandboxKinds.Local, "the runner's own key must stay the shared constant, not a second literal");
    }

    [Fact]
    public void The_default_runner_configuration_key_is_pinned()
    {
        // Rule 8: an operator who selected an alternative runner through this key reverts to local, silently, if
        // the key is renamed. The rename has to be a visible decision.
        AgentDefaultRunnerSetting.ConfigurationKey.ShouldBe("Agents:DefaultRunnerKind");
    }

    [Theory]
    [InlineData(null, "local")]      // unconfigured — the behaviour every call site hard-coded before the key existed
    [InlineData("", "local")]        // a cleared ConfigMap entry means "not set", never an empty kind no registry resolves
    [InlineData("   ", "local")]
    [InlineData("docker", "docker")] // the override an operator who registered their own runner sets
    [InlineData("  docker  ", "docker")]
    public void Resolves_the_configured_kind_and_falls_back_to_local(string? raw, string expected)
    {
        AgentDefaultRunnerSetting.Resolve(raw).ShouldBe(expected);
    }

    [Fact]
    public void The_configured_kind_arrives_through_the_environment_form_of_the_key()
    {
        // The doc-comment tells operators to set Agents__DefaultRunnerKind; this proves that env name actually
        // reaches the setting through the standard configuration pipeline, rather than only the appsettings path.
        const string environmentName = "Agents__DefaultRunnerKind";
        var original = Environment.GetEnvironmentVariable(environmentName);
        Environment.SetEnvironmentVariable(environmentName, "docker");

        try
        {
            var configuration = new ConfigurationBuilder().AddEnvironmentVariables().Build();

            new AgentDefaultRunnerSetting(configuration).Value.ShouldBe("docker");
        }
        finally
        {
            Environment.SetEnvironmentVariable(environmentName, original);
        }
    }

    [Theory]
    [InlineData(null, "cfg-runner")]        // request pins none → the deployment default
    [InlineData("pinned-runner", "pinned-runner")]   // an explicit request kind always wins over the default
    public async Task RunCommandService_resolves_the_deployment_default_only_when_the_request_pins_none(string? requestKind, string expectedKind)
    {
        var runners = new RecordingRunnerRegistry();
        var service = new RunCommandService(null!, null!, runners, null!, Setting("cfg-runner"));

        // Ephemeral (no repositoryId) so the DbContext / auth / workspace collaborators are never touched.
        await service.RunAsync(new RunCommandRequest { Command = "true", RunnerKind = requestKind }, CancellationToken.None);

        runners.Requested.ShouldBe(expectedKind);
    }

    [Theory]
    [InlineData("agent.run")]
    [InlineData("agent.run_command")]
    [InlineData("agent.supervisor")]
    public void Every_runnerKind_description_names_the_key_that_sets_the_default(string typeKey)
    {
        var descriptions = RunnerKindDescriptions(typeKey).ToList();

        descriptions.ShouldNotBeEmpty($"node '{typeKey}' is expected to expose a runnerKind property");

        foreach (var description in descriptions)
            description.ShouldContain(AgentDefaultRunnerSetting.ConfigurationKey,
                customMessage: $"node '{typeKey}' tells the operator an empty runnerKind lands on a deployment default, so the description must name the configuration key that sets it — otherwise the promise is unactionable.");
    }

    private static AgentDefaultRunnerSetting Setting(string kind)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [AgentDefaultRunnerSetting.ConfigurationKey] = kind })
            .Build();

        return new AgentDefaultRunnerSetting(configuration);
    }

    /// <summary>Every <c>runnerKind</c> description the editor renders for this node, from anywhere in either schema — agent.run declares it at the top of its config, agent.run_command on its input, agent.supervisor inside a nested per-agent object.</summary>
    private static IEnumerable<string> RunnerKindDescriptions(string typeKey)
    {
        INodeRuntime node = typeKey switch
        {
            "agent.run" => new AgentCodeNode(),
            "agent.run_command" => new AgentRunCommandNode(null!, null!),
            _ => new AgentSupervisorNode(null!),
        };

        return Descriptions(node.Manifest.ConfigSchema).Concat(Descriptions(node.Manifest.InputSchema));
    }

    /// <summary>Walks the whole schema (properties nest arbitrarily deep) and yields the description of every property named <c>runnerKind</c>.</summary>
    private static IEnumerable<string> Descriptions(JsonElement schema)
    {
        if (schema.ValueKind == JsonValueKind.Array)
        {
            foreach (var description in schema.EnumerateArray().SelectMany(Descriptions)) yield return description;

            yield break;
        }

        if (schema.ValueKind != JsonValueKind.Object) yield break;

        foreach (var property in schema.EnumerateObject())
        {
            if (property.Name == "runnerKind" && property.Value.ValueKind == JsonValueKind.Object && property.Value.TryGetProperty("description", out var own))
                yield return own.GetString()!;

            foreach (var description in Descriptions(property.Value)) yield return description;
        }
    }

    /// <summary>Records the kind the service asked for and hands back a runner that reports success without touching the OS.</summary>
    private sealed class RecordingRunnerRegistry : ISandboxRunnerRegistry
    {
        public string? Requested { get; private set; }

        public IReadOnlyList<ISandboxRunner> All => new[] { (ISandboxRunner)new NoopRunner() };

        public ISandboxRunner Resolve(string kind)
        {
            Requested = kind;
            return new NoopRunner();
        }
    }

    private sealed class NoopRunner : ISandboxRunner
    {
        public string Kind => "noop";

        public Task<SandboxResult> RunAsync(SandboxSpec spec, CancellationToken cancellationToken) =>
            Task.FromResult(new SandboxResult { Status = SandboxStatus.Success, ExitCode = 0, Stdout = "", Stderr = "" });
    }
}
