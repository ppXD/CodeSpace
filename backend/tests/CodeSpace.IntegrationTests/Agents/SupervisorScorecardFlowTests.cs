using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Agents.Eval;
using CodeSpace.Core.Services.Supervisor;
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
using CodeSpace.Messages.Queries.Agents;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.Agents;

/// <summary>
/// 🟢 PR-E E6 high-fidelity end-to-end: drives a REAL flag-on supervisor run (REAL engine +
/// <see cref="SupervisorTurnService"/> + <see cref="RealSupervisorActionExecutor"/> + REAL agent completion +
/// barrier over real Postgres; the scripted decider stands in for the LLM — the E3 plan→spawn→stop arc), then
/// proves the scorecard reads its REAL computed score off the durable ledger + real agent terminals:
/// <list type="bullet">
///   <item>decisions counted (plan/spawn/stop); spawned agents = 2; spawn success rate = 0.5 from the REAL agent
///         terminals (one Succeeded, one Failed) — NOT the decider's self-report; outcome = completed (the real
///         terminal stop label); the run is scored (terminal).</item>
///   <item>tenancy: a DIFFERENT team's scorecard sees NONE of it.</item>
/// </list>
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class SupervisorScorecardFlowTests : IDisposable
{
    private readonly PostgresFixture _fixture;

    public SupervisorScorecardFlowTests(PostgresFixture fixture)
    {
        _fixture = fixture;

        using var scope = _fixture.BeginScope();
        scope.Resolve<SupervisorDecisionScript>().PlanSpawnStop();   // plan → spawn → stop
    }

    public void Dispose()
    {
        using var scope = _fixture.BeginScope();
        scope.Resolve<SupervisorDecisionScript>().PlanThenStop();
        scope.Resolve<InMemoryBackgroundJobClient>().AutoExecute = true;   // restore the shared fixture default for sibling tests
    }

    [Fact]
    public async Task A_real_terminal_supervisor_run_is_scored_with_ground_truth_spawn_success_and_outcome()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);

        var (runId, agent0, agent1) = await DriveSupervisorToSpawnParkAsync(teamId, userId);

        // One spawned agent SUCCEEDS, the other FAILS — both terminal, so the barrier resumes → turn 2 stop →
        // the run completes. The scorecard's spawn success must reflect 1-of-2 from the REAL agent terminals.
        await SimulateAgentCompletionAsync(agent0, AgentRunStatus.Succeeded);
        await SimulateAgentCompletionAsync(agent1, AgentRunStatus.Failed);
        await RunEngineAsync(runId);

        using (var verify = _fixture.BeginScope())
        {
            (await verify.Resolve<CodeSpaceDbContext>().WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId)).Status
                .ShouldBe(WorkflowRunStatus.Success, "both spawned agents terminated → the supervisor resumed → turn 2 stop → the run completes");
        }

        var card = await GetScorecardAsync(userId, teamId);

        var run = card.Runs.Single(r => r.SupervisorRunId == runId);
        run.NotScored.ShouldBeFalse("the run reached a terminal status");
        run.TotalDecisions.ShouldBe(3, "plan + spawn + stop");
        run.PlanCount.ShouldBe(1);
        run.SpawnCount.ShouldBe(1);
        run.StopCount.ShouldBe(1);
        run.SpawnedAgents.ShouldBe(2, "the spawn staged 2 real agent runs");
        run.SpawnSuccessRate.ShouldBe(0.5, "1 of the 2 spawned agents actually Succeeded — the REAL terminal, not the decider's word");
        run.Outcome.ShouldBe(SupervisorOutcomes.Completed, "the terminal stop label was 'completed'");
        run.TimeToStopSeconds.ShouldNotBeNull();

        card.Rollup.ScoredRuns.ShouldBe(1);
        card.Rollup.OverallSpawnSuccessRate.ShouldBe(0.5, "the cross-run ground-truth spawn success");
        card.Rollup.OutcomeDistribution[SupervisorOutcomes.Completed].ShouldBe(1);
    }

    [Fact]
    public async Task A_run_that_FAILS_with_no_supervisor_stop_decision_is_scored_but_NOT_reported_completed()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);

        var workflowId = await CreateWorkflowAsync(teamId, userId);
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);

        var jobClient = ResolveJobClient();
        jobClient.Clear();
        jobClient.AutoExecute = false;

        // ── Turn 1: the supervisor plans (one Succeeded ledger row) + parks on the self-advance wait. ──
        await RunEngineAsync(runId);

        // ── The ledger now has a plan row but NO supervisor stop decision. Force the run terminal-Failure (an
        //    abandoned / crashed run) — the exact reachable scenario the scorecard must NOT call completed. ──
        using (var fail = _fixture.BeginScope())
            await fail.Resolve<CodeSpaceDbContext>().WorkflowRun
                .Where(r => r.Id == runId)
                .ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, WorkflowRunStatus.Failure));

        using (var verify = _fixture.BeginScope())
        {
            (await verify.Resolve<CodeSpaceDbContext>().WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId)).Status
                .ShouldBe(WorkflowRunStatus.Failure, "the run died with a plan row but no stop decision — terminal Failure");
        }

        var card = await GetScorecardAsync(userId, teamId);

        var run = card.Runs.Single(r => r.SupervisorRunId == runId);
        run.NotScored.ShouldBeFalse("the run reached a terminal Failure — it is scored");
        run.StopCount.ShouldBe(0, "the run never recorded a supervisor stop decision");
        run.Outcome.ShouldBe(SupervisorOutcomes.Aborted, "a terminal Failure with no stop decision is aborted — never folded into completed");
        run.Outcome.ShouldNotBe(SupervisorOutcomes.Completed);

        card.Rollup.ScoredRuns.ShouldBe(1);
        card.Rollup.OutcomeDistribution.GetValueOrDefault(SupervisorOutcomes.Completed, 0)
            .ShouldBe(0, "no run landed in the completed bucket — the failed run must not inflate it");
        card.Rollup.OutcomeDistribution[SupervisorOutcomes.Aborted].ShouldBe(1);
    }

    [Theory]
    [InlineData(false, SupervisorOutcomes.AcceptanceFailed)]  // the model self-reported "completed" but its OWN definition-of-done FAILED → never completed
    [InlineData(true, SupervisorOutcomes.Completed)]          // it passed → the success label stands
    public async Task The_scorecard_reads_the_objective_acceptance_verdict_off_the_stop_row_not_the_self_report(bool gradePassed, string expectedOutcome)
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);

        // A terminal supervisor run whose stop the model LABELLED "completed", with the server's OBJECTIVE acceptance
        // verdict folded onto the durable stop OUTCOME (the L4 P1 grade). The scorecard must classify by the grade, not
        // the label: gradePassed=false ⇒ acceptance-failed even though the brain claimed completion.
        var runId = await SeedTerminalSupervisorRunWithGradedStopAsync(teamId, userId, label: "completed", acceptancePassed: gradePassed);

        var card = await GetScorecardAsync(userId, teamId);

        var run = card.Runs.Single(r => r.SupervisorRunId == runId);
        run.NotScored.ShouldBeFalse("the run reached a terminal status");
        run.StopCount.ShouldBe(1);
        run.Outcome.ShouldBe(expectedOutcome, "the OBJECTIVE acceptance verdict on the stop's OutcomeJson drives the bucket, not the model's self-reported label");

        card.Rollup.OutcomeDistribution[expectedOutcome].ShouldBe(1);
        if (!gradePassed)
            card.Rollup.OutcomeDistribution.GetValueOrDefault(SupervisorOutcomes.Completed, 0).ShouldBe(0, "a brain that over-claims completion must never inflate the completed bucket");
    }

    [Fact]
    public async Task A_different_team_sees_none_of_another_teams_supervisor_runs()
    {
        var (teamA, userA) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var (teamB, userB) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);

        // Team A runs a real supervisor run to completion.
        var (runId, agent0, agent1) = await DriveSupervisorToSpawnParkAsync(teamA, userA);
        await SimulateAgentCompletionAsync(agent0, AgentRunStatus.Succeeded);
        await SimulateAgentCompletionAsync(agent1, AgentRunStatus.Succeeded);
        await RunEngineAsync(runId);

        // Team A sees its run; team B sees nothing — the tenancy promise.
        (await GetScorecardAsync(userA, teamA)).Runs.ShouldContain(r => r.SupervisorRunId == runId);

        var cardB = await GetScorecardAsync(userB, teamB);
        cardB.Runs.ShouldBeEmpty("team B has no supervisor runs of its own");
        cardB.Runs.ShouldNotContain(r => r.SupervisorRunId == runId, "team A's run must never enter team B's scorecard");
        cardB.Rollup.ScoredRuns.ShouldBe(0);
    }

    [Fact]
    public async Task The_since_filter_windows_the_scorecard_on_the_first_decision()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);

        // A real recent run + the SAME run's ledger rows back-dated to simulate an OLD run, then a since-window
        // that excludes the old one — proving the GROUP-BY-first-decision since path translates against Postgres.
        var (runId, agent0, agent1) = await DriveSupervisorToSpawnParkAsync(teamId, userId);
        await SimulateAgentCompletionAsync(agent0, AgentRunStatus.Succeeded);
        await SimulateAgentCompletionAsync(agent1, AgentRunStatus.Succeeded);
        await RunEngineAsync(runId);

        (await GetScorecardAsync(userId, teamId, since: DateTimeOffset.UtcNow.AddDays(-7)))
            .Runs.ShouldContain(r => r.SupervisorRunId == runId, "the recent run is inside the 7-day window");

        // Back-date this run's whole ledger to a month ago → its FIRST decision is now before the window.
        using (var scope = _fixture.BeginScope())
        {
            await scope.Resolve<CodeSpaceDbContext>().SupervisorDecisionRecord
                .Where(d => d.SupervisorRunId == runId)
                .ExecuteUpdateAsync(s => s.SetProperty(d => d.CreatedDate, DateTimeOffset.UtcNow.AddDays(-30)));
        }

        (await GetScorecardAsync(userId, teamId, since: DateTimeOffset.UtcNow.AddDays(-7)))
            .Runs.ShouldNotContain(r => r.SupervisorRunId == runId, "the back-dated run's first decision is before the window — excluded");
    }

    // ─── Helpers ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Seed a TERMINAL supervisor run with a single model-authored stop whose payload carries the self-reported
    /// <paramref name="label"/> and whose durable OUTCOME carries the SERVER's objective acceptance grade (folded by
    /// the real <see cref="SupervisorOutcome.AppendAcceptanceGrade"/>). The grade-production path is owned by
    /// SupervisorAcceptanceFoldFlowTests; this seeds the persisted shape to prove the scorecard READS it.
    /// </summary>
    private async Task<Guid> SeedTerminalSupervisorRunWithGradedStopAsync(Guid teamId, Guid userId, string label, bool acceptancePassed)
    {
        var workflowId = await CreateWorkflowAsync(teamId, userId);
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);

        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var now = DateTimeOffset.UtcNow;

        var stopOutcome = SupervisorOutcome.AppendAcceptanceGrade($"{{\"stopped\":true,\"outcome\":\"{label}\"}}", acceptancePassed, "model-check");

        db.SupervisorDecisionRecord.Add(new SupervisorDecisionRecord
        {
            Id = Guid.NewGuid(), TeamId = teamId, SupervisorRunId = runId, Sequence = 1,
            DecisionKind = SupervisorDecisionKinds.Stop, IdempotencyKey = $"stop-{Guid.NewGuid():N}", InputHash = "test",
            Status = SupervisorDecisionStatus.Succeeded, PayloadJson = $"{{\"outcome\":\"{label}\",\"summary\":\"done\"}}",
            OutcomeJson = stopOutcome,
            FenceEpoch = 1, CreatedDate = now, CreatedBy = Guid.Empty, LastModifiedDate = now, LastModifiedBy = Guid.Empty,
        });
        await db.SaveChangesAsync();

        // Drive the WorkflowRun to a terminal Success so the run is SCORED (the acceptance verdict, not the run status,
        // is what distinguishes acceptance-failed from completed — the node completes either way).
        await db.WorkflowRun.Where(r => r.Id == runId)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, WorkflowRunStatus.Success).SetProperty(r => r.CompletedAt, now));

        return runId;
    }

    /// <summary>Drive turn 0 (plan) → self-advance → turn 1 (spawn) which stages 2 real agent runs + parks. Returns the supervisor run id + the two spawned agent-run ids.</summary>
    private async Task<(Guid RunId, Guid Agent0, Guid Agent1)> DriveSupervisorToSpawnParkAsync(Guid teamId, Guid userId)
    {
        var workflowId = await CreateWorkflowAsync(teamId, userId);
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);

        var jobClient = ResolveJobClient();
        jobClient.Clear();
        jobClient.AutoExecute = false;

        await RunEngineAsync(runId);
        await ResolveSelfAdvanceAsync(runId);
        await RunEngineAsync(runId);

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();
        var waits = await db.WorkflowRunWait.AsNoTracking()
            .Where(w => w.RunId == runId && w.WaitKind == WorkflowWaitKinds.AgentRun && w.Status == WorkflowWaitStatuses.Pending)
            .OrderBy(w => w.IterationKey).ToListAsync();
        waits.Count.ShouldBe(2, "the spawn staged exactly 2 agent waits");

        return (runId, Guid.Parse(waits[0].Token), Guid.Parse(waits[1].Token));
    }

    private async Task<SupervisorScorecard> GetScorecardAsync(Guid userId, Guid teamId, DateTimeOffset? since = null)
    {
        using var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin);
        return await scope.Resolve<IMediator>().Send(new GetSupervisorScorecardQuery { Since = since });
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

    // Drive the executor's terminal sequence (MarkRunning → Complete → Notify) without the sandboxed CLI —
    // the exact path AgentRunExecutor follows, so the agent terminal status is REAL (mirrors SupervisorSpawnFlowTests).
    private async Task SimulateAgentCompletionAsync(Guid agentRunId, AgentRunStatus status)
    {
        using var scope = _fixture.BeginScope();
        var runs = scope.Resolve<IAgentRunService>();
        var notifier = scope.Resolve<IAgentRunCompletionNotifier>();

        await runs.MarkRunningAsync(agentRunId, CancellationToken.None);
        await runs.CompleteAsync(agentRunId, new AgentRunResult { Status = status, ExitReason = status.ToString(), Summary = $"{status}" }, CancellationToken.None);
        await notifier.NotifyCompletedAsync(agentRunId, CancellationToken.None);
    }

    private async Task<Guid> CreateWorkflowAsync(Guid teamId, Guid userId)
    {
        using var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin);
        return await scope.Resolve<IMediator>().Send(new CreateWorkflowCommand
        {
            Name = "sup-score-" + Guid.NewGuid().ToString("N")[..6],
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

    // manual → sup (agent.supervisor) → terminal
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
