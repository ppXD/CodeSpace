using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Workflows.Budget;
using CodeSpace.Core.Services.Workflows.Engine;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Infrastructure.Jobs;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Commands.Agents;
using CodeSpace.Messages.Commands.Storage;
using CodeSpace.Messages.Commands.Webhooks;
using CodeSpace.Messages.Commands.Workflows;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Mediation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows;

/// <summary>
/// Dispatches the system sweeps through the REAL mediator — the pipeline the recurring jobs use — rather than
/// resolving their service directly. Every other reconciler test calls <c>IAgentRunReconcilerService.ReconcileAsync</c>
/// straight off a scope, which is the one topology production never runs: in production a recurring job sends an
/// <c>ICommand</c>, and <c>TransactionalBehavior</c> wraps the whole sweep in ONE transaction on the scoped
/// <c>CodeSpaceDbContext</c>. That difference hid a live defect for weeks — the sweep's wait-recovery step refuses to
/// run under an ambient transaction, so every minutely tick threw at its last step and the behavior rolled back the
/// ENTIRE sweep: abandons, orphan cancels, cleanup receipts, re-dispatches. Nothing caught it because no test sent
/// the command.
///
/// <para>So these tests assert the two things the direct-service tests structurally cannot: the command completes
/// through the pipeline at all, and the effect on a row THIS test owns is still there when read back in a fresh
/// scope (i.e. it was committed, not rolled back). Never the sweep's global tally — this suite shares one database.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class SweepCommandTransactionFlowTests
{
    private readonly PostgresFixture _fixture;

    public SweepCommandTransactionFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task Reconcile_stuck_agent_runs_commits_the_abandon_it_performed()
    {
        var teamId = await SeedTeamAsync();
        var runId = await SeedStaleRunningRunAsync(teamId);

        using (var scope = _fixture.BeginScope())
            await scope.Resolve<IMediator>().Send(new ReconcileStuckAgentRunsCommand());

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();
        var run = await db.AgentRun.AsNoTracking().SingleAsync(r => r.Id == runId);

        run.Status.ShouldBe(AgentRunStatus.Failed,
            customMessage: "the sweep abandoned this run, so a fresh scope must still see Failed — a Running row here means the " +
                           "tick threw at a later step and TransactionalBehavior rolled the whole sweep back (check Seq for " +
                           "'Command ReconcileStuckAgentRunsCommand failed; transaction rolled back')");

        (await db.AgentRunEvent.AsNoTracking().AnyAsync(e => e.AgentRunId == runId && e.Kind == AgentEventKind.Error))
            .ShouldBeTrue("the abandonment's timeline entry is committed too, not only the status flip");
    }

    [Fact]
    public async Task Reconcile_stuck_agent_runs_re_dispatches_the_stuck_queued_run_it_owns()
    {
        var teamId = await SeedTeamAsync();
        var parentRunId = await SeedWorkflowRunAsync(teamId, WorkflowRunStatus.Running);
        var runId = await SeedStuckQueuedRunAsync(teamId, parentRunId);

        var jobClient = _fixture.BeginScope().Resolve<InMemoryBackgroundJobClient>();
        jobClient.AutoExecute = false;   // record the dispatch; the binary-less executor must not actually run

        try
        {
            using var scope = _fixture.BeginScope();
            await scope.Resolve<IMediator>().Send(new ReconcileStuckAgentRunsCommand());
        }
        finally
        {
            jobClient.AutoExecute = true;
        }

        jobClient.Calls.Any(c => c.ServiceType == typeof(IAgentRunExecutor) && c.RunId == runId).ShouldBeTrue(
            customMessage: "the sweep's last step re-dispatches a stuck-Queued run whose dispatch was lost; no recorded enqueue " +
                           "for this run means that step never ran at all");
    }

    [Fact]
    public async Task Budget_settlement_releases_the_terminal_map_branch_reservation_it_owns()
    {
        var teamId = await SeedTeamAsync();
        var parentRunId = await SeedWorkflowRunAsync(teamId, WorkflowRunStatus.Success);
        var reservationId = await SeedMapBranchReservationAsync(teamId, parentRunId);

        using (var scope = _fixture.BeginScope())
            await scope.Resolve<IMediator>().Send(new SweepBudgetSettlementCommand());

        using var verify = _fixture.BeginScope();
        var reservation = await verify.Resolve<CodeSpaceDbContext>().BudgetReservation.AsNoTracking().SingleAsync(r => r.Id == reservationId);

        reservation.State.ShouldBe(BudgetReservationStates.Released,
            customMessage: "a terminal run's branch reservation holds team headroom for work that already finished, so the sweep " +
                           "must release it — still Reserved here means IBudgetLedger.ReleaseAsync could not open its own " +
                           "transaction and took the whole tick down with it");
    }

    /// <summary>
    /// Every sweep marked <see cref="INonTransactionalCommand"/>, dispatched through the real pipeline. The cases
    /// differ only by which command is sent, so they are one Theory rather than nineteen copies of one fact.
    ///
    /// <para>Completing is the whole assertion, and it is not a weak one: three of these commands do NOT complete
    /// without the marker. Two of them open their own transaction on the scoped DbContext, which EF refuses while
    /// the behavior's is already open, and one refuses outright to run its wait recovery under an ambient
    /// transaction. Nothing else in the suite sends any of them, which is why all three ticked and threw in
    /// production unnoticed. A per-effect assertion belongs with each sweep's own tests, where the candidate rows
    /// can be seeded; this one measures only that the tick survives its own pipeline.</para>
    /// </summary>
    [Theory]
    [InlineData(typeof(BackfillRunScorecardsCommand))]
    [InlineData(typeof(DistillLessonsCommand))]
    [InlineData(typeof(MaterializeWorkflowRunModelCallBodiesCommand))]
    [InlineData(typeof(ProbeStaleModelAvailabilityCommand))]
    [InlineData(typeof(ProbeStaleStorageDestinationsCommand))]
    [InlineData(typeof(ProbeUnknownModelCapabilitiesCommand))]
    [InlineData(typeof(ReapAgentRunOrphansCommand))]
    [InlineData(typeof(ReapAgentRunSpoolsCommand))]
    [InlineData(typeof(ReapUnreferencedArtifactsCommand))]
    [InlineData(typeof(ReconcileAgentRunLogCapturesCommand))]
    [InlineData(typeof(ReconcileRunDataManifestsCommand))]
    [InlineData(typeof(ReconcileStuckAgentRunsCommand))]
    [InlineData(typeof(ReconcileStuckRunsCommand))]
    [InlineData(typeof(ReconcileStuckWebhookRegistrationsCommand))]
    [InlineData(typeof(ResumeAbandonedArtifactTransfersCommand))]
    [InlineData(typeof(SweepBudgetSettlementCommand))]
    [InlineData(typeof(SweepCompletionShadowCommand))]
    [InlineData(typeof(TierStaleModelCapabilitiesCommand))]
    [InlineData(typeof(VerifyStaleArtifactLocationsCommand))]
    public async Task A_system_sweep_completes_a_tick_through_the_real_pipeline(Type commandType)
    {
        typeof(INonTransactionalCommand).IsAssignableFrom(commandType).ShouldBeTrue($"{commandType.Name} is listed here as a system sweep, so it must carry the marker");

        var jobClient = _fixture.BeginScope().Resolve<InMemoryBackgroundJobClient>();
        jobClient.AutoExecute = false;   // a sweep may re-dispatch work; record it rather than run it here

        try
        {
            using var scope = _fixture.BeginScope();
            await scope.Resolve<IMediator>().Send(Activator.CreateInstance(commandType)!);
        }
        finally
        {
            jobClient.AutoExecute = true;
        }
    }

    private async Task<Guid> SeedStaleRunningRunAsync(Guid teamId)
    {
        var runId = Guid.NewGuid();
        // Far older than any sibling test's stale run, not just past the window. The sweep takes the 50
        // longest-lapsed leases (AgentRunReconcilerService.cs:329), and this suite shares one database with
        // several classes that seed ~20-minute-old ones — a row merely eligible could be crowded out of the batch
        // and the assertion below would fail for a reason that has nothing to do with what it measures.
        var stamp = DateTimeOffset.UtcNow - TimeSpan.FromDays(365);

        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        // Lapsed lease + no events = the abandon path's precondition, the same shape AgentRunRecoveryFlowTests seeds.
        db.AgentRun.Add(new AgentRun { Id = runId, TeamId = teamId, Harness = "codex-cli", Status = AgentRunStatus.Running, StartedAt = stamp, HeartbeatAt = stamp, LeaseExpiresAt = stamp + AgentRunLiveness.Window });

        await db.SaveChangesAsync();
        return runId;
    }

    /// <summary>Seed the lost-dispatch state: a Queued run parked past the liveness window under a still-LIVE parent, plus the pending wait that selects it.</summary>
    private async Task<Guid> SeedStuckQueuedRunAsync(Guid teamId, Guid parentRunId)
    {
        var runId = Guid.NewGuid();

        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        db.AgentRun.Add(new AgentRun
        {
            Id = runId, TeamId = teamId, Harness = "codex-cli", Status = AgentRunStatus.Queued,
            WorkflowRunId = parentRunId, NodeId = "agent", IterationKey = "",
            CreatedDate = DateTimeOffset.UtcNow - TimeSpan.FromMinutes(20),
        });

        db.WorkflowRunWait.Add(new WorkflowRunWait
        {
            Id = Guid.NewGuid(), RunId = parentRunId, NodeId = "agent", IterationKey = "",
            WaitKind = WorkflowWaitKinds.AgentRun, Token = runId.ToString(),
            Status = WorkflowWaitStatuses.Pending, CreatedAt = DateTimeOffset.UtcNow,
        });

        await db.SaveChangesAsync();
        return runId;
    }

    /// <summary>Seed one still-held map-branch admission estimate on a run that has since gone terminal — the state the release pass exists to return. No expiry, so the sweep's overdue pass cannot claim it first.</summary>
    private async Task<Guid> SeedMapBranchReservationAsync(Guid teamId, Guid workflowRunId)
    {
        var reservationId = Guid.NewGuid();

        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        db.BudgetReservation.Add(new BudgetReservation
        {
            Id = reservationId, TeamId = teamId, WorkflowRunId = workflowRunId,
            Kind = WorkflowEngine.MapBranchReservationKind, ScopeKey = $"sweep-{reservationId:N}",
            State = BudgetReservationStates.Reserved, ReservedUsd = 1m, PriceVersion = "realized-v1",
        });

        await db.SaveChangesAsync();
        return reservationId;
    }

    /// <summary>Seed a parent workflow run (request + run) in the given status — WorkflowId null (no Workflow row needed; the FK is optional).</summary>
    private async Task<Guid> SeedWorkflowRunAsync(Guid teamId, WorkflowRunStatus status)
    {
        var requestId = Guid.NewGuid();
        var runId = Guid.NewGuid();

        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        db.WorkflowRunRequest.Add(new WorkflowRunRequest
        {
            Id = requestId, TeamId = teamId, SourceType = WorkflowRunSourceTypes.Manual, ActorType = "user",
            ActorId = SystemUsers.SeederId, NormalizedPayloadJson = "{}", Status = WorkflowRunRequestStatus.Consumed,
            ReceivedAt = DateTimeOffset.UtcNow, VerifiedAt = DateTimeOffset.UtcNow, NormalizedAt = DateTimeOffset.UtcNow,
        });
        db.WorkflowRun.Add(new WorkflowRun
        {
            Id = runId, TeamId = teamId, RunRequestId = requestId, SourceType = WorkflowRunSourceTypes.Manual,
            Status = status, CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId,
        });

        await db.SaveChangesAsync();
        return runId;
    }

    private async Task<Guid> SeedTeamAsync()
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var userId = Guid.NewGuid();
        db.User.Add(new User { Id = userId, Email = $"sweep-{userId:N}@test.local", Name = $"sweep-{userId:N}" });

        var teamId = Guid.NewGuid();
        db.Team.Add(new Team { Id = teamId, Slug = $"sweep-{teamId:N}", Name = "Sweep Team", Kind = TeamKind.Workspace });
        db.TeamMembership.Add(new TeamMembership { Id = Guid.NewGuid(), TeamId = teamId, UserId = userId, Role = TeamRole.Owner });

        await db.SaveChangesAsync();
        return teamId;
    }
}
