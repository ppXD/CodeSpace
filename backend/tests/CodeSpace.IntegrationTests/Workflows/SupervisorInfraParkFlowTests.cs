using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
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
using MediatR;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows;

/// <summary>
/// 🟢 Integration (real Postgres + real engine + the production retry decorator): the P1.1 model-plane outage park.
/// A brain call that exhausts its bounded in-call retry on a transient fault must PARK the run on a
/// <c>SupervisorInfraPark</c> wait (never terminalize hours of orchestration), the deadline wake must RE-ENTER the
/// SAME turn and recover when the plane is back, a persisting outage must advance the exponential ladder DURABLY
/// across suspend cycles, and only a whole exhausted window ends the run — as an HONEST degraded <c>Stopped</c>
/// through the ledger, never a fake success. The injected faults ride the production
/// <c>RetryingSupervisorDeciderDecorator</c> (a tiny Retry-After keeps each 5-attempt exhaust cycle fast).
/// </summary>
[Collection("Postgres")]
[Trait("Category", "Integration")]
public class SupervisorInfraParkFlowTests
{
    private readonly PostgresFixture _fixture;

    public SupervisorInfraParkFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task A_model_plane_outage_parks_the_run_and_the_deadline_wake_recovers_it()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowId = await CreateWorkflowAsync(teamId, userId, """{"goal":"ship it"}""");
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);

        var jobClient = ResolveJobClient();
        jobClient.Clear();
        jobClient.AutoExecute = false;

        // 5 faults = the whole in-call budget: the decorator exhausts, the fault ESCAPES, the node must park.
        Script(s => { s.PlanThenStop(); s.TransientFaultRetryAfter = TimeSpan.FromMilliseconds(1); s.FailTransientlyOnTurn(0, 5); });

        try
        {
            await RunEngineAsync(runId);   // turn 0 decide → exhausted transient → PARK, not Failed

            Guid waitId;
            string markerJson;
            using (var mid = _fixture.BeginScope())
            {
                var db = mid.Resolve<CodeSpaceDbContext>();

                var nodeRows = await db.WorkflowRunNode.AsNoTracking().Where(n => n.RunId == runId).ToListAsync();
                var waitRows = await db.WorkflowRunWait.AsNoTracking().Where(w => w.RunId == runId).ToListAsync();
                var runRow = await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId);
                var diag = $"runError={runRow.Error} nodes=[" + string.Join(" | ", nodeRows.Select(n => $"{n.NodeId}:{n.Status}:{n.Error}")) + "] waits=[" + string.Join(" | ", waitRows.Select(w => $"{w.WaitKind}:{w.Status}:{w.IterationKey}")) + "]";

                runRow.Status.ShouldBe(WorkflowRunStatus.Suspended, $"an exhausted transient parks the run — it must NEVER terminalize it ({diag})");

                var wait = await db.WorkflowRunWait.AsNoTracking().SingleAsync(w => w.RunId == runId && w.Status == WorkflowWaitStatuses.Pending);
                wait.WaitKind.ShouldBe(WorkflowWaitKinds.SupervisorInfraPark);
                wait.WakeAt.ShouldNotBeNull("the deadline IS the wake — the run-detail shows when it retries");
                (wait.WakeAt!.Value - DateTimeOffset.UtcNow).ShouldBeLessThanOrEqualTo(TimeSpan.FromSeconds(73), "park 1 rides the 1m rung (+20% jitter)");

                var marker = JsonDocument.Parse(wait.PayloadJson!).RootElement;
                marker.GetProperty("parks").GetInt32().ShouldBe(1);

                waitId = wait.Id;
                markerJson = wait.PayloadJson!;
            }

            // The plane recovers; the deadline fires (what the scheduled Hangfire job does at wake_at).
            await FireDeadlineAsync(runId, waitId, markerJson);
            await RunEngineAsync(runId);           // re-enters the SAME turn → plan succeeds now
            await ResolveSelfAdvanceAsync(runId);
            await RunEngineAsync(runId);           // turn 1 → stop

            using var verify = _fixture.BeginScope();
            var vdb = verify.Resolve<CodeSpaceDbContext>();

            (await vdb.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId)).Status
                .ShouldBe(WorkflowRunStatus.Success, "the outage was ridden out — the run completed as if the blip never happened");

            var decisions = await vdb.SupervisorDecisionRecord.AsNoTracking().Where(d => d.SupervisorRunId == runId).ToListAsync();
            decisions.Select(d => d.DecisionKind).OrderBy(k => k).ShouldBe(new[] { SupervisorDecisionKinds.Plan, SupervisorDecisionKinds.Stop });
            decisions.ShouldAllBe(d => d.Status == SupervisorDecisionStatus.Succeeded, "no decision row was failed or stranded by the outage");
        }
        finally
        {
            using (var s = _fixture.BeginScope()) s.Resolve<SupervisorDecisionScript>().ClearTransientFaults();
            jobClient.AutoExecute = true;
        }
    }

    [Fact]
    public async Task A_persisting_outage_advances_the_exponential_ladder_durably()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowId = await CreateWorkflowAsync(teamId, userId, """{"goal":"ship it"}""");
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);

        var jobClient = ResolveJobClient();
        jobClient.Clear();
        jobClient.AutoExecute = false;

        // 10 faults = TWO whole exhaust cycles: park 1, wake, park 2 — the ladder must advance across the suspend cycle.
        Script(s => { s.PlanThenStop(); s.TransientFaultRetryAfter = TimeSpan.FromMilliseconds(1); s.FailTransientlyOnTurn(0, 10); });

        try
        {
            await RunEngineAsync(runId);   // cycle 1 → park 1

            (Guid WaitId, string Marker) first;
            using (var mid = _fixture.BeginScope())
            {
                var wait = await mid.Resolve<CodeSpaceDbContext>().WorkflowRunWait.AsNoTracking()
                    .SingleAsync(w => w.RunId == runId && w.Status == WorkflowWaitStatuses.Pending);
                first = (wait.Id, wait.PayloadJson!);
            }

            await FireDeadlineAsync(runId, first.WaitId, first.Marker);
            await RunEngineAsync(runId);   // cycle 2 (5 more faults) → park 2

            using var verify = _fixture.BeginScope();
            var db = verify.Resolve<CodeSpaceDbContext>();

            var second = await db.WorkflowRunWait.AsNoTracking().SingleAsync(w => w.RunId == runId && w.Status == WorkflowWaitStatuses.Pending);
            second.Id.ShouldNotBe(first.WaitId, "each park is its own wait row (distinct iteration key)");
            second.WaitKind.ShouldBe(WorkflowWaitKinds.SupervisorInfraPark);
            second.IterationKey.ShouldEndWith("#infra2");

            var marker = JsonDocument.Parse(second.PayloadJson!).RootElement;
            marker.GetProperty("parks").GetInt32().ShouldBe(2, "the ladder position survived the suspend cycle — durable, not in-memory");

            var delay = second.WakeAt!.Value - DateTimeOffset.UtcNow;
            delay.ShouldBeGreaterThanOrEqualTo(TimeSpan.FromSeconds(300 * 0.8 - 5), "park 2 rides the 5m rung (−20% jitter, small clock slack)");
            delay.ShouldBeLessThanOrEqualTo(TimeSpan.FromSeconds(300 * 1.2), "…and never more than the rung +20%");

            (await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId)).Status
                .ShouldBe(WorkflowRunStatus.Suspended, "still riding the outage — never Failed");
        }
        finally
        {
            using (var s = _fixture.BeginScope()) s.Resolve<SupervisorDecisionScript>().ClearTransientFaults();
            jobClient.AutoExecute = true;
        }
    }

    [Fact]
    public async Task An_exhausted_park_window_stops_the_run_honestly_through_the_ledger()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowId = await CreateWorkflowAsync(teamId, userId, """{"goal":"ship it"}""");
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);

        var jobClient = ResolveJobClient();
        jobClient.Clear();
        jobClient.AutoExecute = false;

        Script(s => { s.PlanThenStop(); s.TransientFaultRetryAfter = TimeSpan.FromMilliseconds(1); s.FailTransientlyOnTurn(0, 10); });

        try
        {
            await RunEngineAsync(runId);   // park 1

            Guid waitId;
            using (var mid = _fixture.BeginScope())
                waitId = (await mid.Resolve<CodeSpaceDbContext>().WorkflowRunWait.AsNoTracking()
                    .SingleAsync(w => w.RunId == runId && w.Status == WorkflowWaitStatuses.Pending)).Id;

            // The wake arrives with a window that ALREADY elapsed (the marker's anchor is 25h old) and the plane is
            // STILL down (5 faults remain) — the node must stop honestly instead of parking forever.
            var doctored = JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                [SupervisorInfraPark.MarkerField] = true,
                ["parks"] = 4,
                ["firstParkedAtUtc"] = (DateTimeOffset.UtcNow - TimeSpan.FromHours(25)).ToString("o"),
                ["error"] = "gateway 503",
            });

            await FireDeadlineAsync(runId, waitId, doctored);
            await RunEngineAsync(runId);   // cycle 2 exhausts → window gone → forced HONEST stop

            using var verify = _fixture.BeginScope();
            var db = verify.Resolve<CodeSpaceDbContext>();

            var run = await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId);
            run.Status.ShouldBe(WorkflowRunStatus.Success, "the node finished the run cleanly (the walk completes) — the HONESTY lives in the status output + the ledger stop");

            var outputs = JsonDocument.Parse(run.OutputsJson).RootElement;
            outputs.GetProperty("status").GetString().ShouldBe("Stopped", "a run abandoned to an outage must NEVER read as Completed");
            outputs.GetProperty("reason").GetString().ShouldBe(SupervisorStopReasons.ModelPlaneUnavailable);

            var stop = await db.SupervisorDecisionRecord.AsNoTracking().SingleAsync(d => d.SupervisorRunId == runId && d.DecisionKind == SupervisorDecisionKinds.Stop);
            stop.Status.ShouldBe(SupervisorDecisionStatus.Succeeded, "the forced stop went through the SAME exactly-once ledger path a bound trip takes");
            JsonDocument.Parse(stop.PayloadJson).RootElement.GetProperty("reason").GetString().ShouldBe(SupervisorStopReasons.ModelPlaneUnavailable);

            (await db.WorkflowRunWait.AsNoTracking().CountAsync(w => w.RunId == runId && w.Status == WorkflowWaitStatuses.Pending))
                .ShouldBe(0, "nothing is left parked behind the honest stop");
        }
        finally
        {
            using (var s = _fixture.BeginScope()) s.Resolve<SupervisorDecisionScript>().ClearTransientFaults();
            jobClient.AutoExecute = true;
        }
    }

    // ── Helpers (the SupervisorBoundsFlowTests pattern) ─────────────────────────────

    private void Script(Action<SupervisorDecisionScript> configure)
    {
        using var scope = _fixture.BeginScope();
        configure(scope.Resolve<SupervisorDecisionScript>());
    }

    private async Task FireDeadlineAsync(Guid runId, Guid waitId, string timeoutPayloadJson)
    {
        using var scope = _fixture.BeginScope();
        (await scope.Resolve<IWorkflowResumeService>().ResumeByDeadlineAsync(waitId, timeoutPayloadJson, CancellationToken.None))
            .ShouldBeTrue($"the deadline must resolve pending wait {waitId} on run {runId} — check workflow_run_wait manually if this fails");
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

    private async Task<Guid> CreateWorkflowAsync(Guid teamId, Guid userId, string supervisorConfigJson)
    {
        using var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin);
        return await scope.Resolve<IMediator>().Send(new CreateWorkflowCommand
        {
            Name = "sup-infra-park-" + Guid.NewGuid().ToString("N")[..6],
            Description = null,
            Definition = SupervisorDefinition(supervisorConfigJson),
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

    // start → sup → end; the terminal forwards status + reason so the honest Stopped is asserted end-to-end.
    private static WorkflowDefinition SupervisorDefinition(string supervisorConfigJson) => new()
    {
        SchemaVersion = 1,
        Nodes = new List<NodeDefinition>
        {
            new() { Id = "start", TypeKey = "trigger.manual", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "sup", TypeKey = "agent.supervisor", Config = WorkflowsTestSeed.Json(supervisorConfigJson), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "end", TypeKey = "builtin.terminal", Config = WorkflowsTestSeed.EmptyJson(),
                    Inputs = WorkflowsTestSeed.Json("""{"status":"{{nodes.sup.outputs.status}}","reason":"{{nodes.sup.outputs.reason}}"}""") },
        },
        Edges = new List<EdgeDefinition>
        {
            new() { From = "start", To = "sup" },
            new() { From = "sup", To = "end" },
        },
    };
}
