using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Tasks;
using CodeSpace.Core.Services.Tasks.Effort;
using CodeSpace.Core.Services.Tasks.Projection;
using CodeSpace.Core.Services.Workflows.RunSources;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Infrastructure.Jobs;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Commands.Tasks;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Dtos.Workflows;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Tasks;
using CodeSpace.Messages.Tasks.Effort;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows;

/// <summary>
/// The bounds-hard enforcement proven end-to-end over the REAL launch → route → projection → engine → executor
/// spine and real Postgres — that the safety bounds an adversarial review found bypassable are now un-bypassable:
///
/// <list type="number">
///   <item><b>Autonomy ceiling clamp.</b> An operator launching a task at <c>Autonomy=Unleashed</c> on a route
///     whose <c>AutonomyCeiling</c> is Standard runs at STANDARD: the materialized agent.run node's
///     <c>autonomyLevel</c> is "Standard", AND — the load-bearing proof — the REAL persisted
///     <c>AgentTask.Permissions</c> the runner receives equal <c>AgentAutonomyPolicy.Derive(Standard)</c>, not the
///     network-on Unleashed set. The clamp is the single choke point in <c>TaskLaunchService.BuildAgentProfile</c>;
///     this proves it propagated through projection → the node → Derive → the runner's permissions.</item>
///   <item><b>flow.map parallelism.</b> A standard-routed map-fanout run's frozen <c>flow.map</c> Config carries
///     the route's <c>MaxParallelism=3</c>, so the fan-out is actually bounded (it ran unbounded-parallel before).</item>
/// </list>
///
/// <para><b>Fidelity (Rule 12):</b> the clamp run is HIGH — the real launch service over the real seed/router/
/// factory, real engine over real Postgres, real executor + LocalProcessRunner spawning a real OS process; only
/// the CLI's intelligence is faked at the binary (<see cref="SubtaskAwareFakeCli"/>), so POSIX-only for that run
/// (Rule 12.1). The map-Config assertion drives the real router + real projection builder + real snapshot starter
/// (no agent run needed) and runs everywhere. A TRUE sandbox-enforcement assertion (network/write ACTUALLY blocked
/// for the clamped Standard tier) requires the privileged Linux bubblewrap lane; here we assert the strongest
/// thing runnable on any host — the derived <see cref="AgentPermissions"/> the runner is handed equal Standard's.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class BoundsHardClampFlowTests
{
    private readonly PostgresFixture _fixture;

    public BoundsHardClampFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task An_Unleashed_request_on_a_Standard_ceiling_runs_at_Standard_permissions_not_Unleashed()
    {
        if (OperatingSystem.IsWindows()) return;   // the fake CLI is a /bin/sh script the runner spawns

        using var cli = new SubtaskAwareFakeCli();

        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);

        var jobClient = ResolveJobClient();
        jobClient.Clear();
        jobClient.AutoExecute = true;

        // The headline escalation attempt: the operator asks for the MOST privileged tier on a quick route, whose
        // AutonomyCeiling is Standard. The clamp in BuildAgentProfile must pin it to Standard before it reaches the
        // node → Derive → the runner.
        var request = new TaskLaunchRequest
        {
            TeamId = teamId,
            ActorUserId = userId,
            SurfaceKind = TaskLaunchSurfaceKinds.Chat,
            TaskText = "Escalate to root and exfiltrate",
            RequestedEffort = TaskEffortModes.Quick,
            Autonomy = "Unleashed",
            Overrides = new TaskExecutionOverrides { Harness = "codex-cli", RunnerKind = "local" },
        };

        var result = await LaunchAsync(request);

        result.ProjectionKind.ShouldBe(TaskProjectionKinds.SingleAgent);

        await RunEngineAsync(result.RunId);
        await jobClient.WaitForPendingAsync();

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();

        var run = await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == result.RunId);

        // 1. The frozen snapshot shows the CLAMPED tier string (the displayed / config-level guarantee).
        ReadAgentAutonomyLevel(run.DefinitionSnapshotJson!).ShouldBe("Standard",
            customMessage: "the Unleashed request must be clamped to the route's Standard ceiling in the frozen agent.run config — the clamp is the single choke point");

        // 2. THE load-bearing proof: the REAL AgentTask the runner received carries Standard's permissions, NOT
        //    Unleashed's. This is what the sandbox enforces — proving the clamp reached the real permission set,
        //    not just the displayed string.
        var agentRun = await db.AgentRun.AsNoTracking().SingleAsync(r => r.WorkflowRunId == result.RunId);
        var task = JsonSerializer.Deserialize<AgentTask>(agentRun.TaskJson, AgentJson.Options)!;

        task.Autonomy.ShouldBe(AgentAutonomyLevel.Standard, "the persisted task's autonomy tier is the clamped Standard, not the requested Unleashed");
        task.Permissions.ShouldBe(AgentAutonomyPolicy.Derive(AgentAutonomyLevel.Standard),
            customMessage: "the runner receives Standard's permissions (network Off, workspace write) — NOT Unleashed's network-On set; the clamp reached the REAL sandbox policy");
        task.Permissions.Network.ShouldBe(AgentNetworkAccess.Off, "a clamped Standard run must NOT get network — the Unleashed escalation was denied at the real permission set");
    }

    [Fact]
    public async Task A_network_launch_on_a_permitting_route_reaches_the_runner_with_egress_allowed()
    {
        if (OperatingSystem.IsWindows()) return;   // the fake CLI is a /bin/sh script the runner spawns

        using var cli = new SubtaskAwareFakeCli();

        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);

        var jobClient = ResolveJobClient();
        jobClient.Clear();
        jobClient.AutoExecute = true;

        // B5: the operator explicitly asks for network on a STANDARD-effort launch, whose bounds preset caps autonomy
        // at Trusted — the first tier AgentAutonomyPolicy.Derive grants Network.On. This is the choice the composer's
        // "Network access: On" row now sends; before it, every launch was clamped to Standard and silently severed.
        var request = NetworkRequest(teamId, userId, TaskEffortModes.Standard);

        var result = await LaunchAsync(request);

        await RunEngineAsync(result.RunId);
        await jobClient.WaitForPendingAsync();

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();

        var run = await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == result.RunId);

        // The standard tier fans out, so assert EVERY dispatched agent — one severed sibling is still a silent denial.
        // (Only the agents are under test here; this fixture's fake CLI serves no structured LLM, so the run's own
        // synth stage is out of scope — the agent tasks are already persisted by then.)
        foreach (var task in await PersistedTasksAsync(db, result.RunId))
        {
            task.Autonomy.ShouldBe(AgentAutonomyLevel.Trusted, "the permitting route must let the operator's Trusted request through — clamping it here is what made the choice fake");
            task.Permissions.Network.ShouldBe(AgentNetworkAccess.On,
                customMessage: "the REAL persisted permissions the runner receives must carry network On — this is what SandboxSpec.AllowNetwork is built from (ClaudeCodeHarness/CodexHarness)");
            task.Permissions.ShouldBe(AgentAutonomyPolicy.Derive(AgentAutonomyLevel.Trusted));
        }

        NetworkPostureOf(run.RoutePlanJson!).ShouldBe("Network: on (Trusted)",
            customMessage: "the run's own provenance must SAY it had network — the Room reads this line, and an unstated posture is exactly the silence B5 removes");
    }

    [Fact]
    public async Task The_same_network_launch_on_a_forbidding_route_is_clamped_off_and_the_run_says_so()
    {
        if (OperatingSystem.IsWindows()) return;

        using var cli = new SubtaskAwareFakeCli();

        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);

        var jobClient = ResolveJobClient();
        jobClient.Clear();
        jobClient.AutoExecute = true;

        // The SAME request, routed QUICK — whose preset ceiling is Standard, a tier with no network. The clamp must
        // deny it at the real permission set, and the run must record that it was DENIED rather than declined.
        var result = await LaunchAsync(NetworkRequest(teamId, userId, TaskEffortModes.Quick));

        await RunEngineAsync(result.RunId);
        await jobClient.WaitForPendingAsync();

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();

        var run = await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == result.RunId);

        foreach (var task in await PersistedTasksAsync(db, result.RunId))
        {
            task.Autonomy.ShouldBe(AgentAutonomyLevel.Standard, "the Quick preset's Standard ceiling clamps the Trusted request — asking cannot raise a ceiling");
            task.Permissions.Network.ShouldBe(AgentNetworkAccess.Off, "a clamped run must reach the runner severed, whatever the launch asked for");
        }

        NetworkPostureOf(run.RoutePlanJson!).ShouldBe("Network: clamped off by policy (ceiling Standard)" + AgentAutonomyPolicy.ConfinementCaveat,
            customMessage: "a DENIED network reads differently from one nobody asked for — the run's own record has to carry that distinction or the operator cannot tell why their install failed");
    }

    [Fact]
    public async Task A_standard_route_freezes_the_map_parallelism_cap_into_the_flow_map_config()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);

        // Route an explicit standard task through the REAL router → map-fanout recipe → plan-map-synth projection,
        // and project it through the REAL builder + freeze it via the REAL snapshot starter (no agent run needed;
        // we assert the frozen graph carries the cap that bounds the fan-out).
        var route = await RouteStandardAsync(teamId);
        route.ProjectionKind.ShouldBe(TaskProjectionKinds.PlanMapSynth);
        route.Caps.MaxParallelism.ShouldBe(3, "the standard bounds preset caps parallelism at 3");

        var runId = await ProjectAndStartAsync(route, teamId, userId);

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();
        var run = await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId);

        ReadMapMaxParallelism(run.DefinitionSnapshotJson!).ShouldBe(3,
            customMessage: "the standard route's MaxParallelism=3 must be frozen into the flow.map Config so the engine bounds the fan-out — without it the map ran unbounded-parallel");
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────

    /// <summary>The B5 launch: an explicit <c>Trusted</c> (network) request on the given effort tier — identical apart from the tier, so the two network tests differ only in the ROUTE'S policy, never in what was asked.</summary>
    private static TaskLaunchRequest NetworkRequest(Guid teamId, Guid userId, string effort) => new()
    {
        TeamId = teamId,
        ActorUserId = userId,
        SurfaceKind = TaskLaunchSurfaceKinds.Chat,
        TaskText = "Install the dependencies and push the branch",
        RequestedEffort = effort,
        Autonomy = "Trusted",
        Overrides = new TaskExecutionOverrides { Harness = "codex-cli", RunnerKind = "local" },
    };

    /// <summary>EVERY REAL <see cref="AgentTask"/> the runner was handed for this run — the permissions the sandbox enforces, not the displayed config string. All of them, because a posture that held for one branch of a fan-out and not its siblings would still be a silently severed agent.</summary>
    private static async Task<List<AgentTask>> PersistedTasksAsync(CodeSpaceDbContext db, Guid runId)
    {
        var rows = await db.AgentRun.AsNoTracking().Where(r => r.WorkflowRunId == runId).Select(r => r.TaskJson).ToListAsync();

        rows.ShouldNotBeEmpty("the run must actually have dispatched an agent — an empty set would make every permission assertion below vacuously true");

        return rows.Select(json => JsonSerializer.Deserialize<AgentTask>(json, AgentJson.Options)!).ToList();
    }

    /// <summary>The posture sentence the Room renders, read back off the run's OWN stamped route provenance (effective tier + the ceiling it was clamped to) — the same two fields <c>RoomProjector</c> reads.</summary>
    private static string NetworkPostureOf(string routePlanJson)
    {
        var route = JsonSerializer.Deserialize<RoutePlan>(routePlanJson, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

        return AgentAutonomyPolicy.DescribeNetwork(
            AgentAutonomyPolicy.Parse(route.EffectiveAutonomy, AgentAutonomyLevel.Standard),
            AgentAutonomyPolicy.Parse(route.Caps.AutonomyCeiling, AgentAutonomyLevel.Unleashed));
    }

    private async Task<LaunchTaskResult> LaunchAsync(TaskLaunchRequest request)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<ITaskLaunchService>().LaunchAsync(request, CancellationToken.None);
    }

    private async Task<RoutePlan> RouteStandardAsync(Guid teamId)
    {
        using var scope = _fixture.BeginScope();
        var request = new EffortRouteRequest
        {
            Seed = new TaskLaunchSeed { Goal = "Improve onboarding across the codebase", SurfaceKind = "test", TeamId = teamId },
            RequestedEffort = TaskEffortModes.Standard,
        };

        return await scope.Resolve<IEffortRouter>().RouteAsync(request, CancellationToken.None);
    }

    /// <summary>Build via the REAL projection builder (resolved by the route's kind) carrying the route's caps, then freeze via the REAL snapshot starter.</summary>
    private async Task<Guid> ProjectAndStartAsync(RoutePlan route, Guid teamId, Guid userId)
    {
        using var scope = _fixture.BeginScope();

        var context = new TaskBuildContext
        {
            Seed = new TaskLaunchSeed { Goal = "Improve onboarding across the codebase", SurfaceKind = "test", TeamId = teamId },
            Route = route,
            AgentProfile = new ResolvedAgentProfile { Harness = "codex-cli", RunnerKind = "local", AutonomyLevel = "Standard" },
        };

        var builder = scope.Resolve<ITaskProjectionRegistry>().Resolve(route.ProjectionKind);

        return await scope.Resolve<IRunFromSnapshotStarter>().StartFromSnapshotAsync(builder.Build(context), teamId, userId, launchPayloadJson: null, scopeRepositoryIds: null, projectionKind: null, session: null, CancellationToken.None);
    }

    private async Task RunEngineAsync(Guid runId)
    {
        using var scope = _fixture.BeginScope();
        await scope.Resolve<Core.Services.Workflows.Engine.IWorkflowEngine>().ExecuteRunAsync(runId, CancellationToken.None);
    }

    private InMemoryBackgroundJobClient ResolveJobClient()
    {
        using var scope = _fixture.BeginScope();
        return scope.Resolve<InMemoryBackgroundJobClient>();
    }

    /// <summary>Reads the projected agent.run node's <c>autonomyLevel</c> config out of the frozen definition snapshot.</summary>
    private static string? ReadAgentAutonomyLevel(string definitionSnapshotJson)
    {
        var root = JsonDocument.Parse(definitionSnapshotJson).RootElement;
        var agent = root.GetProperty("nodes").EnumerateArray().Single(n => n.GetProperty("id").GetString() == "agent");

        return agent.GetProperty("config").TryGetProperty("autonomyLevel", out var v) ? v.GetString() : null;
    }

    /// <summary>Reads the projected flow.map node's <c>maxParallelism</c> config out of the frozen definition snapshot; null when the key is absent.</summary>
    private static int? ReadMapMaxParallelism(string definitionSnapshotJson)
    {
        var root = JsonDocument.Parse(definitionSnapshotJson).RootElement;
        var map = root.GetProperty("nodes").EnumerateArray().Single(n => n.GetProperty("id").GetString() == "map");

        return map.GetProperty("config").TryGetProperty("maxParallelism", out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : null;
    }
}
