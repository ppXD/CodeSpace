using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Workflows.Engine;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Infrastructure.Jobs;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Commands.Workflows;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Dtos.Workflows;
using CodeSpace.Messages.Enums;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows.Supervisor;

/// <summary>
/// 🟢 Integration (real Postgres + real engine + the production deadline wake): <see cref="InfraParkRide"/>'s DB-BACKED
/// half — the part <see cref="InfraParkRideTests"/> cannot reach, and the part that actually decides on the live path.
///
/// <para>Two properties, and the ride is only sound if BOTH hold. (1) OWNERSHIP: the ride owns <c>SupervisorInfraPark</c>
/// waits and NOTHING else, so a genuine park-short — a run stuck on an approval card, an agent run, a supervisor
/// self-advance, a timer — is a single read and a no-op, and the caller's own assertions still red it exactly as before.
/// That predicate lives in the ride's SQL <c>WHERE</c>, which a classification-only test cannot observe: the read filters
/// by wait kind BEFORE <c>Classify</c> ever sees a cell, so at the unit tier a non-park kind is an input production
/// cannot produce. Here the wait row is real and the filter is the thing under test. (2) THE WAKE WORKS: a REAL park
/// (the supervisor's own exhausted-transient park) is cleared by the ride firing the production
/// <see cref="IWorkflowResumeService.ResumeByDeadlineAsync"/> with the wait's stored ladder marker and draining the
/// re-dispatch, so the node RE-ENTERS and the run moves on — which is the whole product claim the gates then judge.</para>
///
/// <para>Drives the ride at a ZERO pause (the internal budget overload) so both tests pin the real SQL + the real wake
/// without sleeping through <see cref="InfraParkRide.WakePause"/>; the pause itself is pinned at the unit tier.</para>
/// </summary>
[Collection("Postgres")]
[Trait("Category", "Integration")]
public sealed class InfraParkRideFlowTests
{
    private readonly PostgresFixture _fixture;

    public InfraParkRideFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    [Theory]
    [InlineData(WorkflowWaitKinds.Approval)]           // an approval card — a human is the only thing that resolves it
    [InlineData(WorkflowWaitKinds.AgentRun)]           // an agent barrier — the executor's completion resolves it
    [InlineData(WorkflowWaitKinds.SupervisorDecision)] // a supervisor self-advance
    [InlineData(WorkflowWaitKinds.Timer)]              // a plain delay
    public async Task A_park_short_on_a_wait_the_ride_does_not_own_is_one_read_and_a_no_op(string waitKind)
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowId = await CreateWorkflowAsync(teamId, userId);
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);

        var waitId = await SeedPendingWaitAsync(runId, waitKind);

        var ride = await InfraParkRide.RideAsync(_fixture, runId, InfraParkRide.MaxWakes, TimeSpan.Zero);

        ride.Wakes.ShouldBe(0,
            $"a run parked on a {waitKind} wait is NOT the ride's business — it fired {ride.Wakes} wake(s) at it. A ride that owns every wait kind would resolve a human's approval card behind their back, and would turn every genuine park-short into an infra skip instead of the red it must stay.");
        ride.Rode.ShouldBeFalse("nothing was ridden, so the caller judges the run exactly as it stands");

        using var verify = _fixture.BeginScope();
        (await verify.Resolve<CodeSpaceDbContext>().WorkflowRunWait.AsNoTracking().SingleAsync(w => w.Id == waitId)).Status
            .ShouldBe(WorkflowWaitStatuses.Pending, $"the {waitKind} wait must still be Pending — the ride may not resolve a wait it does not own; check workflow_run_wait for run {runId} manually if this fails");
    }

    [Fact]
    public async Task A_real_model_plane_park_is_ridden_to_settlement_by_the_production_deadline_wake()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowId = await CreateWorkflowAsync(teamId, userId);
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);

        var jobClient = ResolveJobClient();
        jobClient.Clear();
        jobClient.AutoExecute = true;   // the ride's documented precondition — the resume re-dispatches through the deferred queue it drains

        // 5 faults = the whole in-call retry budget: the decorator exhausts, the fault ESCAPES, and the node PARKS on
        // the ladder instead of failing the run. Fault 6 never comes, so the wake's re-entry decides successfully.
        Script(s => { s.PlanThenStop(); s.TransientFaultRetryAfter = TimeSpan.FromMilliseconds(1); s.FailTransientlyOnTurn(0, 5); });

        try
        {
            await RunEngineAsync(runId);
            await jobClient.WaitForPendingAsync();

            var park = await SinglePendingWaitAsync(runId);
            park.WaitKind.ShouldBe(WorkflowWaitKinds.SupervisorInfraPark, "the fixture must actually be parked before the ride is measured — otherwise this test proves nothing");

            var ride = await InfraParkRide.RideAsync(_fixture, runId, InfraParkRide.MaxWakes, TimeSpan.Zero);

            ride.Wakes.ShouldBe(1, "ONE production deadline wake clears a park whose plane came back — a second wake would mean the first never re-entered the node");
            ride.Rode.ShouldBeTrue();

            using var verify = _fixture.BeginScope();
            var db = verify.Resolve<CodeSpaceDbContext>();

            (await db.WorkflowRunWait.AsNoTracking().SingleAsync(w => w.Id == park.Id)).Status
                .ShouldBe(WorkflowWaitStatuses.Resolved, $"the ride fires the park's OWN deadline, so the wait must be Resolved — check workflow_run_wait for run {runId} manually if this fails");

            // The turn the outage interrupted RAN AGAIN and got its plan, and the run carried on to its terminal. Assert
            // what the drain ACTUALLY leaves: with AutoExecute the self-advance the re-entry raises is itself dispatched
            // and resolved in the same drain, so a PENDING self-advance is exactly what this cascade never leaves behind.
            // Both assertions still red if WakeAsync's drain is removed — without it the resume resolves the park and the
            // engine is never re-dispatched, so no self-advance is ever raised and the run never reaches a terminal.
            (await db.WorkflowRunWait.AsNoTracking().CountAsync(w => w.RunId == runId && w.WaitKind == WorkflowWaitKinds.SupervisorDecision))
                .ShouldBe(1, $"the node must have RE-ENTERED and raised its self-advance — check workflow_run_wait for run {runId} manually if this fails");

            (await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId)).Status
                .ShouldBe(WorkflowRunStatus.Success, $"the ride's drain must carry the re-entered run to its terminal — check workflow_run for run {runId} manually if this fails");
        }
        finally
        {
            using (var s = _fixture.BeginScope()) s.Resolve<SupervisorDecisionScript>().ClearTransientFaults();
        }
    }

    [Fact]
    public async Task A_wait_this_ride_does_not_own_is_read_as_settled_and_never_woken()
    {
        // The predicate that actually decides on the live path is the SQL one — the classification unit tests cannot
        // observe it, because the read filters on SupervisorInfraPark before Classify is ever handed a wait. So drive a
        // REAL parked run, then retype its wait to a kind the ride does not own. A genuine park-short waits on exactly
        // such a kind, and swallowing it would turn a model giving up into a silent infra excuse.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowId = await CreateWorkflowAsync(teamId, userId);
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);

        var jobClient = ResolveJobClient();
        jobClient.Clear();
        jobClient.AutoExecute = true;

        Script(s => { s.PlanThenStop(); s.TransientFaultRetryAfter = TimeSpan.FromMilliseconds(1); s.FailTransientlyOnTurn(0, 5); });

        try
        {
            await RunEngineAsync(runId);
            await jobClient.WaitForPendingAsync();

            var park = await SinglePendingWaitAsync(runId);
            park.WaitKind.ShouldBe(WorkflowWaitKinds.SupervisorInfraPark);

            using (var retype = _fixture.BeginScope())
                await retype.Resolve<CodeSpaceDbContext>().WorkflowRunWait.Where(w => w.Id == park.Id)
                    .ExecuteUpdateAsync(setters => setters.SetProperty(w => w.WaitKind, WorkflowWaitKinds.Approval));

            var ride = await InfraParkRide.RideAsync(_fixture, runId, InfraParkRide.MaxWakes, TimeSpan.Zero);

            ride.Rode.ShouldBeFalse("a wait this ride does not own is settled as far as the ride is concerned — riding it would swallow a genuine park-short");
            ride.Wakes.ShouldBe(0, "no deadline may be fired for a wait the ride does not own");

            using var verify = _fixture.BeginScope();

            (await verify.Resolve<CodeSpaceDbContext>().WorkflowRunWait.AsNoTracking().SingleAsync(w => w.Id == park.Id)).Status
                .ShouldBe(WorkflowWaitStatuses.Pending, "the unowned wait must be left exactly as it was found");
        }
        finally
        {
            using (var s = _fixture.BeginScope()) s.Resolve<SupervisorDecisionScript>().ClearTransientFaults();
        }
    }

    // ── Helpers (the SupervisorInfraParkFlowTests pattern) ──────────────────────────────────────────────────────────

    private void Script(Action<SupervisorDecisionScript> configure)
    {
        using var scope = _fixture.BeginScope();
        configure(scope.Resolve<SupervisorDecisionScript>());
    }

    /// <summary>Seed ONE pending wait of an arbitrary kind on <paramref name="runId"/> — the shape the ride must leave completely alone.</summary>
    private async Task<Guid> SeedPendingWaitAsync(Guid runId, string waitKind)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var waitId = Guid.NewGuid();
        db.WorkflowRunWait.Add(new WorkflowRunWait
        {
            Id = waitId, RunId = runId, NodeId = "sup", IterationKey = string.Empty, WaitKind = waitKind,
            Token = Guid.NewGuid().ToString("N"), Status = WorkflowWaitStatuses.Pending,
            PayloadJson = null, CreatedAt = DateTimeOffset.UtcNow,
        });

        await db.SaveChangesAsync();
        return waitId;
    }

    private async Task<WorkflowRunWait> SinglePendingWaitAsync(Guid runId)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<CodeSpaceDbContext>().WorkflowRunWait.AsNoTracking()
            .SingleAsync(w => w.RunId == runId && w.Status == WorkflowWaitStatuses.Pending);
    }

    private async Task<Guid> CreateWorkflowAsync(Guid teamId, Guid userId)
    {
        using var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin);
        return await scope.Resolve<IMediator>().Send(new CreateWorkflowCommand
        {
            Name = "infra-park-ride-" + Guid.NewGuid().ToString("N")[..6],
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

    // start → sup → end — the same minimal supervisor graph SupervisorInfraParkFlowTests parks.
    private static WorkflowDefinition SupervisorDefinition() => new()
    {
        SchemaVersion = 1,
        Nodes = new List<NodeDefinition>
        {
            new() { Id = "start", TypeKey = "trigger.manual", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "sup", TypeKey = "agent.supervisor", Config = WorkflowsTestSeed.Json("""{"goal":"ship it"}"""), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "end", TypeKey = "builtin.terminal", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
        },
        Edges = new List<EdgeDefinition>
        {
            new() { From = "start", To = "sup" },
            new() { From = "sup", To = "end" },
        },
    };
}
