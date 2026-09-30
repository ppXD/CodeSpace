using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Agents.ModelCredentials;
using CodeSpace.Core.Services.Review;
using CodeSpace.Core.Services.Workflows.Engine;
using CodeSpace.IntegrationTests.Infrastructure.Jobs;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Commands.Workflows;
using CodeSpace.Messages.Dtos.Workflows;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Failures;
using CodeSpace.Messages.Review;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows;

/// <summary>
/// The allowed model pool and escalation on the production path: a run the ENGINE stages from an <c>agent.run</c> node
/// (persona/model resolution included) and the REAL executor then dispatches, against real Postgres rows. The pool's
/// fall-through order must hold here, not only when the reconciler is called directly.
/// </summary>
public partial class AgentRunExecutorTests
{
    [Fact]
    public async Task An_engine_staged_run_pinned_to_a_row_whose_credential_died_runs_another_pooled_row_and_says_so()
    {
        // Pool [A on cred1, B on cred2], the launch pinned A, cred1 was revoked before dispatch. The engine used to fail
        // the node at staging ("Selected model … is not an active, enabled credentialed model") before any pooled row was
        // tried — no note, no retry, and on plan-map every branch the same. The pool is the operator's fallback.
        if (OperatingSystem.IsWindows()) return;

        var teamId = await SeedTeamAsync();
        var deadCredential = await SeedModelCredentialAsync(teamId, "scripted-provider", "sk-dead-key");
        var pinned = await SeedModelRowAsync(deadCredential, "model-a");
        var other = await SeedModelRowAsync(await SeedModelCredentialAsync(teamId, "scripted-provider", "sk-live-key"), "model-b");
        await RevokeModelCredentialAsync(deadCredential);

        using var manual = ResolveJobClient().ManualExecution();
        var (workflowRunId, agentRunId) = await StagePoolBoundAgentThroughEngineAsync(teamId, pinned, [pinned, other]);

        await ExecuteAsync(agentRunId, new ProjectingScriptedHarness("scripted-provider", "SCRIPTED_MODEL_KEY", "if [ \"$SCRIPTED_MODEL_KEY\" = 'sk-live-key' ]; then echo LIVE_KEY; else echo OTHER_KEY; fi"));
        await RunEngineAsync(workflowRunId);

        using var scope = _fixture.BeginScope();
        var svc = scope.Resolve<IAgentRunService>();
        var run = await svc.GetAsync(agentRunId, CancellationToken.None);
        run.Status.ShouldBe(AgentRunStatus.Succeeded);
        JsonSerializer.Deserialize<AgentTask>(run.TaskJson, AgentJson.Options)!.ModelCredentialModelId.ShouldBe(other, "the run is persisted as the pooled row that ran");

        var events = (await svc.GetEventsAsync(agentRunId, teamId, 0, CancellationToken.None)).ToList();
        events.Select(e => e.Text).ShouldContain("LIVE_KEY", "the REAL child process ran on the other pooled row's key");
        events.ShouldContain(e => e.Kind == AgentEventKind.Warning && e.Text.Contains("model-a") && e.Text.Contains("no longer active") && e.Text.Contains("model-b"),
            customMessage: "the move is named on the run's timeline by its cause");

        (await scope.Resolve<CodeSpaceDbContext>().WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == workflowRunId)).Status.ShouldBe(WorkflowRunStatus.Success);
    }

    [Fact]
    public async Task An_engine_staged_run_whose_pool_has_nothing_runnable_fails_typed_once_and_is_not_respawned()
    {
        if (OperatingSystem.IsWindows()) return;

        var teamId = await SeedTeamAsync();
        var credential = await SeedModelCredentialAsync(teamId, "scripted-provider", "sk-dead-key");
        var pinned = await SeedModelRowAsync(credential, "model-a");
        await RevokeModelCredentialAsync(credential);
        var logger = new RecordingExecutorLogger();

        using var manual = ResolveJobClient().ManualExecution();
        var (workflowRunId, agentRunId) = await StagePoolBoundAgentThroughEngineAsync(teamId, pinned, [pinned]);

        await ExecuteAsync(agentRunId, new ProjectingScriptedHarness("scripted-provider", "SCRIPTED_MODEL_KEY", "echo should-not-run"), logger: logger);
        await RunEngineAsync(workflowRunId);

        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var run = await scope.Resolve<IAgentRunService>().GetAsync(agentRunId, CancellationToken.None);
        run.Status.ShouldBe(AgentRunStatus.Failed);
        JsonSerializer.Deserialize<AgentRunResult>(run.ResultJson!, AgentJson.Options)!.ExitReason.ShouldBe(FailureCodes.AgentModelPoolUnresolvable);

        (await db.AgentRun.AsNoTracking().CountAsync(r => r.WorkflowRunId == workflowRunId)).ShouldBe(1, "the same pool would refuse a respawn the same way — the node's retry budget is not spent on it");
        (await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == workflowRunId)).Status.ShouldBe(WorkflowRunStatus.Failure);

        logger.Entries.ShouldContain(e => e.Level == LogLevel.Warning && e.Exception == null && e.Message.Contains("allowed models"), "an unrunnable pool is the operator's configuration: a Warning naming it, no stack trace");
        logger.Entries.ShouldNotContain(e => e.Level >= LogLevel.Error, "it is not an executor fault");
    }

    [Fact]
    public async Task An_escalated_run_is_persisted_and_reviewed_as_the_row_that_did_the_work()
    {
        // The task started on base-model's row and escalated to strong-model. The stored task used to keep the base row,
        // and the output review's producer identity reads the row first — so the reviewer avoided base-model and could
        // pick strong-model: the model that did the work reviewing itself.
        if (OperatingSystem.IsWindows()) return;

        var teamId = await SeedTeamAsync();
        var credential = await SeedModelCredentialAsync(teamId, "scripted-provider", "sk-key");
        var baseRow = await SeedModelRowAsync(credential, "base-model", ModelCapabilityTier.Basic);
        var strongRow = await SeedModelRowAsync(credential, "strong-model", ModelCapabilityTier.Strong);
        var critic = new ProducerRecordingCritic();

        var runId = await CreateTaskRunAsync(teamId, new AgentTask
        {
            Goal = "scripted", Harness = "scripted-projector", Model = "base-model", ModelCredentialId = credential, ModelCredentialModelId = baseRow, OutputReviewMode = ReviewMode.Gate,
            Escalation = new AgentModelEscalation { Reason = "the prior attempt claimed success but its check failed", From = "base-model" },
        });

        await ExecuteAsync(runId, new ProjectingScriptedHarness("scripted-provider", "SCRIPTED_MODEL_KEY", "echo answered"), critic: critic);

        using var scope = _fixture.BeginScope();
        var run = await scope.Resolve<IAgentRunService>().GetAsync(runId, CancellationToken.None);
        JsonSerializer.Deserialize<AgentRunResult>(run.ResultJson!, AgentJson.Options)!.ModelEscalation!.To.ShouldBe("strong-model", "the run escalated — otherwise this test proves nothing");

        var persisted = JsonSerializer.Deserialize<AgentTask>(run.TaskJson, AgentJson.Options)!;
        persisted.Model.ShouldBe("strong-model");
        persisted.ModelCredentialModelId.ShouldBe(strongRow, "a re-attach reads the stored task — it must name the row that ran");

        critic.ProducerModel.ShouldNotBeNull("the run was gated — if the critic never ran, this test proves nothing");
        critic.ProducerModel!.ModelCredentialModelId.ShouldBe(strongRow);
        (await scope.Resolve<IModelPoolSelector>().SelectReviewerRowIdAsync(teamId, ["scripted-provider"], critic.ProducerModel, CancellationToken.None))
            .ShouldBe(baseRow, "the reviewer avoids the model that did the work — the escalated one");
    }

    /// <summary>manual → agent.run (a pinned row inside an allowed pool, three attempts) → terminal, run by the REAL engine until it stages the agent. Returns the workflow run and the agent run it staged.</summary>
    private async Task<(Guid WorkflowRunId, Guid AgentRunId)> StagePoolBoundAgentThroughEngineAsync(Guid teamId, Guid pinnedRow, IReadOnlyList<Guid> pool)
    {
        var poolJson = string.Join(",", pool.Select(id => $"\"{id}\""));
        var agent = WorkflowsTestSeed.Json($$"""{"goal":"scripted","harness":"scripted-projector","runnerKind":"local","timeoutSeconds":900,"modelCredentialModelId":"{{pinnedRow}}","allowedModelIds":[{{poolJson}}]}""");
        var definition = new WorkflowDefinition
        {
            SchemaVersion = 1,
            Nodes = new List<NodeDefinition>
            {
                new() { Id = "start", TypeKey = "trigger.manual", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
                new() { Id = "agent", TypeKey = "agent.run", Config = agent, Inputs = WorkflowsTestSeed.EmptyJson(), Retry = new RetryPolicy { MaxAttempts = 3, BackoffSeconds = 0 } },
                new() { Id = "end", TypeKey = "builtin.terminal", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
            },
            Edges = new List<EdgeDefinition> { new() { From = "start", To = "agent" }, new() { From = "agent", To = "end" } },
        };

        Guid workflowId;
        using (var scope = await WorkflowsTestSeed.BeginSeedOperatorScopeAsync(_fixture, teamId))
            workflowId = await scope.Resolve<IMediator>().Send(new CreateWorkflowCommand { Name = "pool-" + Guid.NewGuid().ToString("N")[..6], Description = null, Definition = definition, Activations = new List<WorkflowActivationInput>(), Enabled = true });

        var workflowRunId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);
        await RunEngineAsync(workflowRunId);

        using var verify = _fixture.BeginScope();
        var staged = await verify.Resolve<CodeSpaceDbContext>().AgentRun.AsNoTracking().Where(r => r.WorkflowRunId == workflowRunId).Select(r => r.Id).ToListAsync();
        staged.ShouldHaveSingleItem("the engine staged the agent — an unusable pin inside a pool is the dispatch's to resolve, not a staging failure");

        return (workflowRunId, staged[0]);
    }

    private async Task RunEngineAsync(Guid workflowRunId)
    {
        using var scope = _fixture.BeginScope();
        await scope.Resolve<IWorkflowEngine>().ExecuteRunAsync(workflowRunId, CancellationToken.None);
    }

    private InMemoryBackgroundJobClient ResolveJobClient()
    {
        using var scope = _fixture.BeginScope();
        return scope.Resolve<InMemoryBackgroundJobClient>();
    }

    private async Task RevokeModelCredentialAsync(Guid credentialId)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        (await db.ModelCredential.SingleAsync(c => c.Id == credentialId)).Status = CredentialStatus.Revoked;
        await db.SaveChangesAsync();
    }

    /// <summary>Records the producer identity the output review hands the critic — the input the reviewer-model avoidance reads — and approves.</summary>
    private sealed class ProducerRecordingCritic : IStructuredCritic
    {
        public ReviewModelIdentity? ProducerModel { get; private set; }

        public Task<CriticVerdict> ReviewAsync(CriticRequest request, Guid teamId, Guid? reviewerModelId, CancellationToken cancellationToken)
        {
            ProducerModel = request.ProducerModel;
            return Task.FromResult(new CriticVerdict { Mode = request.Mode, Approved = true, Rationale = "recorded" });
        }
    }

    private sealed class RecordingExecutorLogger : ILogger<AgentRunExecutor>
    {
        public List<(LogLevel Level, Exception? Exception, string Message)> Entries { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, exception, formatter(state, exception)));
    }
}
