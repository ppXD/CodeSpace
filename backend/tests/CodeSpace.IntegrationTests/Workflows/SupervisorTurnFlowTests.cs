using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Services.Supervisor;
using CodeSpace.Core.Services.Workflows.Engine;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Infrastructure.Jobs;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Commands.Workflows;
using CodeSpace.Messages.Dtos.Workflows;
using CodeSpace.Messages.Enums;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows;

/// <summary>
/// 🟢 Integration (high fidelity — REAL engine + REAL <see cref="SupervisorDecisionLog"/> over real Postgres,
/// the supervisor lane flag flipped ON). The PR-E E2 SMALLEST VERTICAL of the bounded durable turn loop:
/// <list type="bullet">
///   <item>a flag-on <c>agent.supervisor</c> run executes turn 1 (plan → a Succeeded ledger row), PARKS on a
///         per-turn <c>SupervisorDecision</c> wait, the self-advance resumes it, turn 2 (stop) → the run
///         reaches Success; the ledger has EXACTLY 2 rows in Sequence order;</item>
///   <item>RESTART-REPLAY: drive turn 1, park, then SIMULATE A RESTART by re-dispatching the Suspended run
///         WITHOUT re-running turn 1's side effect — assert the ledger STILL has exactly 1 plan row (no
///         double-plan), then resume → turn 2 → Success.</item>
/// </list>
/// The in-memory job client records the post-commit <c>ResumeWaitAsync</c> self-advance enqueue; draining it
/// via <see cref="InMemoryBackgroundJobClient.WaitForPendingAsync"/> runs the resume in a fresh scope — a
/// faithful Hangfire-worker model — which resolves the wait + re-dispatches the run for the next turn.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class SupervisorTurnFlowTests : IDisposable
{
    private readonly PostgresFixture _fixture;
    private readonly string? _flagBefore;

    public SupervisorTurnFlowTests(PostgresFixture fixture)
    {
        _fixture = fixture;
        _flagBefore = Environment.GetEnvironmentVariable(SupervisorLane.EnabledEnvVar);
        Environment.SetEnvironmentVariable(SupervisorLane.EnabledEnvVar, "1");

        // The scripted decider drives the REAL turn service with no LLM. This E2 flow pins the plan→stop arc;
        // reset the shared fixture-singleton script (the E3 crown-jewel flips it to plan→spawn→stop).
        using var scope = _fixture.BeginScope();
        scope.Resolve<CodeSpace.IntegrationTests.Workflows.Infrastructure.SupervisorDecisionScript>().PlanThenStop();
    }

    public void Dispose() => Environment.SetEnvironmentVariable(SupervisorLane.EnabledEnvVar, _flagBefore);

    [Fact]
    public async Task Supervisor_runs_plan_then_self_advances_to_stop_and_completes()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowId = await CreateWorkflowAsync(teamId, userId);
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);

        var jobClient = ResolveJobClient();

        // ── Turn 1: the supervisor plans (a Succeeded ledger row) + parks on a per-turn wait. ──
        await RunEngineAsync(runId);

        using (var verify = _fixture.BeginScope())
        {
            var db = verify.Resolve<CodeSpaceDbContext>();

            (await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId)).Status
                .ShouldBe(WorkflowRunStatus.Suspended, "the supervisor parks between turns");

            var wait = await db.WorkflowRunWait.AsNoTracking().SingleAsync(w => w.RunId == runId);
            wait.WaitKind.ShouldBe(WorkflowWaitKinds.SupervisorDecision);
            wait.IterationKey.ShouldBe("sup#turn1", "turn 1 parks under the per-turn IterationKey for the NEXT turn");

            var rows = await Ledger(db, runId, teamId);
            rows.Count.ShouldBe(1, "turn 1 recorded exactly one decision");
            rows[0].DecisionKind.ShouldBe(SupervisorDecisionKinds.Plan);
            rows[0].Status.ShouldBe(SupervisorDecisionStatus.Succeeded);
        }

        // ── Self-advance: drain the post-commit ResumeWaitAsync enqueue (resolves the wait + re-dispatches). ──
        await jobClient.WaitForPendingAsync();

        // The re-dispatch ran turn 2 (stop) inline via the resume; the run is now terminal.
        using (var verify = _fixture.BeginScope())
        {
            var db = verify.Resolve<CodeSpaceDbContext>();

            (await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId)).Status
                .ShouldBe(WorkflowRunStatus.Success, "turn 2's stop completes the run via the normal walk");

            var rows = await Ledger(db, runId, teamId);
            rows.Count.ShouldBe(2, "exactly two decisions — plan then stop");
            rows.Select(r => r.DecisionKind).ShouldBe(new[] { SupervisorDecisionKinds.Plan, SupervisorDecisionKinds.Stop }, "in Sequence order");
            rows.All(r => r.Status == SupervisorDecisionStatus.Succeeded).ShouldBeTrue("both decisions succeeded");
        }
    }

    [Fact]
    public async Task A_restart_mid_park_re_dispatches_without_double_planning()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowId = await CreateWorkflowAsync(teamId, userId);
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);

        var jobClient = ResolveJobClient();

        // ── Turn 1: plan + park. ──
        await RunEngineAsync(runId);

        using (var verify = _fixture.BeginScope())
        {
            (await Ledger(verify.Resolve<CodeSpaceDbContext>(), runId, teamId)).Count.ShouldBe(1, "one plan row after turn 1");
        }

        // ── SIMULATE A RESTART: the post-commit self-advance enqueue is LOST (we DROP it, never draining the
        //    job client). The run is Suspended with a pending SupervisorDecision wait and NOTHING coming. The
        //    durable recovery path — the SAME ResumeWaitAsync the engine self-advance enqueues and the
        //    reconciler's RecoverSupervisorAdvancesAsync re-fires — re-enters the node. The rehydrate replays
        //    the terminal plan row (does NOT re-run its side effect), so there must be NO second plan row. We
        //    invoke ResumeWaitAsync directly: it's the exact entry point both the engine and the reconciler use,
        //    so this proves the re-entry replays regardless of WHICH actor re-fires it (the reconciler's own
        //    2-min threshold excludes a just-parked run, which is why we drive the shared core directly). ──
        jobClient.Clear();   // drop the lost enqueue — the recovery, not the original, must drive the run forward.

        Guid waitId;
        using (var verify = _fixture.BeginScope())
        {
            var db = verify.Resolve<CodeSpaceDbContext>();
            waitId = (await db.WorkflowRunWait.AsNoTracking().SingleAsync(w => w.RunId == runId && w.Status == WorkflowWaitStatuses.Pending)).Id;
        }

        using (var scope = _fixture.BeginScope())
        {
            await scope.Resolve<IWorkflowResumeService>().ResumeWaitAsync(runId, waitId, null, CancellationToken.None);
        }

        // Drain whatever the resume itself enqueued (the re-dispatch + turn 2's own self-advance, if any).
        await jobClient.WaitForPendingAsync();

        using (var verify = _fixture.BeginScope())
        {
            var db = verify.Resolve<CodeSpaceDbContext>();

            var rows = await Ledger(db, runId, teamId);
            rows.Count(r => r.DecisionKind == SupervisorDecisionKinds.Plan).ShouldBe(1, "NO double-plan — the restart replayed the settled plan, never re-ran it");
            rows.Count.ShouldBe(2, "plan (replayed) + stop (the recovered turn) — exactly two rows");

            (await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId)).Status
                .ShouldBe(WorkflowRunStatus.Success, "the recovered run reached turn 2's stop and completed");
        }
    }

    private static async Task<IReadOnlyList<Core.Persistence.Entities.SupervisorDecisionRecord>> Ledger(CodeSpaceDbContext db, Guid runId, Guid teamId) =>
        await db.SupervisorDecisionRecord.AsNoTracking().Where(d => d.SupervisorRunId == runId && d.TeamId == teamId).OrderBy(d => d.Sequence).ToListAsync();

    private async Task<Guid> CreateWorkflowAsync(Guid teamId, Guid userId)
    {
        using var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin);
        return await scope.Resolve<IMediator>().Send(new CreateWorkflowCommand
        {
            Name = "sup-" + Guid.NewGuid().ToString("N")[..6],
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
