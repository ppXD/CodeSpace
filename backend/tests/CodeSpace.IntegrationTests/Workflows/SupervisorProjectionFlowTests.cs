using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Services.Supervisor;
using CodeSpace.Core.Services.Tasks.Effort;
using CodeSpace.Core.Services.Tasks.Projection;
using CodeSpace.Core.Services.Workflows.Engine;
using CodeSpace.Core.Services.Workflows.Runtime;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Infrastructure.Jobs;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Tasks;
using CodeSpace.Messages.Tasks.Effort;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows;

/// <summary>
/// 🟢 THE PR6 CROWN JEWEL (high fidelity — REAL effort router over the real registries + REAL
/// <see cref="ITaskRunSnapshotFactory"/> + REAL engine + REAL <c>SupervisorTurnService</c> over real Postgres;
/// the scripted decider stands in for the LLM, no real CLI). The DEEP effort tier flows end-to-end through the
/// supervisor lane (always on): route explicit <c>deep</c> through the REAL router → it resolves the supervisor
/// recipe (no degrade) → build the <see cref="TaskBuildContext"/> → the factory projects the
/// <c>trigger.manual → agent.supervisor → builtin.terminal</c> snapshot run → the engine drives the
/// supervisor turn loop (turn 0 plan → self-advance → turn 1 stop) to terminal SUCCESS. The decision
/// ledger has [Plan, Stop] and NO <c>workflow</c> / <c>workflow_version</c> row (a snapshot run).
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class SupervisorProjectionFlowTests : IDisposable
{
    private readonly PostgresFixture _fixture;

    public SupervisorProjectionFlowTests(PostgresFixture fixture)
    {
        _fixture = fixture;

        using var scope = _fixture.BeginScope();
        scope.Resolve<SupervisorDecisionScript>().PlanThenStop();   // the SIMPLEST arc: turn0 plan → turn1 stop, no spawn, no CLI
    }

    public void Dispose()
    {
        using var scope = _fixture.BeginScope();
        scope.Resolve<SupervisorDecisionScript>().PlanThenStop();   // restore the default for sibling tests
    }

    [Fact]
    public async Task Deep_routes_the_supervisor_and_runs_the_lane_to_success_as_a_snapshot_run()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);

        var jobClient = ResolveJobClient();
        jobClient.Clear();
        jobClient.AutoExecute = false;   // the supervisor self-advance is record-only; we resolve + drive turns explicitly

        var workflowCountBefore = await CountWorkflowsAsync(teamId);
        var versionCountBefore = await CountWorkflowVersionsAsync();

        // ── L2: the REAL router turns an explicit deep request into a RoutePlan → supervisor, no degrade. ──
        var request = new EffortRouteRequest
        {
            Seed = new TaskLaunchSeed { Goal = "ship the whole feature", SurfaceKind = "test", TeamId = teamId },
            RequestedEffort = TaskEffortModes.Deep,
        };

        var plan = await RouteAsync(request);

        plan.RecipeKind.ShouldBe(TaskRecipeKinds.Supervisor, "deep routes the supervisor recipe");
        plan.ProjectionKind.ShouldBe(TaskProjectionKinds.Supervisor);
        plan.DegradedReason.ShouldBeNull("no degrade — the lane is available");

        // ── L3: the RoutePlan drives the projection factory → a snapshot supervisor run. ──
        var context = new TaskBuildContext
        {
            Seed = request.Seed,
            Route = plan,
            AgentProfile = new ResolvedAgentProfile { Harness = "codex-cli", RunnerKind = "local", AutonomyLevel = "Standard" },
        };

        var handle = await CreateAndRunAsync(context, teamId, userId);
        handle.ProjectionKind.ShouldBe(TaskProjectionKinds.Supervisor);

        // ── Drive the supervisor turn loop: turn 0 plan (self-advance) → resolve the self-advance → turn 1 stop → Success. ──
        await RunEngineAsync(handle.RunId);
        (await LedgerKinds(handle.RunId, teamId)).ShouldBe(new[] { SupervisorDecisionKinds.Plan }, "turn 0 recorded the plan");

        await ResolveSelfAdvanceAsync(handle.RunId);
        await RunEngineAsync(handle.RunId);

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();

        var run = await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == handle.RunId);
        run.Status.ShouldBe(WorkflowRunStatus.Success,
            customMessage: "the routed deep task must walk start → agent.supervisor → terminal to Success through the real router → projection → engine → supervisor turn loop; if Suspended the turn loop did not finish");

        (await LedgerKinds(handle.RunId, teamId)).ShouldBe(
            new[] { SupervisorDecisionKinds.Plan, SupervisorDecisionKinds.Stop },
            "the supervisor decision ledger has plan then stop — a real turn loop ran, not a stub");

        // It is a SNAPSHOT run — no Workflow / WorkflowVersion row for a routed deep task.
        run.WorkflowId.ShouldBeNull("a routed supervisor task run is a snapshot run — not a child of any workflow");
        run.WorkflowVersion.ShouldBeNull("a routed supervisor task run has no pinned version");
        (await CountWorkflowsAsync(teamId)).ShouldBe(workflowCountBefore, "no workflow row is created for the routed supervisor snapshot run");
        (await CountWorkflowVersionsAsync()).ShouldBe(versionCountBefore, "no workflow_version row is created for the routed supervisor snapshot run");
    }

    // ─── Helpers ────────────────────────────────────────────────────────────────

    private async Task<RoutePlan> RouteAsync(EffortRouteRequest request)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<IEffortRouter>().RouteAsync(request, CancellationToken.None);
    }

    private async Task<TaskRunHandle> CreateAndRunAsync(TaskBuildContext context, Guid teamId, Guid userId)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<ITaskRunSnapshotFactory>().CreateAndRunAsync(context, teamId, userId, session: null, CancellationToken.None);
    }

    private async Task RunEngineAsync(Guid runId)
    {
        using var scope = _fixture.BeginScope();
        await scope.Resolve<IWorkflowEngine>().ExecuteRunAsync(runId, CancellationToken.None);
    }

    // Resolve the run's pending SupervisorDecision self-advance wait via the SAME entry point the engine
    // enqueues post-commit (ResumeWaitAsync) — flips the run Pending so the next RunEngineAsync runs the next turn.
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

    private async Task<IReadOnlyList<string>> LedgerKinds(Guid runId, Guid teamId)
    {
        using var verify = _fixture.BeginScope();
        return await verify.Resolve<CodeSpaceDbContext>().SupervisorDecisionRecord.AsNoTracking()
            .Where(d => d.SupervisorRunId == runId && d.TeamId == teamId).OrderBy(d => d.Sequence).Select(d => d.DecisionKind).ToListAsync();
    }

    private InMemoryBackgroundJobClient ResolveJobClient()
    {
        using var scope = _fixture.BeginScope();
        return scope.Resolve<InMemoryBackgroundJobClient>();
    }

    private async Task<int> CountWorkflowsAsync(Guid teamId)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<CodeSpaceDbContext>().Workflow.AsNoTracking().CountAsync(w => w.TeamId == teamId);
    }

    private async Task<int> CountWorkflowVersionsAsync()
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<CodeSpaceDbContext>().WorkflowVersion.AsNoTracking().CountAsync();
    }
}
