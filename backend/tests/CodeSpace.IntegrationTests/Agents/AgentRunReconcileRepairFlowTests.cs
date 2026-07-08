using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Agents.ModelCredentials;
using CodeSpace.Core.Services.Agents.Sandbox;
using CodeSpace.Core.Services.Agents.Workspace;
using CodeSpace.Core.Services.Credentials;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Enums;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.Agents;

/// <summary>
/// The 兜底 end-to-end through the REAL <see cref="AgentRunExecutor"/>: an agent whose authored harness can't drive its
/// pinned credential's provider is REPAIRED to a harness that can — and that repaired harness ACTUALLY runs (its
/// process executes, its credential resolves + projects, the run succeeds) instead of failing with "provider this
/// harness cannot drive". Proves the executor's repair branch: the reconciled harness invocation, the repair event,
/// and the corrected <c>AgentRun.Harness</c> column.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class AgentRunReconcileRepairFlowTests
{
    private readonly PostgresFixture _fixture;

    public AgentRunReconcileRepairFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task An_incompatible_authored_harness_is_repaired_and_the_compatible_harness_actually_runs()
    {
        if (OperatingSystem.IsWindows()) return;   // the probe harness runs /bin/sh

        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var credentialId = await SeedCredentialAsync(teamId, "ProviderB");
        var runId = await CreateRunAsync(teamId, authoredHarness: "harness-a", credentialId);

        // Two harnesses: the AUTHORED one drives ProviderA only; the OTHER drives ProviderB (the pinned credential).
        var harnessA = new ProbeHarness("harness-a", "ProviderA", "A-RAN");
        var harnessB = new ProbeHarness("harness-b", "ProviderB", "B-RAN");

        using (var scope = _fixture.BeginScope())
        {
            var registry = new AgentHarnessRegistry(new IAgentHarness[] { harnessA, harnessB });
            var db = scope.Resolve<CodeSpaceDbContext>();
            var executor = new AgentRunExecutor(
                scope.Resolve<IAgentRunService>(), registry, new HarnessModelReconciler(registry, scope.Resolve<IModelPoolSelector>(), db),
                scope.Resolve<ISandboxRunnerRegistry>(), scope.Resolve<IAgentWorkspaceResolver>(),
                scope.Resolve<IModelCredentialResolver>(), scope.Resolve<IWorkspaceProviderRegistry>(),
                scope.Resolve<IAgentRunCompletionNotifier>(), scope.Resolve<IServiceScopeFactory>(), db,
                scope.Resolve<CodeSpace.Core.Services.Review.IStructuredCritic>(),
                scope.Resolve<CodeSpace.Core.Services.Workflows.Artifacts.IArtifactOffloader>(),
                scope.Resolve<CodeSpace.Core.Services.Agents.Publish.IPublishManifestStore>(),
                scope.Resolve<IEnumerable<CodeSpace.Core.Services.Agents.Publish.IPublishGuard>>(),
                NullLogger<AgentRunExecutor>.Instance);

            await executor.ExecuteAsync(runId, CancellationToken.None);
        }

        using var verify = _fixture.BeginScope();
        var verifyDb = verify.Resolve<CodeSpaceDbContext>();

        var run = await verifyDb.AgentRun.AsNoTracking().SingleAsync(r => r.Id == runId);
        run.Status.ShouldBe(AgentRunStatus.Succeeded, "the repair let the agent run instead of failing on 'cannot drive'");
        run.Harness.ShouldBe("harness-b", "the stored harness reflects the harness that ACTUALLY ran, not the impossible authored one");

        var events = await verifyDb.AgentRunEvent.AsNoTracking().Where(e => e.AgentRunId == runId).ToListAsync();
        events.ShouldContain(e => e.Kind == AgentEventKind.Warning && e.Text.Contains("reconciled to 'harness-b'"), "the timeline records the auto-repair");
        events.ShouldContain(e => e.Text.Contains("B-RAN"), "the COMPATIBLE harness's process ran (not the authored one)");
        events.ShouldNotContain(e => e.Text.Contains("A-RAN"), "the incompatible authored harness never ran");
    }

    private async Task<Guid> SeedCredentialAsync(Guid teamId, string provider)
    {
        var id = Guid.NewGuid();

        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        db.ModelCredential.Add(new ModelCredential
        {
            Id = id, TeamId = teamId, Provider = provider, DisplayName = provider + " cred",
            EncryptedApiKey = scope.Resolve<IPayloadEncryptor>().Encrypt("k"), Status = CredentialStatus.Active,
        });

        await db.SaveChangesAsync();
        return id;
    }

    private async Task<Guid> CreateRunAsync(Guid teamId, string authoredHarness, Guid credentialId)
    {
        using var scope = _fixture.BeginScope();
        var run = await scope.Resolve<IAgentRunService>().CreateAsync(
            new AgentTask { Goal = "g", Harness = authoredHarness, Model = "test-model", ModelCredentialId = credentialId },
            teamId, null, null, iterationKey: "", cancellationToken: CancellationToken.None);
        return run.Id;
    }

    /// <summary>A projecting harness with a given kind + the single provider it drives; its process prints a marker so a test can prove WHICH harness actually ran.</summary>
    private sealed class ProbeHarness : IAgentHarness, IModelCredentialProjector
    {
        private readonly string _provider;
        private readonly string _marker;

        public ProbeHarness(string kind, string provider, string marker) { Kind = kind; _provider = provider; _marker = marker; }

        public string Kind { get; }
        public string Version => "test";
        public IReadOnlyList<string> Models { get; } = new[] { "test-model" };
        public IReadOnlyList<string> SupportedProviders => new[] { _provider };

        public SandboxSpec BuildInvocation(AgentTask task) =>
            new() { Command = "/bin/sh", Args = new[] { "-c", $"printf '{_marker}\\n'" }, WorkingDirectory = task.WorkspaceDirectory, Environment = task.Environment, TimeoutSeconds = task.TimeoutSeconds };

        public IReadOnlyList<AgentEvent> ParseEvents(string rawLine) =>
            string.IsNullOrWhiteSpace(rawLine) ? Array.Empty<AgentEvent>() : new[] { new AgentEvent { Kind = AgentEventKind.AssistantMessage, Text = rawLine.Trim() } };

        public AgentRunResult BuildResult(IReadOnlyList<AgentEvent> events, int exitCode) =>
            exitCode == 0
                ? new AgentRunResult { Status = AgentRunStatus.Succeeded, ExitReason = "completed", Summary = events.Count > 0 ? events[^1].Text : null }
                : new AgentRunResult { Status = AgentRunStatus.Failed, ExitReason = "non-zero-exit", Error = $"exit {exitCode}" };

        public IReadOnlyDictionary<string, string> ProjectToEnv(ResolvedModelCredential credential) =>
            new Dictionary<string, string> { ["PROBE_KEY"] = credential.ApiKey ?? "" };
    }
}
