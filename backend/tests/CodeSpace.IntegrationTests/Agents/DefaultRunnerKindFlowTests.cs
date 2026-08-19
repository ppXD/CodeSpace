using Autofac;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Settings;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Enums;
using Microsoft.Extensions.Configuration;
using Shouldly;

namespace CodeSpace.IntegrationTests.Agents;

/// <summary>
/// 🟢 High fidelity: the REAL <see cref="IAgentRunExecutor"/> resolved from the REAL container, so this covers what
/// a hand-built executor cannot — that production DI supplies <see cref="AgentDefaultRunnerSetting"/> to the
/// executor's OPTIONAL constructor parameter. A silently-null setting would leave the deployment key inert and make
/// the <c>agent.run</c> node schema's promise ("empty → the deployment default") false.
///
/// <para>The configured kind names a runner no <c>ISandboxRunner</c> is registered for, which is the observable
/// signal: the registry throws, the executor's catch-all lands the run terminal Failed, and the recorded error
/// names the kind. That also pins the fail-loud posture — an unresolvable configured kind must never fall back to
/// running the agent locally, i.e. somewhere the operator did not choose.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class DefaultRunnerKindFlowTests
{
    private readonly PostgresFixture _fixture;

    public DefaultRunnerKindFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task A_task_pinning_no_runner_dispatches_on_the_configured_deployment_default()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedRunWithNoRunnerKindAsync(teamId);

        using var scope = _fixture.BeginScope(b => b.RegisterInstance(Setting("no-such-runner")).AsSelf().SingleInstance());

        await scope.Resolve<IAgentRunExecutor>().ExecuteAsync(runId, CancellationToken.None);

        var run = await scope.Resolve<IAgentRunService>().GetAsync(runId, CancellationToken.None);

        run.Status.ShouldBe(AgentRunStatus.Failed,
            customMessage: "the configured default names an unregistered runner, so the dispatch must fail the run loudly instead of quietly running it locally");
        run.Error.ShouldContain("no-such-runner",
            customMessage: "the executor must have asked the registry for the CONFIGURED kind — if the setting were not injected it would have asked for 'local' and this run would have failed (or run) elsewhere");
    }

    /// <summary>A run whose task leaves <c>RunnerKind</c> null — the only case the deployment default applies to — on a harness the production registry really has.</summary>
    private async Task<Guid> SeedRunWithNoRunnerKindAsync(Guid teamId)
    {
        using var scope = _fixture.BeginScope();

        var run = await scope.Resolve<IAgentRunService>().CreateAsync(
            new AgentTask { Goal = "default-runner-kind", Harness = "claude-code", Model = "test-model", TimeoutSeconds = 60 },
            teamId, null, null, iterationKey: "", cancellationToken: CancellationToken.None);

        return run.Id;
    }

    private static AgentDefaultRunnerSetting Setting(string kind)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [AgentDefaultRunnerSetting.ConfigurationKey] = kind })
            .Build();

        return new AgentDefaultRunnerSetting(configuration);
    }
}
