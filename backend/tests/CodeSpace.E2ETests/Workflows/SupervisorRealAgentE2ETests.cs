using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Supervisor;
using CodeSpace.Core.Services.Workflows.Engine;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Infrastructure.Jobs;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Commands.Workflows;
using CodeSpace.Messages.Dtos.Workflows;
using CodeSpace.Messages.Enums;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.E2ETests.Workflows;

/// <summary>
/// 🟢 THE supervisor→real-agent seam, driven top-to-bottom as ONE engine run — the join a verified self-review
/// found had NEVER executed end-to-end at any tier. Every prior supervisor flow test
/// (<see cref="SupervisorSpawnFlowTests"/>) runs the agents with <c>AutoExecute=false</c> +
/// <c>SimulateAgentCompletionAsync</c> (a hand-fabricated MarkRunning→Complete→Notify that BYPASSES the whole
/// <see cref="AgentRunExecutor"/> — the harness/runner spawn, the real <c>LocalProcessRunner</c>, ParseEvent,
/// BuildResult, the fenced CompleteAsync). This test deletes that bypass: the 2 supervisor-spawned agents run
/// through the PRODUCTION executor + runner + a real OS process, and the wait-for-all barrier resumes the
/// supervisor from a REAL completion-notifier call, not a simulation.
///
/// <para><c>trigger.manual</c> → <c>agent.supervisor</c> (the scripted decider's E3 arc: turn 0 plan(2 subtasks)
/// → SELF-ADVANCES; turn 1 spawn[both] → stages 2 REAL agent runs + parks 2 <c>AgentRun</c> waits keyed
/// <c>sup#turn1#0/#1</c>; the WAIT-FOR-ALL barrier holds the supervisor Suspended until BOTH agents complete for
/// real, then resumes → turn 2 stop → run Success). With the in-memory job client's <c>AutoExecute</c> ON, the
/// supervisor's self-advance (turn 0→1), the spawn-turn executor dispatch, AND the barrier-driven turn 1→2 resume
/// all CHAIN through the deferred job queue, so a single <c>RunEngineAsync</c> + <c>WaitForPendingAsync</c> drains
/// the whole arc to Success deterministically (each enqueue appends to the same FIFO queue the drain loop walks).</para>
///
/// <para><b>Fidelity (Rule 12) — HIGH tier.</b> Real engine (CAS suspend/resume, the supervisor turn loop, the
/// wait-for-all barrier), real Postgres, the real <see cref="SupervisorTurnService"/> +
/// <see cref="RealSupervisorActionExecutor"/>, the real <see cref="AgentRunService"/> state machine, the real
/// <see cref="AgentRunExecutor"/> + the real <c>LocalProcessRunner</c> SPAWNING A REAL OS PROCESS, and the
/// harness's real ParseEvent/BuildResult are ALL real. Two things are faked at HONEST boundaries: (1) the
/// supervisor's decision-making is faked at the <see cref="ISupervisorDecider"/> seam
/// (<see cref="ScriptedSupervisorDecider"/>) — the supervisor node still routes through the real turn loop +
/// ledger; (2) the CLI's INTELLIGENCE is faked at the binary itself (<see cref="SubtaskAwareFakeCli"/> stands in
/// for codex so no API key / network is needed) — the executor still drives it through the entire production
/// execution pipeline. A break in ANY seam (plan → spawn staging → real branch execution → barrier resume →
/// stop) fails this test.</para>
///
/// <para>POSIX-only: the fake CLI is a <c>/bin/sh</c> script the runner spawns. Skipped on Windows (Rule 12.1),
/// where the agent-execution suites don't run anyway.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "E2E")]
[Trait("Surface", "Engine")]
public sealed class SupervisorRealAgentE2ETests : IDisposable
{
    private readonly PostgresFixture _fixture;

    public SupervisorRealAgentE2ETests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    public void Dispose()
    {
        // Reset the fixture-singleton decision script to the default for sibling tests,
        // even on the failure path (mirrors SupervisorSpawnFlowTests so no global state leaks).
        using var scope = _fixture.BeginScope();
        scope.Resolve<SupervisorDecisionScript>().PlanThenStop();
        scope.Resolve<InMemoryBackgroundJobClient>().AutoExecute = true;   // restore the shared fixture default (mirrors SupervisorScorecardFlowTests)
    }

    [Fact]
    public async Task Supervisor_spawns_two_real_agents_that_execute_through_the_real_pipeline_and_the_barrier_resumes_to_stop()
    {
        if (OperatingSystem.IsWindows()) return;   // the fake CLI is a /bin/sh script the runner spawns

        // The CLI's intelligence is faked at the binary; everything the executor/runner/harness does to drive it
        // is real. One script serves both spawned agents — the per-agent goal arg is the differentiator.
        using var cli = new SubtaskAwareFakeCli();

        SetDecisionScriptToPlanSpawnStop();

        var jobClient = ResolveJobClient();
        jobClient.Clear();
        jobClient.AutoExecute = true;   // the spawn dispatch runs the REAL AgentRunExecutor + real runner + fake CLI

        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowId = await CreateWorkflowAsync(teamId, userId);
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);

        // ── Pass 1: turn 0 plan parks on a SupervisorDecision self-advance wait; the run suspends + the
        //    self-advance resume is enqueued. ──
        await RunEngineAsync(runId);

        // ── Drain the WHOLE deferred chain in one call: the turn-0 self-advance resume → turn 1 spawn (stages 2
        //    real agent runs + dispatches their executors) → each executor spawns the fake CLI through the real
        //    runner + completes for real → the wait-for-all barrier resumes only after the LAST agent → turn 2
        //    stop → Success. Every step enqueues onto the same FIFO queue WaitForPendingAsync walks. ──
        await jobClient.WaitForPendingAsync();

        await AssertRunReachedSuccessAsync(runId);
        await AssertTwoRealAgentRunsSucceededAsync(runId);
        await AssertEachAgentHasRealCompletedEventAsync(runId, teamId);
        await AssertSpawnedSummariesMatchTheFakeCliAsync(runId);
        await AssertDecisionLedgerIsPlanSpawnStopAsync(runId, teamId);
        AssertBothAgentsDispatchedThroughTheRealExecutor(jobClient, runId);
    }

    // ─── Assertions ──────────────────────────────────────────────────────────────────

    private async Task AssertRunReachedSuccessAsync(Guid runId)
    {
        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();

        var run = await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId);
        run.Status.ShouldBe(WorkflowRunStatus.Success,
            customMessage: "the supervisor→real-agent→barrier→stop arc must reach Success; if not, inspect the AgentRun.Error for this run + the failed WorkflowRunNode rows (the real executor/runner/fake-CLI path is the likely break)");
    }

    private async Task AssertTwoRealAgentRunsSucceededAsync(Guid runId)
    {
        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();

        var agentRuns = await db.AgentRun.AsNoTracking().Where(r => r.WorkflowRunId == runId).ToListAsync();
        agentRuns.Count.ShouldBe(2, "spawn[both] staged exactly 2 real agent runs — no extra runs, no missing run");
        agentRuns.ShouldAllBe(r => r.Status == AgentRunStatus.Succeeded, "both spawned agents executed to Succeeded via the REAL executor + runner — not a SimulateAgentCompletionAsync stand-in");
        agentRuns.ShouldAllBe(r => r.ResultJson != null, "each run persisted a real folded AgentRunResult the executor's BuildResult produced from the real fake-CLI event stream");
    }

    private async Task AssertEachAgentHasRealCompletedEventAsync(Guid runId, Guid teamId)
    {
        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();
        var runs = verify.Resolve<IAgentRunService>();

        var agentRunIds = await db.AgentRun.AsNoTracking().Where(r => r.WorkflowRunId == runId).Select(r => r.Id).ToListAsync();

        foreach (var agentRunId in agentRunIds)
        {
            var events = await runs.GetEventsAsync(agentRunId, teamId, 0, CancellationToken.None);
            events.ShouldContain(e => e.Kind == AgentEventKind.Completed,
                customMessage: $"agent run {agentRunId} should carry the real task_complete event the fake CLI emitted + the harness parsed — its absence means the run was hand-completed, not executed through the real runner/harness");
        }
    }

    private async Task AssertSpawnedSummariesMatchTheFakeCliAsync(Guid runId)
    {
        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();

        var summaries = await ResultSummariesAsync(db, runId);

        // The supervisor's BuildAgentTask sets each spawned agent's Goal to its planned subtask Instruction
        // ("do alpha" for sa, "do beta" for sb — RealSupervisorActionExecutor.Spawn.BuildAgentTask), which the
        // harness passes as Codex's LAST positional arg, which the fake CLI stamps into "DONE: <goal>". So the
        // two real summaries are deterministically the fake-CLI transform of those exact goals.
        summaries.ShouldBe(
            new[] { SubtaskAwareFakeCli.ExpectedSummaryFor("do alpha"), SubtaskAwareFakeCli.ExpectedSummaryFor("do beta") },
            ignoreOrder: true,
            customMessage: "the two real folded summaries must be the fake-CLI transform of the planned subtask instructions 'do alpha'/'do beta' — a mismatch means the goal never reached the real CLI or BuildResult didn't fold the real event stream");
    }

    private async Task AssertDecisionLedgerIsPlanSpawnStopAsync(Guid runId, Guid teamId)
    {
        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();

        IReadOnlyList<string> kinds = await db.SupervisorDecisionRecord.AsNoTracking()
            .Where(d => d.SupervisorRunId == runId && d.TeamId == teamId)
            .OrderBy(d => d.Sequence)
            .Select(d => d.DecisionKind)
            .ToListAsync();

        kinds.ShouldBe(
            new[] { SupervisorDecisionKinds.Plan, SupervisorDecisionKinds.Spawn, SupervisorDecisionKinds.Stop },
            customMessage: "the decision ledger must record plan/spawn/stop in Sequence order — proving the supervisor advanced all three turns (the spawn turn only advanced because both real agents completed through the barrier)");
    }

    private static void AssertBothAgentsDispatchedThroughTheRealExecutor(InMemoryBackgroundJobClient jobClient, Guid runId)
    {
        var dispatched = jobClient.Calls
            .Where(c => c.ServiceType == typeof(IAgentRunExecutor) && c.MethodName == nameof(IAgentRunExecutor.ExecuteAsync) && c.RunId != null)
            .Select(c => c.RunId!.Value)
            .Distinct()
            .ToList();

        dispatched.Count.ShouldBe(2,
            customMessage: "exactly 2 IAgentRunExecutor.ExecuteAsync jobs must have been enqueued — proving the spawn turn dispatched both agents through the REAL executor path (DispatchPendingAgentRunAsync), not a simulated completion");
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────────────

    private static async Task<IReadOnlyList<string?>> ResultSummariesAsync(CodeSpaceDbContext db, Guid runId)
    {
        var resultJsons = await db.AgentRun.AsNoTracking().Where(r => r.WorkflowRunId == runId).Select(r => r.ResultJson).ToListAsync();

        return resultJsons
            .Select(json => json == null ? null : System.Text.Json.JsonSerializer.Deserialize<AgentRunResult>(json, AgentJson.Options)?.Summary)
            .ToList();
    }

    private void SetDecisionScriptToPlanSpawnStop()
    {
        using var scope = _fixture.BeginScope();
        scope.Resolve<SupervisorDecisionScript>().PlanSpawnStop();   // the E3 arc: plan → spawn → stop
    }

    private async Task<Guid> CreateWorkflowAsync(Guid teamId, Guid userId)
    {
        using var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin);
        return await scope.Resolve<IMediator>().Send(new CreateWorkflowCommand
        {
            Name = "sup-real-e2e-" + Guid.NewGuid().ToString("N")[..6],
            Description = null,
            Definition = SupervisorDefinition(),
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

    // manual → sup (agent.supervisor) → terminal (the SAME shape SupervisorSpawnFlowTests builds).
    private static WorkflowDefinition SupervisorDefinition() => new()
    {
        SchemaVersion = 1,
        Nodes = new List<NodeDefinition>
        {
            new() { Id = "start", TypeKey = "trigger.manual", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "sup", TypeKey = "agent.supervisor", Config = WorkflowsTestSeed.Json("""{"goal":"ship the feature"}"""), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "end", TypeKey = "builtin.terminal", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
        },
        Edges = new List<EdgeDefinition>
        {
            new() { From = "start", To = "sup" },
            new() { From = "sup", To = "end" },
        },
    };
}
