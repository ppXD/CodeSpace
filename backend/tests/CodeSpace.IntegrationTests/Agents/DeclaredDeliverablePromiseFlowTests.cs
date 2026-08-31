using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Agents.Harnesses;
using CodeSpace.Core.Services.Agents.ModelCredentials;
using CodeSpace.Core.Services.Agents.Publish;
using CodeSpace.Core.Services.Agents.Sandbox;
using CodeSpace.Core.Services.Agents.Workspace;
using CodeSpace.Core.Services.Workflows.Llm;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Agents.Benchmark;
using CodeSpace.Messages.Commands.Workflows;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Contracts;
using CodeSpace.Messages.Dtos.Workflows;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using System.Text.Json;

namespace CodeSpace.IntegrationTests.Agents;

/// <summary>
/// 🟢 Integration (real Postgres + real executor + real OS process + real filesystem): the capture promise now states
/// WHAT WAS OWED, so a shortfall has something to be short of. A run declaring three deliverables, one of them past
/// the per-file capture cap, captures two — and both halves of that fact are durable: the promise carries the declared
/// list, its facts carry the counts, and the bytes nobody took are a <c>BoundExceeded</c> span on the completeness
/// plane rather than a silence.
///
/// <para>The invariant under test alongside them is what the bound must NOT do: the deliverable EXISTS, so the
/// acceptance oracle passes and the run lands Succeeded. A capture bound is an accounting fact about the record, never
/// an execution authority over work that was actually done.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class DeclaredDeliverablePromiseFlowTests
{
    /// <summary>One byte past the store's per-file cap — the smallest deliverable that is honestly over it.</summary>
    private const long OverCapBytes = ArtifactManifestStore.MaxArtifactBytes + 1;

    private readonly PostgresFixture _fixture;

    public DeclaredDeliverablePromiseFlowTests(PostgresFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task A_capture_that_fell_short_of_what_was_declared_is_visible()
    {
        if (OperatingSystem.IsWindows()) return;   // the scripted harness is a /bin/sh invocation

        var world = await RunDeclaringAnOverCapDeliverableAsync();

        using var scope = _fixture.BeginScope();
        var promise = await scope.Resolve<CodeSpaceDbContext>().CaptureIntent.AsNoTracking()
            .SingleAsync(intent => intent.AgentRunId == world.AgentRunId);

        promise.ExpectationsJson.ShouldNotBeNull("a promise that states nothing about what was owed cannot be fallen short of");
        var declared = JsonDocument.Parse(promise.ExpectationsJson!).RootElement.GetProperty("deliverables")
            .EnumerateArray().Select(path => path.GetString()).ToArray();
        declared.ShouldBe(new[] { "report.md", "notes.md", "big.csv" }, "the promise carries the run's own declared deliverable list");

        var facts = JsonDocument.Parse(promise.FactsJson!).RootElement;
        facts.GetProperty("declaredDeliverables").GetInt32().ShouldBe(3);
        facts.GetProperty("typedArtifacts").GetInt32().ShouldBe(2, "the over-cap deliverable's bytes were never taken");
        facts.GetProperty("empty").GetBoolean().ShouldBeFalse();
    }

    [Fact]
    public async Task An_over_cap_deliverable_is_a_bound_exceeded_gap_and_the_run_still_succeeds()
    {
        if (OperatingSystem.IsWindows()) return;

        var world = await RunDeclaringAnOverCapDeliverableAsync();

        using var scope = _fixture.BeginScope();
        var run = await scope.Resolve<IAgentRunService>().GetAsync(world.AgentRunId, CancellationToken.None);

        run.Status.ShouldBe(AgentRunStatus.Succeeded,
            $"the deliverable EXISTS, so the oracle passes — a capture bound must never re-grade work that was done. Error: {run.ResultJson}");

        var gap = (await scope.Resolve<CodeSpaceDbContext>().WorkflowRunCaptureGap.AsNoTracking()
                .Where(candidate => candidate.WorkflowRunId == world.WorkflowRunId && candidate.SubjectKind == WorkflowRunDataOwnerKinds.Deliverable)
                .ToListAsync())
            .ShouldHaveSingleItem("exactly the one deliverable past the cap is a known-missing span; the two captured ones are not");

        gap.SubjectId.ShouldBe("big.csv");
        gap.Reason.ShouldBe(CaptureGapReason.BoundExceeded, "the bytes past the cut were never taken from the source — that is what this reason means");
        gap.Resolution.ShouldBe(CaptureGapResolution.Open);
        gap.ReasonDetail.ShouldNotBeNullOrWhiteSpace("a human reading one row must learn WHICH bound stopped the capture");

        var manifests = await scope.Resolve<IArtifactManifestStore>().ListForAgentRunAsync(world.AgentRunId, world.TeamId, CancellationToken.None);
        manifests.Select(manifest => manifest.LogicalPath).OrderBy(path => path, StringComparer.Ordinal).ToArray()
            .ShouldBe(new[] { "notes.md", "report.md" }, customMessage: "an over-cap deliverable is SKIPPED, never clipped into a manifest row that lies about its bytes");
    }

    // ─── The world: a workflow-bound agent run whose acceptance declares three deliverables, one past the cap ───

    private async Task<RunWorld> RunDeclaringAnOverCapDeliverableAsync()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowRunId = await SeedWorkflowRunAsync(teamId, userId);
        var agentRunId = await CreateRunAsync(teamId, workflowRunId);

        await ExecuteAsync(agentRunId, new ScriptedHarness(
            $"printf '# findings\\n' > report.md; printf 'notes\\n' > notes.md; head -c {OverCapBytes} /dev/zero > big.csv"));

        return new RunWorld(teamId, workflowRunId, agentRunId);
    }

    private async Task<Guid> SeedWorkflowRunAsync(Guid teamId, Guid userId)
    {
        Guid workflowId;
        using (var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin))
        {
            workflowId = await scope.Resolve<MediatR.IMediator>().Send(new CreateWorkflowCommand
            {
                Name = "deliverable-promise-" + Guid.NewGuid().ToString("N")[..8],
                Definition = WorkflowsTestSeed.MinimalDefinition(),
                Activations = new List<WorkflowActivationInput>(),
                Enabled = true,
            });
        }

        return await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);
    }

    private async Task<Guid> CreateRunAsync(Guid teamId, Guid workflowRunId)
    {
        using var scope = _fixture.BeginScope();
        var run = await scope.Resolve<IAgentRunService>().CreateAsync(
            new AgentTask
            {
                Goal = "write the findings report", Harness = "scripted", Model = "test-model",
                Acceptance = new SupervisorAcceptanceSpec { Command = new[] { "report.md", "notes.md", "big.csv" }, Kind = BenchmarkGradingKind.ArtifactPresent },
            },
            teamId, workflowRunId, "agent", iterationKey: "", cancellationToken: CancellationToken.None);
        return run.Id;
    }

    private async Task ExecuteAsync(Guid runId, IAgentHarness harness)
    {
        using var scope = _fixture.BeginScope();
        var executor = new AgentRunExecutor(
            scope.Resolve<IAgentRunService>(),
            new AgentHarnessRegistry(new[] { harness }),
            new HarnessModelReconciler(new AgentHarnessRegistry(new[] { harness }), scope.Resolve<IModelPoolSelector>(), scope.Resolve<CodeSpaceDbContext>()),
            scope.Resolve<ISandboxRunnerRegistry>(),
            scope.Resolve<IAgentWorkspaceResolver>(),
            scope.Resolve<IModelCredentialResolver>(),
            scope.Resolve<IWorkspaceProviderRegistry>(),
            scope.Resolve<IAgentRunCompletionNotifier>(),
            scope.Resolve<IServiceScopeFactory>(),
            scope.Resolve<CodeSpaceDbContext>(),
            scope.Resolve<Core.Services.Review.IStructuredCritic>(),
            scope.Resolve<Core.Services.Workflows.Artifacts.IArtifactOffloader>(),
            scope.Resolve<Core.Services.Workflows.Artifacts.IArtifactStore>(),
            scope.Resolve<IPublishManifestStore>(), scope.Resolve<IArtifactManifestStore>(), scope.Resolve<Core.Services.Agents.Capture.ICaptureIntentService>(),
            scope.Resolve<IEnumerable<IPublishGuard>>(),
            NullLogger<AgentRunExecutor>.Instance);

        await executor.ExecuteAsync(runId, CancellationToken.None);
    }

    private sealed record RunWorld(Guid TeamId, Guid WorkflowRunId, Guid AgentRunId);

    private sealed class ScriptedHarness : IAgentHarness
    {
        private readonly string _script;

        public ScriptedHarness(string script) => _script = script;

        public string Kind => "scripted";
        public string Version => "test";
        public IReadOnlyList<string> Models { get; } = new[] { "test-model" };

        public SandboxSpec BuildInvocation(AgentTask task) => new() { Command = "/bin/sh", Args = new[] { "-c", _script }, WorkingDirectory = task.WorkspaceDirectory, TimeoutSeconds = task.TimeoutSeconds };

        public IReadOnlyList<AgentEvent> ParseEvents(string rawLine) =>
            string.IsNullOrWhiteSpace(rawLine) ? Array.Empty<AgentEvent>() : new[] { new AgentEvent { Kind = AgentEventKind.AssistantMessage, Text = rawLine.Trim() } };

        public IAgentEventFolder CreateFolder() => new TestEventFolder((fold, exitCode) =>
            exitCode == 0
                ? new AgentRunResult { Status = AgentRunStatus.Succeeded, ExitReason = "completed", Summary = fold.LastText }
                : new AgentRunResult { Status = AgentRunStatus.Failed, ExitReason = "non-zero-exit", Error = $"exit {exitCode}" });
    }
}
