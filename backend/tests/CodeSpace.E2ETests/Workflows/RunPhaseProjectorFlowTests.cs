using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Supervisor;
using CodeSpace.Core.Services.Tasks.Effort;
using CodeSpace.Core.Services.Tasks.Phases;
using CodeSpace.Core.Services.Tasks.Projection;
using CodeSpace.Core.Services.Workflows.Engine;
using CodeSpace.Core.Services.Workflows.RunSources;
using CodeSpace.Core.Services.Workflows.Runtime;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Infrastructure.Jobs;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Dtos.Workflows;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Tasks;
using CodeSpace.Messages.Tasks.Effort;
using CodeSpace.Messages.Tasks.Phases;
using CodeSpace.Core.Services.Tasks.Phases.Sources.Nodes;
using CodeSpace.Core.Services.Tasks.Phases.Sources.Supervisor;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.E2ETests.Workflows;

/// <summary>
/// 🟢 PR7's crown jewel — the run phase projector over REAL task runs of all three effort tiers, through the REAL
/// projector + sources + engine over real Postgres (only the CLI's intelligence and the supervisor decider are
/// faked at their honest boundaries). It proves the phase tree is DERIVED from the durable substrate a run actually
/// produced, not asserted:
/// <list type="bullet">
///   <item>QUICK single-agent → the agent.code node surfaces as a phase carrying ONE real PhaseAgentRef (Succeeded);</item>
///   <item>STANDARD plan-map-synth → a 'Fan out' map phase whose Agents are the >1 real branch agent refs;</item>
///   <item>DEEP supervisor (lane on, PlanThenStop) → the agent.supervisor node phase AND the supervisor-ledger Plan + Stop
///         decision phases with the right statuses.</item>
/// </list>
/// Plus the team-scope 404-conflate (a foreign team's run → null) and the ZERO-CORE-EDIT genericity contract: a
/// FAKE source registered only in a child DI scope appears in the merged output with no projector change.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "E2E")]
[Trait("Surface", "Engine")]
public class RunPhaseProjectorFlowTests : IDisposable
{
    private readonly PostgresFixture _fixture;
    private readonly string? _laneFlagBefore;

    public RunPhaseProjectorFlowTests(PostgresFixture fixture)
    {
        _fixture = fixture;
        _laneFlagBefore = Environment.GetEnvironmentVariable(SupervisorLane.EnabledEnvVar);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(SupervisorLane.EnabledEnvVar, _laneFlagBefore);

        using var scope = _fixture.BeginScope();

        // The supervisor decision script is a shared singleton — restore the default so the deep test's
        // PlanThenStop never leaks into a sibling supervisor test (mirrors SupervisorProjectionFlowTests.Dispose).
        scope.Resolve<SupervisorDecisionScript>().PlanThenStop();

        // The in-test job client is a shared singleton too — the deep test runs it with AutoExecute=false +
        // manual turn-driving; reset it (drain pending + re-enable auto-execute) so a sibling test that relies on
        // the default drain-everything behavior never inherits this class's paused/queued state.
        var jobClient = scope.Resolve<InMemoryBackgroundJobClient>();
        jobClient.Clear();
        jobClient.AutoExecute = true;
    }

    [Fact]
    public async Task Quick_run_phases_include_the_agent_node_with_one_real_agent_ref()
    {
        if (OperatingSystem.IsWindows()) return;   // the fake CLI is a /bin/sh script the runner spawns

        using var cli = new SubtaskAwareFakeCli();

        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);

        var jobClient = ResolveJobClient();
        jobClient.Clear();
        jobClient.AutoExecute = true;

        var context = new TaskBuildContext
        {
            Seed = new TaskLaunchSeed { Goal = "Work on the auth refactor", SurfaceKind = "test", TeamId = teamId },
            Route = new RoutePlan { ProjectionKind = TaskProjectionKinds.SingleAgent },
            AgentProfile = new ResolvedAgentProfile { Harness = "codex-cli", RunnerKind = "local", AutonomyLevel = "Confined" },
        };

        var handle = await CreateAndRunAsync(context, teamId, userId);
        await RunEngineAsync(handle.RunId);
        await jobClient.WaitForPendingAsync();

        var phases = await ProjectAsync(handle.RunId, teamId);

        phases.ShouldNotBeNull();

        var agentPhase = phases!.Where(p => p.Kind == "agent").ToList()
            .ShouldHaveSingleItem("the single-agent run's agent.code node surfaces as one 'agent' phase");

        agentPhase.SourceKey.ShouldBe(WorkflowNodePhaseSource.Key);
        agentPhase.Status.ShouldBe(PhaseStatus.Succeeded);

        var agent = agentPhase.Agents.ShouldHaveSingleItem();
        agent.Status.ShouldBe(nameof(AgentRunStatus.Succeeded), "the ref carries the REAL team-scoped AgentRunStatus, not the NodeStatus name");
        agent.AgentRunId.ShouldBe(await AgentRunIdForAsync(handle.RunId, teamId), "the phase ref is the REAL agent run the node spawned");
    }

    [Fact]
    public async Task Standard_run_phases_include_a_fan_out_phase_with_multiple_real_agent_refs()
    {
        if (OperatingSystem.IsWindows()) return;

        using var cli = new SubtaskAwareFakeCli();

        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);

        var jobClient = ResolveJobClient();
        jobClient.Clear();
        jobClient.AutoExecute = true;

        var route = await RouteAsync(new EffortRouteRequest
        {
            Seed = new TaskLaunchSeed { Goal = "Improve the onboarding module across the codebase", SurfaceKind = "test", TeamId = teamId },
            RequestedEffort = TaskEffortModes.Standard,
        });

        route.ProjectionKind.ShouldBe(TaskProjectionKinds.PlanMapSynth);

        var runId = await ProjectRetargetAndStartAsync(route, teamId, userId);
        await RunEngineAsync(runId);
        await jobClient.WaitForPendingAsync();

        var phases = await ProjectAsync(runId, teamId);

        phases.ShouldNotBeNull();

        var fanOut = phases!.Where(p => p.Kind == "map").ToList()
            .ShouldHaveSingleItem("the plan-map-synth run's flow.map node rolls up to one 'Fan out' map phase");

        fanOut.Label.ShouldBe("Fan out");
        fanOut.Agents.Count.ShouldBeGreaterThan(1, "the map fanned out to MULTIPLE real branch agents");
        fanOut.Agents.Count.ShouldBe(DeterministicPlannerLlmClient.Subtasks.Count, "one branch agent ref per planned subtask");
        fanOut.Agents.ShouldAllBe(a => a.Status == nameof(AgentRunStatus.Succeeded), "every real branch agent finished Succeeded (the team-scoped AgentRunStatus, not the NodeStatus name)");
        fanOut.Agents.Select(a => a.IterationKey).ShouldAllBe(k => k != null && k.StartsWith("map#"), "each ref carries its map branch key");

        var realBranchAgentIds = await BranchAgentRunIdsAsync(runId, teamId);
        fanOut.Agents.Select(a => a.AgentRunId).OrderBy(id => id).ShouldBe(realBranchAgentIds.OrderBy(id => id), "the refs are the REAL branch agent runs");

        fanOut.Metrics.AgentCount.ShouldBe(DeterministicPlannerLlmClient.Subtasks.Count);
        fanOut.Metrics.FailedCount.ShouldBe(0);
    }

    [Fact]
    public async Task Deep_run_phases_include_the_supervisor_node_and_the_ledger_decision_phases()
    {
        Environment.SetEnvironmentVariable(SupervisorLane.EnabledEnvVar, "1");

        using (var setup = _fixture.BeginScope()) setup.Resolve<SupervisorDecisionScript>().PlanThenStop();

        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);

        var jobClient = ResolveJobClient();
        jobClient.Clear();
        jobClient.AutoExecute = false;

        var route = await RouteAsync(new EffortRouteRequest
        {
            Seed = new TaskLaunchSeed { Goal = "ship the whole feature", SurfaceKind = "test", TeamId = teamId },
            RequestedEffort = TaskEffortModes.Deep,
        });

        route.ProjectionKind.ShouldBe(TaskProjectionKinds.Supervisor, "deep routes the supervisor with the lane on");

        var context = new TaskBuildContext
        {
            Seed = new TaskLaunchSeed { Goal = "ship the whole feature", SurfaceKind = "test", TeamId = teamId },
            Route = route,
            AgentProfile = new ResolvedAgentProfile { Harness = "codex-cli", RunnerKind = "local", AutonomyLevel = "Standard" },
        };

        var handle = await CreateAndRunAsync(context, teamId, userId);

        // Drive the turn loop: turn 0 plan (self-advance) → resolve → turn 1 stop → Success.
        await RunEngineAsync(handle.RunId);
        await ResolveSelfAdvanceAsync(handle.RunId);
        await RunEngineAsync(handle.RunId);

        var phases = await ProjectAsync(handle.RunId, teamId);

        phases.ShouldNotBeNull();

        // The structural agent.supervisor node phase from the node source (the builder names the node "sup").
        phases!.ShouldContain(p => p.SourceKey == WorkflowNodePhaseSource.Key && p.NodeIdMatches("sup"),
            "the agent.supervisor node surfaces as a structural phase");

        // The supervisor-ledger decision phases: Plan then Stop, both Succeeded, sorted AFTER the structural ones.
        var ledgerPhases = phases.Where(p => p.SourceKey == SupervisorPhaseSource.Key).ToList();

        ledgerPhases.Select(p => p.Kind).ShouldBe(new[] { SupervisorDecisionKinds.Plan, SupervisorDecisionKinds.Stop },
            "the supervisor decision ledger surfaces as plan then stop phases");
        ledgerPhases.Select(p => p.Label).ShouldBe(new[] { "Plan", "Stop" });
        ledgerPhases.ShouldAllBe(p => p.Status == PhaseStatus.Succeeded);

        var firstStructuralOrder = phases.Where(p => p.SourceKey == WorkflowNodePhaseSource.Key).Max(p => p.Order);
        ledgerPhases.ShouldAllBe(p => p.Order > firstStructuralOrder, "ledger phases sort after the structural node phases (the high base offset)");
    }

    [Fact]
    public async Task Deep_run_with_a_real_spawn_surfaces_the_spawn_phase_with_the_ground_truth_child_agent_refs()
    {
        // 🟢 The team-scoped spawn-child fold over a REAL run — the security-relevant path the PlanThenStop deep test
        // can't exercise (it stages no agents). The scripted decider emits a REAL spawn (PlanSpawnStop), so 2 real
        // AgentRun rows are staged + parked on agent waits; we simulate their completion to resume the supervisor to
        // stop, then ProjectAsync and assert the supervisor-ledger spawn phase's child PhaseAgentRefs equal the REAL
        // AgentRun ids, each carrying the REAL AgentRunStatus read TEAM-SCOPED from the DB (never the decider's word).
        Environment.SetEnvironmentVariable(SupervisorLane.EnabledEnvVar, "1");

        // Switch the shared scripted decider to the spawn arc; Dispose restores PlanThenStop for sibling tests.
        using (var setup = _fixture.BeginScope()) setup.Resolve<SupervisorDecisionScript>().PlanSpawnStop();

        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);

        var jobClient = ResolveJobClient();
        jobClient.Clear();
        jobClient.AutoExecute = false;   // the binary-less harness must not run; we simulate agent completion below — Dispose resets this

        var route = await RouteAsync(new EffortRouteRequest
        {
            Seed = new TaskLaunchSeed { Goal = "ship the whole feature with a team", SurfaceKind = "test", TeamId = teamId },
            RequestedEffort = TaskEffortModes.Deep,
        });

        route.ProjectionKind.ShouldBe(TaskProjectionKinds.Supervisor, "deep routes the supervisor with the lane on");

        var context = new TaskBuildContext
        {
            Seed = new TaskLaunchSeed { Goal = "ship the whole feature with a team", SurfaceKind = "test", TeamId = teamId },
            Route = route,
            AgentProfile = new ResolvedAgentProfile { Harness = "codex-cli", RunnerKind = "local", AutonomyLevel = "Standard" },
        };

        var handle = await CreateAndRunAsync(context, teamId, userId);

        // Drive the spawn arc: turn 0 plan (self-advance) → turn 1 spawn (stages 2 real agents + parks) → simulate
        // both agents completing → the barrier resumes → turn 2 stop → Success.
        await RunEngineAsync(handle.RunId);
        await ResolveSelfAdvanceAsync(handle.RunId);
        await RunEngineAsync(handle.RunId);

        var stagedAgentIds = await PendingAgentWaitTokensAsync(handle.RunId);
        stagedAgentIds.Count.ShouldBe(2, "the real spawn staged exactly 2 agent runs (the scripted spawn arc fans out to both subtasks)");

        foreach (var agentId in stagedAgentIds) await SimulateAgentCompletionAsync(agentId, $"done-{agentId:N}");
        await RunEngineAsync(handle.RunId);

        var phases = await ProjectAsync(handle.RunId, teamId);

        phases.ShouldNotBeNull();

        var spawnPhase = phases!.Where(p => p.SourceKey == SupervisorPhaseSource.Key && p.Kind == SupervisorDecisionKinds.Spawn).ToList()
            .ShouldHaveSingleItem("the supervisor-ledger source surfaces the real spawn decision as one spawn phase");

        // The REAL AgentRun rows the spawn staged, read team-scoped from the DB — the ground truth the fold must reflect.
        var realAgentIds = await BranchAgentRunIdsAsync(handle.RunId, teamId);
        realAgentIds.Count.ShouldBe(2);

        spawnPhase.Agents.Select(a => a.AgentRunId).OrderBy(id => id)
            .ShouldBe(realAgentIds.OrderBy(id => id), "the spawn phase's child refs are EXACTLY the real AgentRun ids the spawn staged — team-scoped");
        spawnPhase.Agents.ShouldAllBe(a => a.Status == nameof(AgentRunStatus.Succeeded),
            "each child ref carries the REAL team-scoped AgentRunStatus (the simulated completion drove them Succeeded) — not the decider's self-report");

        spawnPhase.Metrics.AgentCount.ShouldBe(2);
        spawnPhase.Metrics.SucceededCount.ShouldBe(2);
        spawnPhase.Metrics.FailedCount.ShouldBe(0);
    }

    [Fact]
    public async Task A_foreign_teams_run_projects_to_null_404_conflate()
    {
        if (OperatingSystem.IsWindows()) return;

        using var cli = new SubtaskAwareFakeCli();

        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var (foreignTeamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);

        var jobClient = ResolveJobClient();
        jobClient.Clear();
        jobClient.AutoExecute = true;

        var context = new TaskBuildContext
        {
            Seed = new TaskLaunchSeed { Goal = "private goal", SurfaceKind = "test", TeamId = teamId },
            Route = new RoutePlan { ProjectionKind = TaskProjectionKinds.SingleAgent },
            AgentProfile = new ResolvedAgentProfile { Harness = "codex-cli", RunnerKind = "local", AutonomyLevel = "Confined" },
        };

        var handle = await CreateAndRunAsync(context, teamId, userId);
        await RunEngineAsync(handle.RunId);
        await jobClient.WaitForPendingAsync();

        // The owning team sees phases; the foreign team gets null — existence is never leaked.
        (await ProjectAsync(handle.RunId, teamId)).ShouldNotBeNull("the owning team projects its own run");
        (await ProjectAsync(handle.RunId, foreignTeamId)).ShouldBeNull("a foreign team's projection of someone else's run is null — 404-conflate, no leak");
    }

    [Fact]
    public async Task A_fake_source_appears_in_the_merged_phases_with_zero_core_edit()
    {
        // THE zero-core-edit genericity proof. A brand-new phase source is registered ONLY in a child DI scope; the
        // SAME RunPhaseProjector (rebuilt over the AUGMENTED IEnumerable<IRunPhaseSource>) fans out across it with no
        // production-core change — a new run shape is purely "drop a source", exactly like adding an IAgentHarness.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);

        var runId = await StartTrivialSnapshotRunAsync(teamId, userId);

        using var scope = _fixture.BeginScope(b => b.RegisterType<SentinelPhaseSource>().As<IRunPhaseSource>().InstancePerLifetimeScope());

        // The projector resolved in this scope sees the production sources AND the sentinel — additive, not a replacement.
        var phases = await scope.Resolve<IRunPhaseProjector>().ProjectAsync(runId, teamId, CancellationToken.None);

        phases.ShouldNotBeNull();
        phases!.ShouldContain(p => p.SourceKey == SentinelPhaseSource.Key,
            "a fake source registered only in a test scope is picked up by the projector's injected IEnumerable — zero projector edit");
    }

    /// <summary>A test-only phase source proving zero-core-edit extensibility — it contributes one sentinel phase for any run.</summary>
    private sealed class SentinelPhaseSource : IRunPhaseSource
    {
        public const string Key = "fake-source";

        public string SourceKey => Key;

        public Task<IReadOnlyList<RunPhase>> ContributeAsync(RunPhaseContext context, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<RunPhase>>(new[]
            {
                new RunPhase { Id = "sentinel", Label = "Sentinel", Kind = "fake", Status = PhaseStatus.Pending, Order = 99, SourceKey = Key },
            });
    }

    // ─── Helpers ────────────────────────────────────────────────────────────────

    private async Task<IReadOnlyList<RunPhase>?> ProjectAsync(Guid runId, Guid teamId)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<IRunPhaseProjector>().ProjectAsync(runId, teamId, CancellationToken.None);
    }

    private async Task<RoutePlan> RouteAsync(EffortRouteRequest request)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<IEffortRouter>().RouteAsync(request, CancellationToken.None);
    }

    private async Task<TaskRunHandle> CreateAndRunAsync(TaskBuildContext context, Guid teamId, Guid userId)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<ITaskRunSnapshotFactory>().CreateAndRunAsync(context, teamId, userId, CancellationToken.None);
    }

    private async Task<Guid> ProjectRetargetAndStartAsync(RoutePlan route, Guid teamId, Guid userId)
    {
        using var scope = _fixture.BeginScope();

        var context = new TaskBuildContext
        {
            Seed = new TaskLaunchSeed { Goal = "Improve the onboarding module across the codebase", SurfaceKind = "test", TeamId = teamId },
            Route = route,
            AgentProfile = new ResolvedAgentProfile { Harness = "codex-cli", RunnerKind = "local", AutonomyLevel = "Confined" },
        };

        var definition = RetargetPlannerToFake(scope.Resolve<ITaskProjectionRegistry>().Resolve(route.ProjectionKind).Build(context));

        return await scope.Resolve<IRunFromSnapshotStarter>().StartFromSnapshotAsync(definition, teamId, userId, launchPayloadJson: null, scopeRepositoryIds: null, projectionKind: null, session: null, CancellationToken.None);
    }

    private static WorkflowDefinition RetargetPlannerToFake(WorkflowDefinition definition) => definition with
    {
        Nodes = definition.Nodes.Select(RetargetNode).ToList(),
    };

    private static NodeDefinition RetargetNode(NodeDefinition node)
    {
        if (node.TypeKey != "llm.complete") return node;

        var config = node.Config.Deserialize<Dictionary<string, JsonElement>>() ?? new();
        config["provider"] = JsonSerializer.SerializeToElement(DeterministicPlannerLlmClient.ProviderTag);

        return node with { Config = JsonSerializer.SerializeToElement(config) };
    }

    private async Task<Guid> StartTrivialSnapshotRunAsync(Guid teamId, Guid userId)
    {
        using var scope = _fixture.BeginScope();

        var definition = new WorkflowDefinition
        {
            SchemaVersion = WorkflowDefinition.CurrentSchemaVersion,
            Nodes = new List<NodeDefinition>
            {
                new() { Id = "start", TypeKey = "trigger.manual", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
                new() { Id = "done", TypeKey = "builtin.terminal", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
            },
            Edges = new List<EdgeDefinition> { new() { From = "start", To = "done" } },
        };

        return await scope.Resolve<IRunFromSnapshotStarter>().StartFromSnapshotAsync(definition, teamId, userId, launchPayloadJson: null, scopeRepositoryIds: null, projectionKind: null, session: null, CancellationToken.None);
    }

    private async Task RunEngineAsync(Guid runId)
    {
        using var scope = _fixture.BeginScope();
        await scope.Resolve<IWorkflowEngine>().ExecuteRunAsync(runId, CancellationToken.None);
    }

    private async Task ResolveSelfAdvanceAsync(Guid runId)
    {
        Guid waitId;
        using (var verify = _fixture.BeginScope())
        {
            waitId = (await verify.Resolve<CodeSpaceDbContext>().WorkflowRunWait.AsNoTracking()
                .SingleAsync(w => w.RunId == runId && w.WaitKind == WorkflowWaitKinds.SupervisorDecision && w.Status == WorkflowWaitStatuses.Pending)).Id;
        }

        using var scope = _fixture.BeginScope();
        await scope.Resolve<IWorkflowResumeService>().ResumeWaitAsync(runId, waitId, null, CancellationToken.None);
    }

    private async Task<Guid> AgentRunIdForAsync(Guid runId, Guid teamId)
    {
        using var scope = _fixture.BeginScope();
        return (await scope.Resolve<CodeSpaceDbContext>().AgentRun.AsNoTracking().SingleAsync(r => r.WorkflowRunId == runId && r.TeamId == teamId)).Id;
    }

    private async Task<IReadOnlyList<Guid>> BranchAgentRunIdsAsync(Guid runId, Guid teamId)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<CodeSpaceDbContext>().AgentRun.AsNoTracking().Where(r => r.WorkflowRunId == runId && r.TeamId == teamId).Select(r => r.Id).ToListAsync();
    }

    /// <summary>The agent-run ids a spawn turn parked on (the AgentRun waits' tokens) — the K real agents staged.</summary>
    private async Task<IReadOnlyList<Guid>> PendingAgentWaitTokensAsync(Guid runId)
    {
        using var scope = _fixture.BeginScope();
        return (await scope.Resolve<CodeSpaceDbContext>().WorkflowRunWait.AsNoTracking()
                .Where(w => w.RunId == runId && w.WaitKind == WorkflowWaitKinds.AgentRun && w.Status == WorkflowWaitStatuses.Pending)
                .OrderBy(w => w.IterationKey).Select(w => w.Token).ToListAsync())
            .Select(Guid.Parse).ToList();
    }

    // Drive the executor's terminal sequence (MarkRunning → Complete → Notify) without the sandboxed CLI — the exact
    // path AgentRunExecutor follows on a real completion (mirrors SupervisorSpawnFlowTests.SimulateAgentCompletionAsync).
    private async Task SimulateAgentCompletionAsync(Guid agentRunId, string summary)
    {
        using var scope = _fixture.BeginScope();
        var runs = scope.Resolve<IAgentRunService>();
        var notifier = scope.Resolve<IAgentRunCompletionNotifier>();

        await runs.MarkRunningAsync(agentRunId, CancellationToken.None);
        await runs.CompleteAsync(agentRunId, new AgentRunResult { Status = AgentRunStatus.Succeeded, ExitReason = "completed", Summary = summary }, CancellationToken.None);
        await notifier.NotifyCompletedAsync(agentRunId, CancellationToken.None);
    }

    private InMemoryBackgroundJobClient ResolveJobClient()
    {
        using var scope = _fixture.BeginScope();
        return scope.Resolve<InMemoryBackgroundJobClient>();
    }
}

internal static class RunPhaseAssertions
{
    /// <summary>A phase from the node source carries the node id as its <c>Id</c>; this reads it for a readable assertion.</summary>
    public static bool NodeIdMatches(this RunPhase phase, string nodeId) => phase.Id == nodeId;
}
