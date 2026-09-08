using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Tasks;
using CodeSpace.Core.Services.Tasks.Contracts;
using CodeSpace.Core.Services.Tasks.Projection;
using CodeSpace.Core.Services.Completion;
using CodeSpace.Core.Services.Workflows;
using CodeSpace.Core.Services.Workflows.Engine;
using CodeSpace.Core.Services.Workflows.RunSources;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Infrastructure.Jobs;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Contracts;
using CodeSpace.Messages.Dtos.Workflows;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Tasks;
using CodeSpace.Messages.Tasks.Effort;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows;

/// <summary>Real launch, snapshot, read, and replay contract paths over Postgres. No CLI or model is executed or substituted.</summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class TaskLaunchContractFlowTests
{
    private readonly PostgresFixture _fixture;

    public TaskLaunchContractFlowTests(PostgresFixture fixture) => _fixture = fixture;

    [Theory]
    [InlineData(TaskEffortModes.Quick, TaskProjectionKinds.SingleAgent)]
    [InlineData(TaskEffortModes.Standard, TaskProjectionKinds.PlanMapSynth)]
    [InlineData(TaskEffortModes.Deep, TaskProjectionKinds.Supervisor)]
    public async Task Every_launch_lane_records_original_controls_in_the_frozen_run_detail(string effort, string projectionKind)
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        using var scope = _fixture.BeginScope();
        scope.Resolve<InMemoryBackgroundJobClient>().AutoExecute = false;
        var request = new TaskLaunchRequest
        {
            TeamId = teamId, ActorUserId = userId, SurfaceKind = TaskLaunchSurfaceKinds.Chat,
            TaskText = "Preserve the original delivery obligations", RequestedEffort = effort, Autonomy = "Unleashed",
            CapsOverride = new() { MaxCostUsd = 3.25m, AutonomyCeiling = "Standard" },
            AcceptanceCriteria = ["Use original inputs", "Explain limitations"], AcceptanceChecks = ["sh", "verify.sh"],
            DeliverySpec = new() { OpenPullRequest = false, TargetBranch = "review" },
            AllowedModelIds = [], AllowedAgentDefinitionIds = [], RequirePlanConfirmation = false,
            Overrides = new() { Harness = "codex-cli", AllowedTools = ["Read", "Grep"], PushBranch = false, EnableMcp = false },
        };

        var launched = await scope.Resolve<ITaskLaunchService>().LaunchAsync(request, CancellationToken.None);
        var run = await LoadRunAsync(launched.RunId);
        var detail = await scope.Resolve<IWorkflowService>().GetRunAsync(launched.RunId, teamId, CancellationToken.None);
        var contract = detail!.Definition!.LaunchContract;

        contract.ShouldNotBeNull("the production run-detail consumer reads the same persisted contract for all projections");
        contract.Version.ShouldBe(TaskLaunchContract.CurrentVersion);
        contract.Goal.ShouldBe(request.TaskText);
        contract.AcceptanceCriteria.ShouldBe(request.AcceptanceCriteria);
        contract.AcceptanceChecks.ShouldBe(request.AcceptanceChecks);
        contract.DeliverySpec.ShouldBe(request.DeliverySpec);
        contract.RequestedControls!.AllowedModelIds.ShouldNotBeNull();
        contract.RequestedControls.AllowedModelIds.ShouldBeEmpty();
        contract.RequestedControls.AllowedAgentDefinitionIds.ShouldNotBeNull();
        contract.RequestedControls.AllowedAgentDefinitionIds.ShouldBeEmpty();
        contract.RequestedControls.RequirePlanConfirmation.ShouldBe(false);
        contract.RequestedControls.CapsOverride!.MaxCostUsd.ShouldBe(3.25m);
        contract.RequestedControls.Autonomy.ShouldBe("Unleashed");
        contract.RequestedControls.Overrides!.AllowedTools.ShouldBe(new[] { "Read", "Grep" });
        contract.RequestedControls.Overrides.PushBranch.ShouldBe(false);
        contract.ResolvedRoute!.ProjectionKind.ShouldBe(projectionKind);
        contract.ResolvedRoute.EffectiveAutonomy.ShouldBe("Standard");
        if (projectionKind == TaskProjectionKinds.Supervisor)
        {
            contract.SupervisorModelSelection.ShouldNotBeNull("the supervisor decision is frozen before execution and survives the real PostgreSQL snapshot path");
            run.DefinitionSnapshotJson.ShouldContain(contract.SupervisorModelSelection!.ModelCredentialModelId.ToString(), Case.Insensitive);
            contract.SupervisorModelSelection.Mode.ShouldBe(RunModeKeys.Supervisor);
            contract.SupervisorModelSelection.CapabilityKey.ShouldBe(CapabilityKeys.InlineAnswer);
            contract.PlannerModelSelection.ShouldBeNull();
        }
        else if (projectionKind == TaskProjectionKinds.PlanMapSynth)
        {
            contract.PlannerModelSelection.ShouldNotBeNull("the planner decision is frozen before execution and survives the real PostgreSQL snapshot path");
            contract.PlannerModelSelection!.Mode.ShouldBe(RunModeKeys.PlanMap);
            contract.PlannerModelSelection.CapabilityKey.ShouldBe(CapabilityKeys.InlineAnswer);
            contract.SupervisorModelSelection.ShouldBeNull();
        }
        else
        {
            contract.SupervisorModelSelection.ShouldBeNull();
            contract.PlannerModelSelection.ShouldBeNull();
        }
        JsonElement.DeepEquals(JsonSerializer.SerializeToElement(contract.ResolvedRoute.Caps, WorkflowJson.Options), JsonSerializer.SerializeToElement(launched.Route.Caps, WorkflowJson.Options)).ShouldBeTrue();
        DefinitionHash.Compute(detail.Definition).ShouldBe(run.DefinitionSnapshotHash, "Postgres jsonb normalization cannot detach the contract from the frozen hash");
        (await scope.Resolve<CodeSpaceDbContext>().AgentRun.CountAsync(r => r.WorkflowRunId == launched.RunId)).ShouldBe(0, "this verifies persistence, not model execution or control enforcement");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Projection_cannot_replace_or_invent_the_server_launch_stamp(bool hasRecordedIntent)
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var context = Context(teamId);
        var serverContract = hasRecordedIntent ? Contract(teamId) : null;
        context = context with { LaunchContract = serverContract };
        using var scope = _fixture.BeginScope(b => b.RegisterInstance(new TaskProjectionRegistry([new ProvenanceReplacingBuilder()])).As<ITaskProjectionRegistry>());
        scope.Resolve<InMemoryBackgroundJobClient>().AutoExecute = false;

        var handle = await scope.Resolve<ITaskRunSnapshotFactory>().CreateAndRunAsync(context, teamId, userId, null, CancellationToken.None);
        var detail = await scope.Resolve<IWorkflowService>().GetRunAsync(handle.RunId, teamId, CancellationToken.None);

        JsonSerializer.Serialize(detail!.Definition!.LaunchContract, WorkflowJson.Options).ShouldBe(JsonSerializer.Serialize(serverContract, WorkflowJson.Options));
    }

    [Fact]
    public async Task Replay_keeps_the_contract_and_the_original_hash_without_reinterpreting_requests()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var definition = Definition() with { LaunchContract = Contract(teamId) };
        var originalId = await StartAsync(teamId, userId, definition);
        await ExecuteAsync(originalId);
        var original = await LoadRunAsync(originalId);
        original.Status.ShouldBe(WorkflowRunStatus.Success);

        using var scope = _fixture.BeginScope();
        var replayId = await scope.Resolve<IWorkflowService>().ReplayRunAsync(originalId, teamId, userId, CancellationToken.None);
        await ExecuteAsync(replayId);
        var replay = await LoadRunAsync(replayId);
        var detail = await scope.Resolve<IWorkflowService>().GetRunAsync(replayId, teamId, CancellationToken.None);

        replay.Status.ShouldBe(WorkflowRunStatus.Success);
        replay.DefinitionSnapshotJson.ShouldBe(original.DefinitionSnapshotJson);
        replay.DefinitionSnapshotHash.ShouldBe(original.DefinitionSnapshotHash);
        detail!.Definition!.LaunchContract!.Goal.ShouldBe("Complete this metadata-only workflow");
        detail.Definition.LaunchContract.RequestedControls!.RequirePlanConfirmation.ShouldBeNull();
    }

    [Fact]
    public async Task Legacy_snapshot_executes_and_reads_without_fabricated_launch_intent()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await StartAsync(teamId, userId, Definition());
        await ExecuteAsync(runId);
        using var scope = _fixture.BeginScope();
        var detail = await scope.Resolve<IWorkflowService>().GetRunAsync(runId, teamId, CancellationToken.None);

        detail!.Status.ShouldBe(WorkflowRunStatus.Success);
        detail.Definition!.LaunchContract.ShouldBeNull();
        JsonDocument.Parse((await LoadRunAsync(runId)).DefinitionSnapshotJson!).RootElement.TryGetProperty("launchContract", out _).ShouldBeFalse();
    }

    [Fact]
    public async Task Tampering_only_with_the_original_goal_fails_before_any_node_executes()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var definition = Definition() with { LaunchContract = Contract(teamId) };
        var runId = await StartAsync(teamId, userId, definition);
        using (var scope = _fixture.BeginScope())
        {
            var changed = definition with { LaunchContract = definition.LaunchContract! with { Goal = "A weaker goal" } };
            var json = JsonSerializer.Serialize(changed, WorkflowJson.Options);
            await scope.Resolve<CodeSpaceDbContext>().WorkflowRun.Where(r => r.Id == runId).ExecuteUpdateAsync(s => s.SetProperty(r => r.DefinitionSnapshotJson, json));
        }

        await ExecuteAsync(runId);
        var run = await LoadRunAsync(runId);
        run.Status.ShouldBe(WorkflowRunStatus.Failure);
        run.Error.ShouldContain("tamper", Case.Insensitive);
        using var verify = _fixture.BeginScope();
        (await verify.Resolve<CodeSpaceDbContext>().WorkflowRunNode.CountAsync(n => n.RunId == runId)).ShouldBe(0);
    }

    [Fact]
    public async Task Unknown_contract_version_with_a_matching_hash_is_rejected_at_bootstrap()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var definition = Definition() with { LaunchContract = Contract(teamId) };
        var runId = await StartAsync(teamId, userId, definition);
        using (var scope = _fixture.BeginScope())
        {
            // Represents a newer worker's persisted protocol, not a tamper mismatch. This older reader must reject it.
            var future = definition with { LaunchContract = definition.LaunchContract! with { Version = 2 } };
            var json = JsonSerializer.Serialize(future, WorkflowJson.Options);
            var hash = DefinitionHash.Compute(future);
            await scope.Resolve<CodeSpaceDbContext>().WorkflowRun.Where(r => r.Id == runId)
                .ExecuteUpdateAsync(s => s.SetProperty(r => r.DefinitionSnapshotJson, json).SetProperty(r => r.DefinitionSnapshotHash, hash).SetProperty(r => r.ReleaseHashAtRun, hash));
        }

        await ExecuteAsync(runId);
        var run = await LoadRunAsync(runId);
        run.Status.ShouldBe(WorkflowRunStatus.Failure);
        run.Error.ShouldContain("Unsupported launchContract version 2");
        using var verify = _fixture.BeginScope();
        (await verify.Resolve<CodeSpaceDbContext>().WorkflowRunNode.CountAsync(n => n.RunId == runId)).ShouldBe(0);
    }

    [Fact]
    public async Task Authored_workflow_cannot_persist_server_owned_launch_provenance()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        using var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin);
        var definition = Definition() with { LaunchContract = Contract(teamId) };

        var error = await Should.ThrowAsync<WorkflowValidationException>(() => scope.Resolve<IWorkflowService>().CreateAsync(teamId, "forged provenance", null, definition, [], false, CancellationToken.None));

        error.Errors.ShouldContain(e => e.Contains("cannot be authored", StringComparison.Ordinal));
        (await scope.Resolve<CodeSpaceDbContext>().Workflow.CountAsync(w => w.TeamId == teamId)).ShouldBe(0);
    }

    private async Task<Guid> StartAsync(Guid teamId, Guid userId, WorkflowDefinition definition)
    {
        using var scope = _fixture.BeginScope();
        scope.Resolve<InMemoryBackgroundJobClient>().AutoExecute = false;
        return await scope.Resolve<IRunFromSnapshotStarter>().StartFromSnapshotAsync(definition, teamId, userId, "{}", null, null, null, CancellationToken.None);
    }

    private async Task ExecuteAsync(Guid runId)
    {
        using var scope = _fixture.BeginScope();
        await scope.Resolve<IWorkflowEngine>().ExecuteRunAsync(runId, CancellationToken.None);
    }

    private async Task<WorkflowRun> LoadRunAsync(Guid runId)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<CodeSpaceDbContext>().WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId);
    }

    private static TaskBuildContext Context(Guid teamId) => new()
    {
        Seed = new() { TeamId = teamId, Goal = "Complete this metadata-only workflow", SurfaceKind = "chat" },
        Route = new() { ProjectionKind = ProvenanceReplacingBuilder.Kind },
    };

    private static TaskLaunchContract Contract(Guid teamId) => TaskLaunchContractSnapshot.Capture(new TaskLaunchRequest { TeamId = teamId, ActorUserId = Guid.NewGuid(), SurfaceKind = "chat" }, Context(teamId));

    private static WorkflowDefinition Definition() => new()
    {
        Nodes = [new() { Id = "start", TypeKey = "trigger.manual", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() }, new() { Id = "end", TypeKey = "builtin.terminal", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() }],
        Edges = [new() { From = "start", To = "end" }],
    };

    private sealed class ProvenanceReplacingBuilder : IWorkflowDefinitionBuilder
    {
        public const string Kind = "contract-fixture";
        public string ProjectionKind => Kind;
        public WorkflowDefinition Build(TaskBuildContext context) => Definition() with { LaunchContract = new() { Version = 999, Goal = "The builder invented this" } };
    }
}
