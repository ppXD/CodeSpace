using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Workflows.Reconciliation;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Commands.Workflows;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Enums;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows;

/// <summary>
/// Proves the three reconciler sweeps recover stuck rows correctly.
///
/// <para>The reconciler is the safety net behind the dispatcher's CAS — without it, a row
/// stuck in Pending / Enqueued / Running (because of a process crash or Hangfire outage)
/// would freeze forever. These tests force each stuck state by manipulating row timestamps
/// past the threshold, fire the reconciler, then assert the row landed in the expected
/// recovery state.</para>
///
/// <para>We exercise the reconciler via the MediatR command (the same path the recurring
/// job uses) so the handler delegation is also tested end-to-end.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
public class StuckRunReconcilerFlowTests
{
    private readonly PostgresFixture _fixture;

    public StuckRunReconcilerFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task Stuck_pending_is_redispatched_and_lifts_to_enqueued()
    {
        // Scenario: a workflow_run row was inserted by RunStarter but the process crashed
        // before DispatchAsync. The row sits in Pending with no progress; the reconciler
        // must re-dispatch it, which CAS-flips Pending → Enqueued.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowId = await CreateWorkflowAsync(teamId, userId);

        var runId = await StageStuckRunAsync(
            workflowId, teamId,
            status: WorkflowRunStatus.Pending,
            createdAgo: StuckRunReconcilerService.PendingStuckAfter + TimeSpan.FromMinutes(1));

        var summary = await ReconcileAsync();

        summary.RedispatchedFromPending.ShouldBe(1, "the stuck Pending row must be re-dispatched");
        summary.RevertedFromEnqueued.ShouldBe(0);
        summary.MarkedAbandonedFromRunning.ShouldBe(0);

        (await ReadStatusAsync(runId)).ShouldBe(WorkflowRunStatus.Enqueued,
            "after the dispatcher's CAS lifts the row, it sits in Enqueued waiting for the Hangfire worker (in-memory in tests)");
    }

    [Fact]
    public async Task Recent_pending_is_NOT_redispatched()
    {
        // Negative case: a Pending row younger than the threshold MUST be left alone — it's
        // a legitimate in-flight dispatch that hasn't transitioned to Enqueued yet (the
        // dispatcher's CAS hasn't completed). Touching it would risk double-dispatch.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowId = await CreateWorkflowAsync(teamId, userId);

        var runId = await StageStuckRunAsync(
            workflowId, teamId,
            status: WorkflowRunStatus.Pending,
            createdAgo: TimeSpan.Zero);   // just-created

        var summary = await ReconcileAsync();

        summary.RedispatchedFromPending.ShouldBe(0,
            "Pending rows younger than the threshold must not be touched — they're in flight");
        (await ReadStatusAsync(runId)).ShouldBe(WorkflowRunStatus.Pending,
            "the young Pending row must remain Pending");
    }

    [Fact]
    public async Task Stuck_enqueued_is_reverted_to_pending_for_next_tick()
    {
        // Scenario: dispatcher CAS-flipped to Enqueued + handed to Hangfire, but Hangfire
        // dropped the job (storage outage, queue mis-routing). The row sits in Enqueued
        // forever unless we revert it; reconciler walks it back to Pending so the next
        // sweep can re-dispatch.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowId = await CreateWorkflowAsync(teamId, userId);

        var runId = await StageStuckRunAsync(
            workflowId, teamId,
            status: WorkflowRunStatus.Enqueued,
            createdAgo: StuckRunReconcilerService.EnqueuedStuckAfter + TimeSpan.FromMinutes(1),
            backdateLastModified: true);

        var summary = await ReconcileAsync();

        summary.RevertedFromEnqueued.ShouldBe(1, "the stuck Enqueued row must walk back to Pending");
        (await ReadStatusAsync(runId)).ShouldBe(WorkflowRunStatus.Pending,
            "post-revert, the row is Pending and the NEXT reconciler tick (or a new dispatcher call) re-claims it");
    }

    [Fact]
    public async Task Abandoned_running_is_marked_failure_with_reason()
    {
        // Scenario: engine CAS-flipped Enqueued → Running, then the worker died. The row
        // sits in Running with no ledger progress past the threshold. Reconciler marks
        // Failure with an actionable error so the operator can Replay.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowId = await CreateWorkflowAsync(teamId, userId);

        var runId = await StageStuckRunAsync(
            workflowId, teamId,
            status: WorkflowRunStatus.Running,
            createdAgo: StuckRunReconcilerService.RunningStuckAfter + TimeSpan.FromMinutes(5),
            startedAtAgo: StuckRunReconcilerService.RunningStuckAfter + TimeSpan.FromMinutes(5));

        var summary = await ReconcileAsync();

        summary.MarkedAbandonedFromRunning.ShouldBe(1,
            "the Running row with no ledger activity past the threshold must be marked Failure");

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();
        var run = await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId);
        run.Status.ShouldBe(WorkflowRunStatus.Failure);
        run.Error.ShouldNotBeNullOrEmpty();
        run.Error!.ShouldContain("abandoned",
            customMessage: "error MUST surface 'abandoned' so the operator knows what happened");
        run.Error.ShouldContain("Replay",
            customMessage: "error MUST tell the operator the recovery action");
        run.CompletedAt.ShouldNotBeNull("CompletedAt must be set when transitioning to terminal");

        var failedRecord = await db.WorkflowRunRecord.AsNoTracking()
            .SingleOrDefaultAsync(r => r.RunId == runId && r.RecordType == WorkflowRunRecordTypes.RunFailed);
        failedRecord.ShouldNotBeNull("run.failed ledger record MUST be emitted so the timeline reflects the recovery decision");
    }

    [Fact]
    public async Task Running_with_recent_ledger_activity_is_NOT_marked_abandoned()
    {
        // Negative case: a Running row with a recent ledger entry is alive (e.g. mid-LLM-
        // call). Marking it Failure would corrupt the in-flight execution. The "liveness
        // window" check is what makes this safe — we wait for the absence of ledger
        // activity before declaring death.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowId = await CreateWorkflowAsync(teamId, userId);

        var runId = await StageStuckRunAsync(
            workflowId, teamId,
            status: WorkflowRunStatus.Running,
            createdAgo: StuckRunReconcilerService.RunningStuckAfter + TimeSpan.FromMinutes(5),
            startedAtAgo: StuckRunReconcilerService.RunningStuckAfter + TimeSpan.FromMinutes(5));

        // Emit a fresh ledger record so the liveness check sees recent activity.
        await SeedLedgerRecordAsync(runId, WorkflowRunRecordTypes.NodeStarted, DateTimeOffset.UtcNow.AddSeconds(-30));

        var summary = await ReconcileAsync();

        summary.MarkedAbandonedFromRunning.ShouldBe(0,
            "Running rows with recent ledger activity are alive — must NOT be marked Failure");
        (await ReadStatusAsync(runId)).ShouldBe(WorkflowRunStatus.Running);
    }

    [Fact]
    public async Task Reconciler_sweeps_all_three_states_in_a_single_invocation()
    {
        // End-to-end: a mixed-population sweep recovers all three stuck classes at once.
        //
        // <b>Why we assert on final per-row state, not on summary counts:</b> the integration
        // tests in this collection share a PostgresFixture (single DB across the whole run).
        // Earlier tests in the file leave stuck rows behind (e.g. the "stuck Enqueued reverted
        // to Pending" test ends with the row still Pending + old CreatedDate). Those rows
        // would inflate the summary counts here — RedispatchedFromPending = 2 instead of 1.
        // Asserting on the three specific runIds keeps the test invariants robust to whatever
        // other Pending/Enqueued/Running rows the shared fixture contains.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowId = await CreateWorkflowAsync(teamId, userId);

        var stuckPending = await StageStuckRunAsync(workflowId, teamId, WorkflowRunStatus.Pending,
            createdAgo: StuckRunReconcilerService.PendingStuckAfter + TimeSpan.FromMinutes(1));
        var stuckEnqueued = await StageStuckRunAsync(workflowId, teamId, WorkflowRunStatus.Enqueued,
            createdAgo: StuckRunReconcilerService.EnqueuedStuckAfter + TimeSpan.FromMinutes(1),
            backdateLastModified: true);
        var abandonedRunning = await StageStuckRunAsync(workflowId, teamId, WorkflowRunStatus.Running,
            createdAgo: StuckRunReconcilerService.RunningStuckAfter + TimeSpan.FromMinutes(5),
            startedAtAgo: StuckRunReconcilerService.RunningStuckAfter + TimeSpan.FromMinutes(5));

        await ReconcileAsync();

        (await ReadStatusAsync(stuckPending)).ShouldBe(WorkflowRunStatus.Enqueued,
            "the stuck Pending row must have been re-dispatched into Enqueued");
        (await ReadStatusAsync(stuckEnqueued)).ShouldBe(WorkflowRunStatus.Pending,
            "the stuck Enqueued row must have been reverted to Pending for the next tick");
        (await ReadStatusAsync(abandonedRunning)).ShouldBe(WorkflowRunStatus.Failure,
            "the abandoned Running row must have been marked Failure");
    }

    // ─── Helpers ──────────────────────────────────────────────────────────────────

    private async Task<Guid> CreateWorkflowAsync(Guid teamId, Guid userId)
    {
        using var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin);
        var mediator = scope.Resolve<IMediator>();
        return await mediator.Send(new CreateWorkflowCommand
        {
            Name = "reconciler-" + Guid.NewGuid().ToString("N")[..8],
            Description = null,
            Definition = WorkflowsTestSeed.MinimalDefinition(),
            Activations = new List<WorkflowActivationInput>(),
            Enabled = true,
        });
    }

    /// <summary>
    /// Stages a workflow_run row in the requested status with timestamps backdated to
    /// simulate a stuck row. The dates are set via raw SQL because EF's change tracker
    /// resets CreatedDate on insert — we need to backdate AFTER the insert to bypass.
    /// </summary>
    private async Task<Guid> StageStuckRunAsync(Guid workflowId, Guid teamId, WorkflowRunStatus status,
        TimeSpan createdAgo, TimeSpan? startedAtAgo = null, bool backdateLastModified = false)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var requestId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var createdAt = now - createdAgo;
        var startedAt = startedAtAgo.HasValue ? now - startedAtAgo.Value : (DateTimeOffset?)null;

        db.WorkflowRunRequest.Add(new WorkflowRunRequest
        {
            Id = requestId,
            TeamId = teamId,
            WorkflowId = workflowId,
            SourceType = WorkflowRunSourceTypes.Manual,
            ActorType = "user",
            ActorId = SystemUsers.SeederId,
            NormalizedPayloadJson = "{}",
            Status = WorkflowRunRequestStatus.Consumed,
            ReceivedAt = now,
            VerifiedAt = now,
            NormalizedAt = now,
        });

        db.WorkflowRun.Add(new WorkflowRun
        {
            Id = runId,
            WorkflowId = workflowId,
            WorkflowVersion = 1,
            TeamId = teamId,
            RunRequestId = requestId,
            Status = status,
            // Phase 3.0 hardening — Enqueued status now requires EnqueuedAt to be set
            // (the dispatcher's CAS stamps it; the reconciler's stuck-Enqueued sweep
            // reads it). Backdate it alongside CreatedDate so a staged "stuck Enqueued
            // for 11 minutes" row actually looks stale to the reconciler.
            EnqueuedAt = status == WorkflowRunStatus.Enqueued ? createdAt : null,
            StartedAt = startedAt,
            CreatedBy = SystemUsers.SeederId,
            LastModifiedBy = SystemUsers.SeederId,
        });

        await db.SaveChangesAsync();

        // Backdate timestamps via raw SQL to bypass EF's auto-stamping. Done in a second
        // round-trip because EF resets these on insert.
        var lastModifiedSet = backdateLastModified
            ? ", last_modified_date = {1}"
            : "";
        await db.Database.ExecuteSqlRawAsync(
            $"UPDATE workflow_run SET created_date = {{0}}{lastModifiedSet} WHERE id = {{{(backdateLastModified ? 2 : 1)}}}",
            backdateLastModified
                ? new object[] { createdAt, createdAt, runId }
                : new object[] { createdAt, runId });

        return runId;
    }

    private async Task SeedLedgerRecordAsync(Guid runId, string recordType, DateTimeOffset occurredAt)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        db.WorkflowRunRecord.Add(new WorkflowRunRecord
        {
            Id = Guid.NewGuid(),
            RunId = runId,
            Sequence = 1,
            RecordType = recordType,
            NodeId = "n1",
            IterationKey = string.Empty,
            CorrelationId = null,
            PayloadJson = "{}",
            OccurredAt = occurredAt,
        });

        await db.SaveChangesAsync();
    }

    private async Task<ReconcileStuckRunsResponse> ReconcileAsync()
    {
        using var scope = _fixture.BeginScope();
        var mediator = scope.Resolve<IMediator>();
        return await mediator.Send(new ReconcileStuckRunsCommand());
    }

    private async Task<WorkflowRunStatus> ReadStatusAsync(Guid runId)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<CodeSpaceDbContext>().WorkflowRun.AsNoTracking()
            .Where(r => r.Id == runId)
            .Select(r => r.Status)
            .SingleAsync();
    }
}
