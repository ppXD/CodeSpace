using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Sessions.Room;
using CodeSpace.Core.Services.Workflows.Engine;
using CodeSpace.Core.Services.Workflows.Reconciliation;
using CodeSpace.Core.Settings;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Infrastructure.Jobs;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Commands.Workflows;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Dtos.Sessions.Room;
using CodeSpace.Messages.Dtos.Workflows;
using MediatR;
using Microsoft.Extensions.Configuration;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows;

/// <summary>
/// The deployment autonomy ceiling (<c>Sandbox:MaxAutonomy</c>) proven over the lane the LAUNCH ROUTE never
/// reaches: an AUTHORED workflow whose <c>agent.run</c> node carries its own tier and its own raw
/// <c>"network": true</c> override, and a REPLAY that re-executes that frozen definition long after the run that
/// staged it. Neither consults a <c>RouteCaps</c>, so before this ceiling existed both could hand an agent the
/// internet no route would have granted it, on a host whose operator had forbidden exactly that.
///
/// <para><b>Fidelity (Rule 12) — medium-mock:</b> the real engine + the real <c>agent.run</c> node + real
/// Postgres stage the run and PERSIST the real <see cref="AgentTask"/> the executor would be handed; the harness
/// itself never spawns (<c>AutoExecute=false</c>, mirroring <c>AgentNodeFlowTests</c> — there is no codex binary
/// in CI). The assertion is on the persisted permission set, which is precisely what the sandbox enforces; a
/// TRUE severed-namespace assertion needs the privileged Linux bubblewrap lane.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class DeploymentAutonomyCeilingFlowTests
{
    private readonly PostgresFixture _fixture;

    public DeploymentAutonomyCeilingFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task An_authored_network_node_keeps_its_egress_at_the_committed_default()
    {
        // The control. The committed ceiling grants the top tier, so an authored Trusted + network:true node is
        // byte-identical to before this setting existed — the whole point of the default is that it clamps nothing.
        var task = await StageAuthoredAsync(deploymentCeiling: null);

        task.Autonomy.ShouldBe(AgentAutonomyLevel.Trusted);
        task.Permissions.Network.ShouldBe(AgentNetworkAccess.On,
            customMessage: "an unlowered deployment ceiling must leave the authored node exactly as authored — a setting that changed behaviour on arrival could not ship");
    }

    [Fact]
    public async Task An_authored_network_node_is_severed_under_a_lowered_deployment_ceiling()
    {
        // The bypass this closes: the node writes "network": true straight onto the resolved permissions AFTER the
        // tier baseline, so clamping the TIER alone still handed this run the internet the host forbids every tier.
        var task = await StageAuthoredAsync(deploymentCeiling: "Standard");

        task.Autonomy.ShouldBe(AgentAutonomyLevel.Standard, "the authored Trusted tier is clamped to the deployment's own ceiling");
        task.Permissions.Network.ShouldBe(AgentNetworkAccess.Off,
            customMessage: "the REAL persisted permissions the runner receives must be severed — the raw network override cannot out-rank the deployment ceiling");
    }

    [Fact]
    public async Task The_room_names_the_deployment_ceiling_for_a_run_no_route_can_explain()
    {
        // An authored run carries no route provenance, so the Room's Launch row had nothing to say about it at all.
        // Under a lowered ceiling there IS something to say, and it must name the bound the operator cannot lift by
        // relaunching differently — not the route ceiling, which does not exist here.
        var (runId, teamId, _) = await StageAuthoredRunAsync(deploymentCeiling: "Standard");

        var posture = await NetworkPostureAsync(runId, teamId, deploymentCeiling: "Standard");

        posture.ShouldBe("Network: clamped off by deployment ceiling (Standard)" + AgentAutonomyPolicy.ConfinementCaveat,
            customMessage: "the run's journal has to state WHY these agents had no internet — an unexplained 'off' is the silence this row exists to end");
    }

    [Fact]
    public async Task The_room_stays_silent_about_a_ceiling_that_bound_nothing()
    {
        // The other half of the same honesty: with no clamp to report, the authored run's Launch row is absent
        // exactly as before — the projector never states a posture nobody bounded.
        var (runId, teamId, _) = await StageAuthoredRunAsync(deploymentCeiling: null);

        (await NetworkPostureAsync(runId, teamId, deploymentCeiling: null)).ShouldBeNull();
    }

    [Fact]
    public async Task A_replay_of_a_networked_run_is_clamped_by_the_ceiling_that_stands_now()
    {
        // A replay re-executes the FROZEN definition snapshot: the tier and the network override come back verbatim,
        // and no route is consulted again. Without the node-level clamp, every run staged before an operator lowered
        // the ceiling would keep re-earning the egress that operator has since forbidden — forever, on demand.
        var (originalRunId, teamId, userId) = await StageAuthoredRunAsync(deploymentCeiling: null);

        (await StagedTaskAsync(originalRunId)).Permissions.Network.ShouldBe(AgentNetworkAccess.On, "the original really did have egress — otherwise the replay proves nothing");

        var jobClient = ResolveJobClient();
        jobClient.Clear();
        jobClient.AutoExecute = false;

        try
        {
            Guid replayRunId;

            using (RuntimeSettings.Override(Read("Standard")))
            {
                using (var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin))
                    replayRunId = await scope.Resolve<IMediator>().Send(new ReplayRunCommand { OriginalRunId = originalRunId });

                await RunEngineAsync(replayRunId);
            }

            var replayed = await StagedTaskAsync(replayRunId);

            replayed.Autonomy.ShouldBe(AgentAutonomyLevel.Standard);
            replayed.Permissions.Network.ShouldBe(AgentNetworkAccess.Off,
                customMessage: "the replay re-executes the same frozen node under TODAY's ceiling — a snapshot cannot carry a grant the deployment has withdrawn");
        }
        finally
        {
            jobClient.AutoExecute = true;
        }
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────

    /// <summary>Stage the authored workflow under the given deployment ceiling and read back the REAL persisted <see cref="AgentTask"/> — the permissions the sandbox enforces, never the authored config string.</summary>
    private async Task<AgentTask> StageAuthoredAsync(string? deploymentCeiling)
    {
        var (runId, _, _) = await StageAuthoredRunAsync(deploymentCeiling);

        return await StagedTaskAsync(runId);
    }

    /// <summary>
    /// Run the authored <c>agent.run</c> workflow to its suspension under <paramref name="deploymentCeiling"/>. The
    /// override is an <c>AsyncLocal</c> scope, so it must wrap the ENGINE call itself (which is what runs the node),
    /// not merely the staging around it.
    /// </summary>
    private async Task<(Guid RunId, Guid TeamId, Guid UserId)> StageAuthoredRunAsync(string? deploymentCeiling)
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowId = await CreateWorkflowAsync(teamId, userId, NetworkedAgentNodeDefinition());

        // Staged through the REAL manual-run command, not a seeded row: that seam is what opens the run's work
        // session, and without one there is no room to read a posture out of.
        Guid runId;
        using (var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin))
            runId = await scope.Resolve<IMediator>().Send(new RunWorkflowManuallyCommand { WorkflowId = workflowId });

        var jobClient = ResolveJobClient();
        jobClient.Clear();
        jobClient.AutoExecute = false;   // record the executor dispatch; the real (binary-less) harness must not run

        try
        {
            using (RuntimeSettings.Override(Read(deploymentCeiling))) await RunEngineAsync(runId);
        }
        finally
        {
            jobClient.AutoExecute = true;
        }

        return (runId, teamId, userId);
    }

    private async Task<AgentTask> StagedTaskAsync(Guid runId)
    {
        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();

        var agentRun = await db.AgentRun.AsNoTracking().SingleAsync(r => r.WorkflowRunId == runId);

        return JsonSerializer.Deserialize<AgentTask>(agentRun.TaskJson, AgentJson.Options)!;
    }

    /// <summary>The Launch row's posture sentence as the REAL <see cref="IRoomProjector"/> renders it; null when the room states none.</summary>
    private async Task<string?> NetworkPostureAsync(Guid runId, Guid teamId, string? deploymentCeiling)
    {
        using var scope = _fixture.BeginScope();

        using (RuntimeSettings.Override(Read(deploymentCeiling)))
        {
            var room = await scope.Resolve<IRoomProjector>().ProjectByRunAsync(runId, teamId, CancellationToken.None);

            room.ShouldNotBeNull("an authored workflow run belongs to a Workflow-kind session, so it must project to a room");

            // The stat rows live INSIDE the turn block, which is what the Room actually renders.
            return room.Blocks.OfType<AssistantTurnBlock>()
                .SelectMany(t => t.Blocks)
                .OfType<StatBlock>()
                .SingleOrDefault(b => b.Kind == "launch")?.Detail;
        }
    }

    /// <summary>The authored node this whole file is about: a tier that grants egress PLUS the raw per-field override, which is the combination no route ever bounds.</summary>
    private static WorkflowDefinition NetworkedAgentNodeDefinition() => new()
    {
        SchemaVersion = 1,
        Nodes = new List<NodeDefinition>
        {
            new() { Id = "start", TypeKey = "trigger.manual", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "agent", TypeKey = "agent.run",
                    Config = WorkflowsTestSeed.Json("""{"goal":"Install the dependencies and push the branch","harness":"codex-cli","model":"gpt-5.3-codex","runnerKind":"local","autonomyLevel":"Trusted","network":true}"""),
                    Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "end", TypeKey = "builtin.terminal", Config = WorkflowsTestSeed.EmptyJson(),
                    Inputs = WorkflowsTestSeed.Json("""{"summary":"{{nodes.agent.outputs.summary}}"}""") },
        },
        Edges = new List<EdgeDefinition> { new() { From = "start", To = "agent" }, new() { From = "agent", To = "end" } },
    };

    private async Task<Guid> CreateWorkflowAsync(Guid teamId, Guid userId, WorkflowDefinition definition)
    {
        using var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin);

        return await scope.Resolve<IMediator>().Send(new CreateWorkflowCommand
        {
            Name = "ceiling-" + Guid.NewGuid().ToString("N")[..6],
            Description = null,
            Definition = definition,
            Activations = new List<WorkflowActivationInput>(),
            Enabled = true,
        });
    }

    private async Task RunEngineAsync(Guid runId)
    {
        using var scope = _fixture.BeginScope();
        await scope.Resolve<IWorkflowEngine>().ExecuteRunAsync(runId, CancellationToken.None);
    }

    private InMemoryBackgroundJobClient ResolveJobClient()
    {
        using var scope = _fixture.BeginScope();
        return scope.Resolve<InMemoryBackgroundJobClient>();
    }

    private static RuntimeSettings Read(string? configured) =>
        RuntimeSettings.Read(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [RuntimeSettings.MaxAutonomyKey] = configured })
            .Build());
}

/// <summary>
/// The same deployment ceiling over the WIDEST of the unbounded lanes: an <c>agent.supervisor</c> node whose
/// profile tier is frozen into its config at authoring, read back VERBATIM on every rehydrate, and then FANNED OUT
/// to every agent the model decides to plan. One authored tier, N sandboxes, and no <c>RouteCaps</c> anywhere in
/// the path — so before this clamp a single hand-authored supervisor node (or a replay of any supervisor run)
/// handed N agents the egress the operator had forbidden.
///
/// <para><b>Fidelity (Rule 12) — medium-mock:</b> the real engine + the real supervisor turn service + the real
/// action executor + real Postgres stage the spawns and PERSIST the real <see cref="AgentTask"/> each agent would
/// be handed; the scripted decider stands in for the LLM and the harness never spawns
/// (<c>AutoExecute=false</c> — there is no codex binary in CI), exactly as in <c>SupervisorSpawnFlowTests</c>. The
/// assertion is on the persisted permission set, which is precisely what the sandbox enforces.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class DeploymentAutonomyCeilingSupervisorFlowTests : IDisposable
{
    private readonly PostgresFixture _fixture;

    public DeploymentAutonomyCeilingSupervisorFlowTests(PostgresFixture fixture)
    {
        _fixture = fixture;

        using var scope = _fixture.BeginScope();
        scope.Resolve<SupervisorDecisionScript>().PlanSpawnStop();   // plan(2 subtasks) → spawn[both] → stop
    }

    public void Dispose()
    {
        using var scope = _fixture.BeginScope();
        scope.Resolve<SupervisorDecisionScript>().PlanThenStop();   // restore the default for sibling tests
    }

    [Fact]
    public async Task A_supervisor_profile_tier_reaches_every_spawn_at_the_committed_default()
    {
        // The control. The committed ceiling grants the top tier, so the fan-out is byte-identical to before this
        // setting existed — a ceiling that changed behaviour on arrival could not ship.
        var tasks = await SpawnedTasksAsync(deploymentCeiling: null);

        tasks.Count.ShouldBe(2, "the scripted plan spawns BOTH subtasks — the fan-out is the whole point of this lane");
        tasks.ShouldAllBe(t => t.Autonomy == AgentAutonomyLevel.Trusted);
        tasks.ShouldAllBe(t => t.Permissions.Network == AgentNetworkAccess.On);
    }

    [Fact]
    public async Task Every_agent_a_supervisor_fans_out_is_severed_under_a_lowered_deployment_ceiling()
    {
        var tasks = await SpawnedTasksAsync(deploymentCeiling: "Standard");

        tasks.Count.ShouldBe(2);
        tasks.ShouldAllBe(t => t.Autonomy == AgentAutonomyLevel.Standard);

        foreach (var task in tasks)
            task.Permissions.Network.ShouldBe(AgentNetworkAccess.Off,
                customMessage: "a Trusted supervisor profile under a Standard deployment ceiling must stage EVERY spawn without egress — the tier is read ONCE and derives every spawned agent's permissions, so a miss here is N sandboxes wide, not one");
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Drive the supervisor to its spawn park under <paramref name="deploymentCeiling"/> and read back the REAL
    /// persisted <see cref="AgentTask"/> of every agent it staged. The override is an <c>AsyncLocal</c> scope, so it
    /// wraps the ENGINE calls themselves — turn 0 (plan) self-advances, turn 1 spawns.
    /// </summary>
    private async Task<IReadOnlyList<AgentTask>> SpawnedTasksAsync(string? deploymentCeiling)
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowId = await CreateWorkflowAsync(teamId, userId);
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);

        var jobClient = ResolveJobClient();
        jobClient.Clear();
        jobClient.AutoExecute = false;   // record the executor dispatch; the real (binary-less) harness must not run

        try
        {
            using (RuntimeSettings.Override(Read(deploymentCeiling)))
            {
                await RunEngineAsync(runId);            // turn 0: plan
                await ResolveSelfAdvanceAsync(runId);
                await RunEngineAsync(runId);            // turn 1: spawn[both]
            }
        }
        finally
        {
            jobClient.AutoExecute = true;
        }

        using var verify = _fixture.BeginScope();

        var staged = await verify.Resolve<CodeSpaceDbContext>().AgentRun.AsNoTracking()
            .Where(r => r.WorkflowRunId == runId && r.TeamId == teamId)
            .OrderBy(r => r.CreatedDate)
            .Select(r => r.TaskJson)
            .ToListAsync();

        return staged.Select(json => JsonSerializer.Deserialize<AgentTask>(json, AgentJson.Options)!).ToList();
    }

    /// <summary>Turn 0 parks a self-advance wait; resolving it is the exact entry point the engine enqueues (the post-commit re-dispatch is record-only with AutoExecute off).</summary>
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

    /// <summary>manual → sup (agent.supervisor, profile pinned to the egress-granting Trusted tier) → terminal. The tier no route ever sees.</summary>
    private async Task<Guid> CreateWorkflowAsync(Guid teamId, Guid userId)
    {
        var definition = new WorkflowDefinition
        {
            SchemaVersion = 1,
            Nodes = new List<NodeDefinition>
            {
                new() { Id = "start", TypeKey = "trigger.manual", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
                new() { Id = "sup", TypeKey = "agent.supervisor",
                        Config = WorkflowsTestSeed.Json("""{"goal":"ship the feature","agentProfile":{"harness":"codex-cli","runnerKind":"local","autonomyLevel":"Trusted"}}"""),
                        Inputs = WorkflowsTestSeed.EmptyJson() },
                new() { Id = "end", TypeKey = "builtin.terminal", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
            },
            Edges = new List<EdgeDefinition> { new() { From = "start", To = "sup" }, new() { From = "sup", To = "end" } },
        };

        using var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin);

        return await scope.Resolve<IMediator>().Send(new CreateWorkflowCommand
        {
            Name = "ceiling-sup-" + Guid.NewGuid().ToString("N")[..6],
            Description = null,
            Definition = definition,
            Activations = new List<WorkflowActivationInput>(),
            Enabled = true,
        });
    }

    private async Task RunEngineAsync(Guid runId)
    {
        using var scope = _fixture.BeginScope();
        await scope.Resolve<IWorkflowEngine>().ExecuteRunAsync(runId, CancellationToken.None);
    }

    private InMemoryBackgroundJobClient ResolveJobClient()
    {
        using var scope = _fixture.BeginScope();
        return scope.Resolve<InMemoryBackgroundJobClient>();
    }

    private static RuntimeSettings Read(string? configured) =>
        RuntimeSettings.Read(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [RuntimeSettings.MaxAutonomyKey] = configured })
            .Build());
}
