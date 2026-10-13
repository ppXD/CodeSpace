using System.Text.Json;
using Autofac;
using CodeSpace.Core.Authorization;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Fleet;
using CodeSpace.Core.Services.Variables;
using CodeSpace.Core.Services.Workflows;
using CodeSpace.Core.Services.Workflows.Engine;
using CodeSpace.Core.Services.Workflows.Runtime;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Infrastructure.Jobs;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Commands.Workflows;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Contracts;
using CodeSpace.Messages.Dtos.Workflows;
using CodeSpace.Messages.Enums;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.Fleet;

/// <summary>
/// Sealed hand-offs (0247) during a rolling worker deploy and before a worker rollback, against real Postgres and a real
/// Hangfire server table: while a previous-version worker is live, the engine stages a <c>flow.subworkflow</c> child's
/// payload and an <c>agent.run</c> task in the exact shape the previous build wrote — and the previous build's read of
/// those rows (deserialize the public column, nothing else; quoted from <c>a9cf96d35^</c> at each assertion) gets the
/// real secret, not the mask. Once every live worker advertises the capability, the same staging seals. A replay or
/// rerun fork of a sealed child is a new row too, written in the shape every worker that may run it reads. The admin
/// unseal restores the sealed rows a previous-version worker could still execute to that same shape and leaves terminal
/// rows and launched agent runs sealed.
///
/// <para>Real engine, real fleet registry over Hangfire's own <c>AnnounceServer</c> rows, real sealer and Data
/// Protection; the previous build's read path is an inline mirror of its two lines (the scripted two-build run in the
/// PR exercises the previous build's binary itself).</para>
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class RollingWorkerPayloadSealingFlowTests
{
    private readonly PostgresFixture _fixture;

    public RollingWorkerPayloadSealingFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task While_a_previous_version_worker_is_live_the_handoffs_it_may_execute_are_written_exact()
    {
        using var fleet = new HangfireFleetHarness(_fixture.ConnectionString);
        fleet.AnnounceCurrentWorker();
        fleet.AnnouncePreviousVersionWorker();
        var team = await SeedTeamWithSecretAsync();
        using var manual = ResolveJobClient().ManualExecution();

        var (childRunId, agentRunId) = await StageHandoffsAsync(fleet, team);

        var request = await LoadRunRequestAsync(childRunId);
        request.SealedPayloadCiphertext.ShouldBeNull("a previous-version worker never reads the sealed column");
        PreviousBuildTrigger(request).GetProperty("token").GetString().ShouldBe(team.Secret, "the previous build builds the child's trigger scope from normalized_payload_json alone");

        var agentRun = await LoadAgentRunAsync(agentRunId);
        agentRun.SealedTaskCiphertext.ShouldBeNull();
        PreviousBuildTask(agentRun).Goal.ShouldContain(team.Secret, customMessage: "the previous build's executor launches whatever task_jsonb deserializes to");

        await RunEngineAsync(fleet, childRunId);
        (await LoadRunAsync(childRunId)).Status.ShouldBe(WorkflowRunStatus.Success, "a current worker executes the unsealed row as well — it is every pre-0247 row's shape");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Once_every_live_worker_reads_sealed_payloads_the_handoffs_are_sealed(bool previousVersionWorkerStoppedHeartbeating)
    {
        using var fleet = new HangfireFleetHarness(_fixture.ConnectionString);
        fleet.AnnounceCurrentWorker();
        if (previousVersionWorkerStoppedHeartbeating) fleet.AgeHeartbeats(fleet.AnnouncePreviousVersionWorker(), FleetCapabilityRules.LiveHeartbeatWindow + TimeSpan.FromMinutes(1));
        var team = await SeedTeamWithSecretAsync();
        using var manual = ResolveJobClient().ManualExecution();

        var (childRunId, agentRunId) = await StageHandoffsAsync(fleet, team);

        var request = await LoadRunRequestAsync(childRunId);
        request.SealedPayloadCiphertext.ShouldNotBeNull(previousVersionWorkerStoppedHeartbeating ? "a killed worker's row lingers until Hangfire's watchdog removes it, but it holds nothing back once its heartbeat is stale" : "an all-current fleet seals");
        request.NormalizedPayloadJson.ShouldNotContain(team.Secret);

        var agentRun = await LoadAgentRunAsync(agentRunId);
        agentRun.SealedTaskCiphertext.ShouldNotBeNull();
        agentRun.TaskJson.ShouldNotContain(team.Secret);
    }

    public enum Fork { Replay, RerunFromNode }

    /// <summary>The fleet the API pod sees when it stages the fork: all current, a previous-version worker live beside them (a rolling deploy, a worker rollback), or no live worker at all (every current worker stopped for a rollback, the previous ones not started yet).</summary>
    public enum FleetAtFork { AllCurrent, PreviousVersionWorkerLive, NoWorkerLive }

    [Theory]
    [InlineData(Fork.Replay, FleetAtFork.AllCurrent)]
    [InlineData(Fork.Replay, FleetAtFork.PreviousVersionWorkerLive)]
    [InlineData(Fork.Replay, FleetAtFork.NoWorkerLive)]
    [InlineData(Fork.RerunFromNode, FleetAtFork.AllCurrent)]
    [InlineData(Fork.RerunFromNode, FleetAtFork.PreviousVersionWorkerLive)]
    [InlineData(Fork.RerunFromNode, FleetAtFork.NoWorkerLive)]
    public async Task A_fork_of_a_sealed_child_is_written_in_the_shape_every_worker_that_may_run_it_reads(Fork fork, FleetAtFork fleetAtFork)
    {
        // A finished child stays sealed through the pre-rollback unseal, and the API that forks it stays current while
        // the workers roll back — so the fork is a new row a previous-version worker may execute.
        using var fleet = new HangfireFleetHarness(_fixture.ConnectionString);
        var currentWorker = fleet.AnnounceCurrentWorker();
        var team = await SeedTeamWithSecretAsync();
        using var manual = ResolveJobClient().ManualExecution();
        var (childRunId, _) = await StageHandoffsAsync(fleet, team);
        await RunEngineAsync(fleet, childRunId);
        (await LoadRunRequestAsync(childRunId)).SealedPayloadCiphertext.ShouldNotBeNull("precondition: the finished child was sealed under an all-current fleet");
        if (fleetAtFork == FleetAtFork.PreviousVersionWorkerLive) fleet.AnnouncePreviousVersionWorker();
        if (fleetAtFork == FleetAtFork.NoWorkerLive) fleet.AgeHeartbeats(currentWorker, FleetCapabilityRules.LiveHeartbeatWindow + TimeSpan.FromMinutes(1));

        var forkRunId = await ForkAsync(fleet, team, childRunId, fork);

        var request = await LoadRunRequestAsync(forkRunId);
        if (fleetAtFork == FleetAtFork.AllCurrent)
        {
            request.SealedPayloadCiphertext.ShouldNotBeNull("an all-current fleet keeps the fork sealed");
            request.NormalizedPayloadJson.ShouldNotContain(team.Secret);
        }
        else
        {
            request.SealedPayloadCiphertext.ShouldBeNull("the worker that runs the fork may be a previous-version one, which never reads the sealed column");
            PreviousBuildTrigger(request).GetProperty("token").GetString().ShouldBe(team.Secret, "the previous build builds the fork's trigger scope from normalized_payload_json alone");
        }

        await RunEngineAsync(fleet, forkRunId);
        (await LoadRunAsync(forkRunId)).Status.ShouldBe(WorkflowRunStatus.Success, "a current worker executes the fork in either shape");
    }

    [Fact]
    public void The_fleet_registry_reads_the_server_table_with_utc_heartbeats()
    {
        using var fleet = new HangfireFleetHarness(_fixture.ConnectionString);
        var serverId = fleet.Announce("codespace-control-probe", ["default", FleetCapabilityMarkers.SealedPayloadV1]);

        var registry = new HangfireWorkerFleetRegistry(fleet.Storage);
        registry.ObservesFleet.ShouldBeTrue("every deployed role registers job storage, so an empty server table there is a fleet with no live worker, not a host with none");
        var server = registry.ReadServers().ShouldHaveSingleItem();

        server.ServerId.ShouldBe(serverId);
        server.Queues.ShouldBe(["default", FleetCapabilityMarkers.SealedPayloadV1]);
        (DateTime.UtcNow - server.LastHeartbeatUtc!.Value).Duration().ShouldBeLessThan(TimeSpan.FromMinutes(1),
            "a heartbeat read in the wrong zone would age every server by the host's UTC offset and hide a live previous-version worker");
    }

    [Fact]
    public async Task Unsealing_restores_every_non_terminal_sealed_row_and_leaves_terminal_ones_sealed()
    {
        using var fleet = new HangfireFleetHarness(_fixture.ConnectionString);
        fleet.AnnounceCurrentWorker();
        var team = await SeedTeamWithSecretAsync();
        using var manual = ResolveJobClient().ManualExecution();
        var (liveChildRunId, liveAgentRunId) = await StageHandoffsAsync(fleet, team);
        var (finishedChildRunId, finishedAgentRunId) = await StageHandoffsAsync(fleet, team);
        await RunEngineAsync(fleet, finishedChildRunId);
        await SetAgentRunStatusAsync(finishedAgentRunId, AgentRunStatus.Cancelled);

        var response = await UnsealAsAdminAsync();

        response.AgentTasks.ShouldBeGreaterThanOrEqualTo(1);
        response.RunPayloads.ShouldBeGreaterThanOrEqualTo(1);

        var liveRequest = await LoadRunRequestAsync(liveChildRunId);
        liveRequest.SealedPayloadCiphertext.ShouldBeNull();
        PreviousBuildTrigger(liveRequest).GetProperty("token").GetString().ShouldBe(team.Secret, "the pending child must start on a previous-version worker with the real value");

        var liveAgent = await LoadAgentRunAsync(liveAgentRunId);
        liveAgent.SealedTaskCiphertext.ShouldBeNull();
        PreviousBuildTask(liveAgent).Goal.ShouldContain(team.Secret, customMessage: "the queued agent must launch on a previous-version worker with the real goal");

        (await LoadRunRequestAsync(finishedChildRunId)).SealedPayloadCiphertext.ShouldNotBeNull("a finished child executes nothing again, so its secret stays sealed");
        (await LoadAgentRunAsync(finishedAgentRunId)).SealedTaskCiphertext.ShouldNotBeNull("a cancelled agent run executes nothing again");

        await UnsealAsAdminAsync();

        (await LoadAgentRunAsync(liveAgentRunId)).TaskJson.ShouldBe(liveAgent.TaskJson, "a second send finds nothing left to restore");
        (await LoadRunRequestAsync(liveChildRunId)).NormalizedPayloadJson.ShouldBe(liveRequest.NormalizedPayloadJson);
    }

    [Fact]
    public async Task Unsealing_leaves_an_agent_run_that_already_launched_sealed()
    {
        // A previous-version worker never launches a Running row — Running is reached only from Queued — it only
        // re-attaches one, and its re-attach cannot rebuild the launch's redactor in either shape. A current worker can,
        // from the sealed copy alone: unsealed, the row would drop the secrets folded into the handle's fingerprint, and
        // a re-attach after an aborted rollback would land the run marker-only, losing its tail.
        using var fleet = new HangfireFleetHarness(_fixture.ConnectionString);
        fleet.AnnounceCurrentWorker();
        var team = await SeedTeamWithSecretAsync();
        using var manual = ResolveJobClient().ManualExecution();
        var (_, agentRunId) = await StageHandoffsAsync(fleet, team);
        await SetAgentRunStatusAsync(agentRunId, AgentRunStatus.Running);
        var launched = await LoadAgentRunAsync(agentRunId);

        await UnsealAsAdminAsync();

        var after = await LoadAgentRunAsync(agentRunId);
        after.SealedTaskCiphertext.ShouldBe(launched.SealedTaskCiphertext, "a launched run keeps the shape its launch was built from");
        after.TaskJson.ShouldBe(launched.TaskJson);
    }

    [Fact]
    public async Task Unsealing_a_contract_bearing_first_attempt_re_stakes_it_from_the_restored_task()
    {
        // The composer re-derives a workflow agent's contract hash from task_jsonb and admission compares it with the
        // staked one. Staked from the MASKED goal, a restored exact goal would attest a contract the ledger never staked.
        using var fleet = new HangfireFleetHarness(_fixture.ConnectionString);
        fleet.AnnounceCurrentWorker();
        var team = await SeedTeamWithSecretAsync();
        using var manual = ResolveJobClient().ManualExecution();
        var workflowId = await CreateWorkflowAsync(team, AgentDefinition(acceptance: """{ "kind": "TestsPass", "command": ["sh", "-c", "exit 0"] }"""));
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, team.TeamId);
        await RunEngineAsync(fleet, runId);
        var agentRun = await LoadAgentRunForRunAsync(runId);

        (await StakedAcceptanceHashAsync(runId)).ShouldBe(AgentAcceptanceContract.Hash(PreviousBuildTask(agentRun)), "precondition: the sealed attempt staked the hash of the masked task_jsonb");

        await UnsealAsAdminAsync();

        var restored = await LoadAgentRunAsync(agentRun.Id);
        (await StakedAcceptanceHashAsync(runId)).ShouldBe(AgentAcceptanceContract.Hash(PreviousBuildTask(restored)), "the unit is re-staked from the restored task — the hash the composer will derive from it");
    }

    [Fact]
    public async Task Only_a_global_admin_can_unseal()
    {
        var team = await SeedTeamWithSecretAsync();
        using var scope = _fixture.BeginScopeAs(team.OwnerId, team.TeamId);

        await Should.ThrowAsync<TenantAccessDeniedException>(() => scope.Resolve<IMediator>().Send(new UnsealNonTerminalPayloadsCommand()));
    }

    // ── The previous build's read path (a9cf96d35^) ───────────────────────────────────────────────────────────

    /// <summary><c>AgentRunExecutor.ExecuteAsync</c> at <c>a9cf96d35^</c>: <c>JsonSerializer.Deserialize&lt;AgentTask&gt;(run.TaskJson, AgentJson.Options)</c> — the only copy of the task it knows.</summary>
    private static AgentTask PreviousBuildTask(AgentRun run) => JsonSerializer.Deserialize<AgentTask>(run.TaskJson, AgentJson.Options)!;

    /// <summary><c>WorkflowEngine.BuildScopeFreshAndPersistSnapshotAsync</c> at <c>a9cf96d35^</c>: <c>ParsePayloadObject(run.RunRequest?.NormalizedPayloadJson)</c> — the child's <c>{{trigger.*}}</c>.</summary>
    private static JsonElement PreviousBuildTrigger(WorkflowRunRequest request) => JsonDocument.Parse(request.NormalizedPayloadJson).RootElement;

    // ── Staging ──────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>A parent whose <c>flow.subworkflow</c> hands the team secret to a child, and a run whose <c>agent.run</c> goal embeds it — each parked at its suspend, so the child is staged (not run) and the agent queued (not launched).</summary>
    private async Task<(Guid ChildRunId, Guid AgentRunId)> StageHandoffsAsync(HangfireFleetHarness fleet, Team team)
    {
        var childWorkflowId = await CreateWorkflowAsync(team, ChildDefinition());
        var parentWorkflowId = await CreateWorkflowAsync(team, ParentDefinition(childWorkflowId));
        var agentWorkflowId = await CreateWorkflowAsync(team, AgentDefinition());

        var parentRunId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, parentWorkflowId, team.TeamId);
        var agentWorkflowRunId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, agentWorkflowId, team.TeamId);

        await RunEngineAsync(fleet, parentRunId);
        await RunEngineAsync(fleet, agentWorkflowRunId);

        return (await LoadChildRunIdAsync(parentRunId), (await LoadAgentRunForRunAsync(agentWorkflowRunId)).Id);
    }

    private async Task RunEngineAsync(HangfireFleetHarness fleet, Guid runId)
    {
        using var scope = fleet.BeginScope(_fixture);
        await scope.Resolve<IWorkflowEngine>().ExecuteRunAsync(runId, CancellationToken.None);
    }

    private async Task<Guid> ForkAsync(HangfireFleetHarness fleet, Team team, Guid runId, Fork fork)
    {
        using var scope = fleet.BeginScope(_fixture);
        var workflows = scope.Resolve<IWorkflowService>();

        return fork == Fork.Replay
            ? await workflows.ReplayRunAsync(runId, team.TeamId, team.OwnerId, CancellationToken.None)
            : await workflows.RerunFromNodeAsync(runId, "emit", team.TeamId, team.OwnerId, CancellationToken.None);
    }

    private async Task SetAgentRunStatusAsync(Guid agentRunId, AgentRunStatus status)
    {
        using var scope = _fixture.BeginScope();
        await scope.Resolve<CodeSpaceDbContext>().AgentRun.Where(run => run.Id == agentRunId).ExecuteUpdateAsync(s => s.SetProperty(run => run.Status, status));
    }

    private async Task<UnsealNonTerminalPayloadsResponse> UnsealAsAdminAsync()
    {
        using var scope = _fixture.BeginScopeAs(Guid.NewGuid(), null, Roles.Admin);
        return await scope.Resolve<IMediator>().Send(new UnsealNonTerminalPayloadsCommand());
    }

    // ── Definitions + seeding ────────────────────────────────────────────────────────────────────────────────

    private sealed record Team(Guid TeamId, Guid OwnerId, string Secret);

    private async Task<Team> SeedTeamWithSecretAsync()
    {
        var (teamId, ownerId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var team = new Team(teamId, ownerId, $"CANARY-rolling-{Guid.NewGuid():N}");

        using var scope = _fixture.BeginScope();
        await scope.Resolve<IVariableService>().SetAsync(VariableScope.Team, teamId, teamId, "DEPLOY_TOKEN", VariableValueType.Secret, JsonSerializer.SerializeToElement(team.Secret), null, ownerId, CancellationToken.None);

        return team;
    }

    private async Task<Guid> CreateWorkflowAsync(Team team, WorkflowDefinition definition)
    {
        using var scope = _fixture.BeginScopeAs(team.OwnerId, team.TeamId, Roles.Admin);

        return await scope.Resolve<IMediator>().Send(new CreateWorkflowCommand { Name = "rolling-" + Guid.NewGuid().ToString("N")[..8], Definition = definition, Activations = [], Enabled = true });
    }

    private static WorkflowDefinition ParentDefinition(Guid childWorkflowId) => new()
    {
        SchemaVersion = 1,
        Nodes =
        [
            new() { Id = "start", TypeKey = "trigger.manual", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "sub", TypeKey = "flow.subworkflow", Config = WorkflowsTestSeed.Json($$"""{ "workflowId": "{{childWorkflowId}}" }"""), Inputs = WorkflowsTestSeed.Json("""{ "inputs": { "token": "{{team.DEPLOY_TOKEN}}" } }""") },
            new() { Id = "end", TypeKey = "builtin.terminal", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.Json("""{ "status": "ok" }""") },
        ],
        Edges = [new() { From = "start", To = "sub" }, new() { From = "sub", To = "end" }],
    };

    private static WorkflowDefinition ChildDefinition() => new()
    {
        SchemaVersion = 1,
        Nodes =
        [
            new() { Id = "start", TypeKey = "trigger.manual", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "emit", TypeKey = JsonEmitNode.Key, Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.Json("""{ "t": "{{trigger.token}}" }""") },
            new() { Id = "end", TypeKey = "builtin.terminal", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.Json("""{ "status": "ok" }""") },
        ],
        Edges = [new() { From = "start", To = "emit" }, new() { From = "emit", To = "end" }],
    };

    private static WorkflowDefinition AgentDefinition(string? acceptance = null) => new()
    {
        SchemaVersion = 1,
        Nodes =
        [
            new() { Id = "start", TypeKey = "trigger.manual", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "agent", TypeKey = "agent.run", Inputs = WorkflowsTestSeed.EmptyJson(),
                    Config = WorkflowsTestSeed.Json("""{ "goal": "Deploy with bearer {{team.DEPLOY_TOKEN}}", "harness": "codex-cli", "runnerKind": "local", "timeoutSeconds": 300""" + (acceptance is null ? "" : $", \"acceptance\": {acceptance}") + " }") },
            new() { Id = "end", TypeKey = "builtin.terminal", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.Json("""{ "summary": "{{nodes.agent.outputs.summary}}" }""") },
        ],
        Edges = [new() { From = "start", To = "agent" }, new() { From = "agent", To = "end" }],
    };

    // ── Reading ──────────────────────────────────────────────────────────────────────────────────────────────

    private InMemoryBackgroundJobClient ResolveJobClient()
    {
        using var scope = _fixture.BeginScope();
        return scope.Resolve<InMemoryBackgroundJobClient>();
    }

    private async Task<WorkflowRun> LoadRunAsync(Guid runId)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<CodeSpaceDbContext>().WorkflowRun.AsNoTracking().SingleAsync(run => run.Id == runId);
    }

    private async Task<WorkflowRunRequest> LoadRunRequestAsync(Guid runId)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var requestId = await db.WorkflowRun.AsNoTracking().Where(run => run.Id == runId).Select(run => run.RunRequestId).SingleAsync();

        return await db.WorkflowRunRequest.AsNoTracking().SingleAsync(request => request.Id == requestId);
    }

    private async Task<AgentRun> LoadAgentRunAsync(Guid agentRunId)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<CodeSpaceDbContext>().AgentRun.AsNoTracking().SingleAsync(run => run.Id == agentRunId);
    }

    private async Task<AgentRun> LoadAgentRunForRunAsync(Guid workflowRunId)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<CodeSpaceDbContext>().AgentRun.AsNoTracking().SingleAsync(run => run.WorkflowRunId == workflowRunId);
    }

    private async Task<Guid> LoadChildRunIdAsync(Guid parentRunId)
    {
        using var scope = _fixture.BeginScope();
        var token = await scope.Resolve<CodeSpaceDbContext>().WorkflowRunWait.AsNoTracking().Where(wait => wait.RunId == parentRunId && wait.WaitKind == WorkflowWaitKinds.Subworkflow).Select(wait => wait.Token).SingleAsync();

        return Guid.Parse(token);
    }

    private async Task<string?> StakedAcceptanceHashAsync(Guid workflowRunId)
    {
        using var scope = _fixture.BeginScope();
        var envelope = await scope.Resolve<CodeSpaceDbContext>().CompletionRequirement.AsNoTracking().Where(r => r.WorkflowRunId == workflowRunId && r.Kind == ContractKinds.Acceptance).Select(r => r.EnvelopeJson).SingleAsync();

        return JsonSerializer.Deserialize<RequirementEnvelope>(envelope, AgentJson.Options)!.SpecHash;
    }
}
