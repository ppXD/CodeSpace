using System.Diagnostics;
using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Agents.Sandbox;
using CodeSpace.Core.Services.Agents.Sandbox.Runners;
using CodeSpace.Core.Services.Workflows;
using CodeSpace.Core.Services.Workflows.Engine;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Infrastructure.Jobs;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Commands.Workflows;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Dtos.Workflows;
using CodeSpace.Messages.Enums;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows;

/// <summary>
/// PR-D4b operator cancel + kill-wave, driving the REAL WorkflowService through DI against real Postgres.
/// The headline target: a SUSPENDED flow.map fan-out (K parked branch AgentRuns) cancels cleanly — run →
/// Cancelled, every pending wait → Resolved, every Queued branch agent → Cancelled, a <c>run.cancelled</c>
/// ledger record emitted, and (critical) the reconciler does NOT subsequently re-launch any of them (the D1
/// parent-run-terminal guard holds). Plus: a Running-agent kill path with a real durable runner (TerminateAsync
/// reaps the process tree + status Cancelled), cross-team cancel REJECTED (fail-closed), and an already-terminal
/// run = clean no-op.
///
/// <para>Fidelity (Rule 12 🟡 medium-mock for the suspend setup, 🟢 high for the kill): every wiring class is
/// real — WorkflowService, AgentRunService, the engine, real Postgres rows; the binary-less harness is stood in
/// for by the job client's AutoExecute=off (mirrors <see cref="MapAgentResumeFlowTests"/>). The Running-agent
/// kill test launches a REAL sleeping process under the local durable runner and asserts it is actually reaped.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class OperatorCancelFlowTests : IDisposable
{
    private readonly PostgresFixture _fixture;
    private readonly List<string> _spoolDirs = new();
    private readonly List<int> _launchedPids = new();

    public OperatorCancelFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task Cancelling_a_suspended_map_fan_out_tears_down_run_waits_and_queued_branch_agents_and_emits_the_ledger_record()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowId = await CreateWorkflowAsync(teamId, userId, MapOverAgentCodeDefinition());
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId, payloadJson: """{ "things": ["a", "b"] }""");

        var jobClient = ResolveJobClient();
        jobClient.Clear();
        jobClient.AutoExecute = false;   // record dispatches; the binary-less harness must not run

        try
        {
            // Pass 1: both branches' agent.code nodes park → two Queued AgentRun waits, the run Suspended.
            await RunEngineAsync(runId);

            var agent0 = await BranchAgentRunIdAsync(runId, "map#0");
            var agent1 = await BranchAgentRunIdAsync(runId, "map#1");

            using (var verify = _fixture.BeginScope())
                (await verify.Resolve<CodeSpaceDbContext>().WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId)).Status
                    .ShouldBe(WorkflowRunStatus.Suspended, "precondition: the fan-out parked on its two branch agent waits");

            // ── THE CANCEL ──
            CancelRunOutcome? outcome;
            using (var scope = _fixture.BeginScope())
                outcome = await scope.Resolve<IWorkflowService>().CancelRunAsync(runId, teamId, CancellationToken.None);

            outcome.ShouldNotBeNull();
            outcome!.Cancelled.ShouldBeTrue();
            outcome.Status.ShouldBe(WorkflowRunStatus.Cancelled);
            outcome.AgentRunsCancelled.ShouldBe(2, "the kill-wave aborted both Queued branch agents");

            using (var verify = _fixture.BeginScope())
            {
                var db = verify.Resolve<CodeSpaceDbContext>();

                (await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId)).Status.ShouldBe(WorkflowRunStatus.Cancelled);

                var pendingWaits = await db.WorkflowRunWait.AsNoTracking()
                    .CountAsync(w => w.RunId == runId && w.Status == WorkflowWaitStatuses.Pending);
                pendingWaits.ShouldBe(0, "every still-pending wait is resolved so none dangle");

                foreach (var id in new[] { agent0, agent1 })
                    (await db.AgentRun.AsNoTracking().SingleAsync(r => r.Id == id)).Status
                        .ShouldBe(AgentRunStatus.Cancelled, "each Queued branch agent was killed by the kill-wave");

                var ledger = await db.WorkflowRunRecord.AsNoTracking()
                    .AnyAsync(r => r.RunId == runId && r.RecordType == WorkflowRunRecordTypes.RunCancelled);
                ledger.ShouldBeTrue("a run.cancelled ledger record is emitted for the operator cancel");
            }

            // ── THE D1 GUARD: backdate the cancelled branch agents past the liveness window + run the REAL reconciler.
            //    It must NOT re-dispatch/launch any of them (the parent run is terminal). ──
            await BackdateAgentRunCreatedAsync(new[] { agent0, agent1 }, AgentRunLiveness.Window + TimeSpan.FromMinutes(5));
            jobClient.Clear();

            using (var scope = _fixture.BeginScope())
                await scope.Resolve<IAgentRunReconcilerService>().ReconcileAsync(CancellationToken.None);

            var relaunched = jobClient.Calls
                .Where(c => c.ServiceType == typeof(IAgentRunExecutor) && c.MethodName == nameof(IAgentRunExecutor.ExecuteAsync))
                .Select(c => c.RunId).ToList();
            relaunched.ShouldNotContain(agent0, "the reconciler must NOT re-launch a cancelled run's branch agent (D1 guard)");
            relaunched.ShouldNotContain(agent1, "the reconciler must NOT re-launch a cancelled run's branch agent (D1 guard)");

            using (var verify = _fixture.BeginScope())
                foreach (var id in new[] { agent0, agent1 })
                    (await verify.Resolve<CodeSpaceDbContext>().AgentRun.AsNoTracking().SingleAsync(r => r.Id == id)).Status
                        .ShouldBe(AgentRunStatus.Cancelled, "the branch agents stay Cancelled — the reconciler leaves a terminal run's agents alone");
        }
        finally
        {
            jobClient.AutoExecute = true;
        }
    }

    [Fact]
    public async Task Cancelling_a_run_with_a_running_branch_agent_kills_its_durable_process_and_marks_it_cancelled()
    {
        if (OperatingSystem.IsWindows()) return;

        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowId = await CreateWorkflowAsync(teamId, userId, MapOverAgentCodeDefinition());
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId, payloadJson: """{ "things": ["a"] }""");

        var jobClient = ResolveJobClient();
        jobClient.Clear();
        jobClient.AutoExecute = false;

        try
        {
            await RunEngineAsync(runId);   // one branch parks a Queued AgentRun wait; the run suspends
            var agentId = await BranchAgentRunIdAsync(runId, "map#0");

            // Drive the branch agent to RUNNING with a REAL detached durable process (a sleeper) + persist its handle —
            // the live-agent post-launch state. The kill-wave's CancelRunningAsync must reap THIS process.
            var pid = await MarkRunningWithRealDurableProcessAsync(agentId);
            ProcessAlive(pid).ShouldBeTrue("precondition: the branch agent's durable process is running before the cancel");

            CancelRunOutcome? outcome;
            using (var scope = _fixture.BeginScope())
                outcome = await scope.Resolve<IWorkflowService>().CancelRunAsync(runId, teamId, CancellationToken.None);

            outcome!.Cancelled.ShouldBeTrue();
            outcome.AgentRunsCancelled.ShouldBe(1, "the running branch agent was aborted by the kill-wave");

            using (var verify = _fixture.BeginScope())
                (await verify.Resolve<CodeSpaceDbContext>().AgentRun.AsNoTracking().SingleAsync(r => r.Id == agentId)).Status
                    .ShouldBe(AgentRunStatus.Cancelled, "a Running branch agent is CANCELLED (a deliberate cancel), not Failed");

            (await WaitForProcessGoneAsync(pid)).ShouldBeTrue(
                "CancelRunningAsync must TerminateAsync the sandbox process tree — check the supervisor pid is reaped");
        }
        finally
        {
            jobClient.AutoExecute = true;
        }
    }

    [Fact]
    public async Task A_different_teams_run_cannot_be_cancelled_and_is_left_untouched()
    {
        var (ownerTeamId, ownerUserId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var (otherTeamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);

        var workflowId = await CreateWorkflowAsync(ownerTeamId, ownerUserId, MapOverAgentCodeDefinition());
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, ownerTeamId, payloadJson: """{ "things": ["a"] }""");

        var jobClient = ResolveJobClient();
        jobClient.Clear();
        jobClient.AutoExecute = false;

        try
        {
            await RunEngineAsync(runId);   // the owner's run suspends on its branch agent wait

            CancelRunOutcome? outcome;
            using (var scope = _fixture.BeginScope())
                outcome = await scope.Resolve<IWorkflowService>().CancelRunAsync(runId, otherTeamId, CancellationToken.None);

            outcome.ShouldBeNull("a cross-team cancel is fail-closed — null (404), never a silent success or a leak");

            using (var verify = _fixture.BeginScope())
                (await verify.Resolve<CodeSpaceDbContext>().WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId)).Status
                    .ShouldBe(WorkflowRunStatus.Suspended, "the foreign cancel left the owner's run completely untouched");
        }
        finally
        {
            jobClient.AutoExecute = true;
        }
    }

    [Theory]
    [InlineData(WorkflowRunStatus.Success)]
    [InlineData(WorkflowRunStatus.Failure)]
    [InlineData(WorkflowRunStatus.Cancelled)]
    public async Task Cancelling_an_already_terminal_run_is_a_clean_no_op_reporting_the_existing_status(WorkflowRunStatus terminal)
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowId = await CreateWorkflowAsync(teamId, userId, MapOverAgentCodeDefinition());
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId, payloadJson: """{ "things": ["a"] }""");

        await SetRunStatusAsync(runId, terminal);

        CancelRunOutcome? outcome;
        using (var scope = _fixture.BeginScope())
            outcome = await scope.Resolve<IWorkflowService>().CancelRunAsync(runId, teamId, CancellationToken.None);

        outcome.ShouldNotBeNull("an already-terminal run is still the caller's run — not a 404");
        outcome!.Cancelled.ShouldBeFalse("cancelling a terminal run is an idempotent no-op");
        outcome.Status.ShouldBe(terminal, "the no-op reports the run's EXISTING terminal status, not Cancelled");
        outcome.AgentRunsCancelled.ShouldBe(0);

        using (var verify = _fixture.BeginScope())
            (await verify.Resolve<CodeSpaceDbContext>().WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId)).Status
                .ShouldBe(terminal, "the terminal run is untouched");
    }

    // ─── Helpers ────────────────────────────────────────────────────────────────

    /// <summary>Flip a branch agent to Running with a REAL detached sleeper under the local durable runner, persisting its handle so the kill-wave's CancelRunningAsync has a live process tree to TerminateAsync. Returns the supervisor pid.</summary>
    private async Task<int> MarkRunningWithRealDurableProcessAsync(Guid agentId)
    {
        var workDir = Path.Combine(Path.GetTempPath(), "cs-cancel-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDir);
        _spoolDirs.Add(workDir);

        var spec = new SandboxSpec { Command = "/bin/sh", Args = new[] { "-c", "sleep 300" }, WorkingDirectory = workDir, TimeoutSeconds = 300 };

        SandboxHandle handle;
        using (var scope = _fixture.BeginScope())
            handle = await ((ISandboxDurableRunner)scope.Resolve<ISandboxRunnerRegistry>().Resolve(LocalProcessRunner.LocalKind))
                .LaunchAsync(spec, agentId.ToString("N"), CancellationToken.None);

        _spoolDirs.Add(handle.SpoolDirectory);
        _launchedPids.Add(handle.ProcessId);

        using (var scope = _fixture.BeginScope())
        {
            var runs = scope.Resolve<IAgentRunService>();
            await runs.MarkRunningAsync(agentId, CancellationToken.None);
            await runs.SetRunnerHandleAsync(agentId, JsonSerializer.Serialize(handle, AgentJson.Options), CancellationToken.None);
        }

        return handle.ProcessId;
    }

    private async Task<Guid> CreateWorkflowAsync(Guid teamId, Guid userId, WorkflowDefinition definition)
    {
        using var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin);
        return await scope.Resolve<IMediator>().Send(new CreateWorkflowCommand
        {
            Name = "op-cancel-" + Guid.NewGuid().ToString("N")[..6],
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

    private async Task<Guid> BranchAgentRunIdAsync(Guid runId, string iterationKey)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var token = await db.WorkflowRunWait.AsNoTracking()
            .Where(w => w.RunId == runId && w.IterationKey == iterationKey && w.WaitKind == WorkflowWaitKinds.AgentRun)
            .Select(w => w.Token).SingleAsync();
        return Guid.Parse(token);
    }

    private async Task SetRunStatusAsync(Guid runId, WorkflowRunStatus status)
    {
        using var scope = _fixture.BeginScope();
        await scope.Resolve<CodeSpaceDbContext>().WorkflowRun
            .Where(r => r.Id == runId)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, status));
    }

    private async Task BackdateAgentRunCreatedAsync(IEnumerable<Guid> agentRunIds, TimeSpan ago)
    {
        var ids = agentRunIds.ToList();
        using var scope = _fixture.BeginScope();
        await scope.Resolve<CodeSpaceDbContext>().AgentRun
            .Where(r => ids.Contains(r.Id))
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.CreatedDate, DateTimeOffset.UtcNow - ago));
    }

    private static bool ProcessAlive(int pid)
    {
        try { using var p = Process.GetProcessById(pid); return !p.HasExited; }
        catch { return false; }
    }

    private static async Task<bool> WaitForProcessGoneAsync(int pid)
    {
        for (var i = 0; i < 50 && ProcessAlive(pid); i++) await Task.Delay(100);
        return !ProcessAlive(pid);
    }

    public void Dispose()
    {
        foreach (var pid in _launchedPids)
            try { using var p = Process.GetProcessById(pid); if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { /* best-effort */ }

        foreach (var dir in _spoolDirs)
            try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ }
    }

    // manual → map(items={{trigger.things}}; body: ms → agent[REAL agent.code]) → terminal. Each branch parks a real AgentRun wait.
    private static WorkflowDefinition MapOverAgentCodeDefinition() => new()
    {
        SchemaVersion = 1,
        Nodes = new List<NodeDefinition>
        {
            new() { Id = "start", TypeKey = "trigger.manual", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "map", TypeKey = "flow.map", Config = WorkflowsTestSeed.EmptyJson(),
                    Inputs = WorkflowsTestSeed.Json("""{ "items": "{{trigger.things}}" }""") },
            new() { Id = "ms", TypeKey = "flow.map_start", ParentId = "map", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "agent", TypeKey = "agent.code", ParentId = "map",
                    Config = WorkflowsTestSeed.Json("""{"goal":"Work on {{item}}","harness":"codex-cli","model":"gpt-5.3-codex","runnerKind":"local","readOnly":true}"""),
                    Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "end", TypeKey = "builtin.terminal", Config = WorkflowsTestSeed.EmptyJson(),
                    Inputs = WorkflowsTestSeed.Json("""{ "count": "{{nodes.map.outputs.count}}" }""") },
        },
        Edges = new List<EdgeDefinition>
        {
            new() { From = "start", To = "map" },
            new() { From = "map", To = "end" },
            new() { From = "ms", To = "agent" },
        },
    };
}
