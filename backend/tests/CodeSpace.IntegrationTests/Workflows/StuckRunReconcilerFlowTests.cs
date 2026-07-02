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
[Trait("Category", "Integration")]
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

    [Fact]
    public async Task Suspended_run_with_a_pending_wait_is_never_swept_by_the_reconciler()
    {
        // Engine v2 Phase 1: a run paused on a suspended node sits in Suspended — intentionally
        // parked (waiting on a timer / approval / callback), NOT stuck. The Pending/Enqueued/Running
        // sweeps target their own statuses only, so they never match a Suspended row; the stranded-
        // Suspended sweep DOES match Suspended but is gated on ZERO pending waits, so a genuinely
        // parked run (one that still HAS a Pending wait — exactly what this stages) survives every
        // sweep however old it is. Without this, a workflow waiting on a long sleep or a human
        // approval would be wrongly recovered.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowId = await CreateWorkflowAsync(teamId, userId);

        var runId = await StageStuckRunAsync(
            workflowId, teamId,
            status: WorkflowRunStatus.Suspended,
            createdAgo: StuckRunReconcilerService.RunningStuckAfter + TimeSpan.FromHours(2),
            startedAtAgo: StuckRunReconcilerService.RunningStuckAfter + TimeSpan.FromHours(2),
            backdateLastModified: true);

        // The parked signature: an outstanding Pending wait this run is genuinely waiting on.
        await SeedWaitAsync(runId, "start", WorkflowWaitStatuses.Pending);

        var summary = await ReconcileAsync();

        summary.MarkedAbandonedFromRunning.ShouldBe(0,
            "a Suspended run must NOT be counted as an abandoned Running run, however old it is");
        summary.RedispatchedFromStrandedSuspended.ShouldBe(0,
            "a Suspended run that still HAS a Pending wait is parked, not stranded — the stranded sweep must skip it");

        (await ReadStatusAsync(runId)).ShouldBe(WorkflowRunStatus.Suspended,
            "a parked Suspended run must survive every reconciler sweep — the status-scoped sweeps don't match it " +
            "and the stranded-Suspended sweep is excluded by its outstanding Pending wait");
    }

    [Fact]
    public async Task Stranded_suspended_with_zero_pending_waits_is_redispatched_and_reaches_terminal()
    {
        // The resume-flip-before-resolve race's residual: a run resolved its last wait in the narrow
        // window AFTER an in-flight re-walk passed that branch node, so the walk re-suspended the run
        // while the resolver's Suspended→Pending flip no-op'd (the run was momentarily Running). The
        // run is now Suspended with ALL waits Resolved and NO dispatch coming — stranded forever. We
        // simulate that exact end-state directly (Suspended + a Resolved wait + an old LastModifiedDate),
        // run the reconciler, and assert it re-dispatches AND — driving the engine — the run reaches
        // its terminal Success state (the resolved wait rehydrates; the rest of the graph walks out).
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowId = await CreateWorkflowAsync(teamId, userId);   // start(trigger) -> end(terminal)

        var runId = await StageStuckRunAsync(
            workflowId, teamId,
            status: WorkflowRunStatus.Suspended,
            createdAgo: StuckRunReconcilerService.SuspendedStrandedAfter + TimeSpan.FromMinutes(5),
            backdateLastModified: true);

        // Pre-record the trigger as already completed (as if it ran, the run suspended downstream,
        // then stranded) so the resumed walk doesn't re-run a settled node — mirrors the durable
        // re-entry pattern. Stamp the run's only wait as RESOLVED — the stranded signature.
        await PreRecordNodeCompletedAsync(runId, "start");
        await SeedWaitAsync(runId, "start", WorkflowWaitStatuses.Resolved);

        var summary = await ReconcileAsync();

        summary.RedispatchedFromStrandedSuspended.ShouldBe(1,
            "a Suspended run past the grace window with zero pending waits is stranded — the 4th sweep must re-dispatch it");

        (await ReadStatusAsync(runId)).ShouldBe(WorkflowRunStatus.Enqueued,
            "after the CAS Suspended→Pending + the dispatcher's Pending→Enqueued, the row waits in Enqueued for the worker");

        // Drive the engine the way the Hangfire worker would, and prove the run actually completes —
        // the recovery is only real if the re-dispatched run reaches a terminal state, not just Enqueued.
        await RunEngineAsync(runId);

        (await ReadStatusAsync(runId)).ShouldBe(WorkflowRunStatus.Success,
            customMessage: "the re-dispatched stranded run must walk to terminal Success — if it re-suspended or " +
                           "stayed Enqueued, the sweep moved the row but the engine couldn't actually finish it");
    }

    [Fact]
    public async Task On_demand_continue_redispatches_a_stranded_suspended_run_and_it_reaches_terminal()
    {
        // P1.3: the user-triggered twin of the stranded-Suspended sweep — a run stranded Suspended with NO pending
        // wait is continued NOW (no ≤2-min wait), driving the SAME CAS Suspended→Pending + dispatch, and the engine
        // walks it to terminal Success. No grace-window backdate needed: continue is on demand, not time-gated.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowId = await CreateWorkflowAsync(teamId, userId);   // start(trigger) -> end(terminal)

        var runId = await StageStuckRunAsync(workflowId, teamId, status: WorkflowRunStatus.Suspended, createdAgo: TimeSpan.FromMinutes(1));
        await PreRecordNodeCompletedAsync(runId, "start");
        await SeedWaitAsync(runId, "start", WorkflowWaitStatuses.Resolved);   // the stranded signature: a RESOLVED wait, no pending one

        (await ContinueAsync(runId, teamId)).ShouldBeTrue("a stranded Suspended run (no pending wait) continues on demand");

        (await ReadStatusAsync(runId)).ShouldBe(WorkflowRunStatus.Enqueued, "after the CAS Suspended→Pending + the dispatcher's Pending→Enqueued");

        await RunEngineAsync(runId);

        (await ReadStatusAsync(runId)).ShouldBe(WorkflowRunStatus.Success, "the continued run walks to terminal Success — the same recovery the sweep performs");
    }

    [Fact]
    public async Task Continue_is_a_no_op_for_a_suspended_run_that_still_has_a_pending_wait()
    {
        // A Suspended run still parked on a Pending wait is legitimately waiting (approval / timer / callback) — it
        // resumes via /resume or its signal, NOT continue. Continue must no-op (false) and never bypass the wait.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowId = await CreateWorkflowAsync(teamId, userId);

        var runId = await StageStuckRunAsync(workflowId, teamId, status: WorkflowRunStatus.Suspended, createdAgo: TimeSpan.FromMinutes(1));
        await SeedWaitAsync(runId, "start", WorkflowWaitStatuses.Pending);

        (await ContinueAsync(runId, teamId)).ShouldBeFalse("a parked Suspended run with a pending wait must not be force-continued");
        (await ReadStatusAsync(runId)).ShouldBe(WorkflowRunStatus.Suspended, "it stays parked on its wait");
    }

    [Theory]
    [InlineData(WorkflowRunStatus.Success)]
    [InlineData(WorkflowRunStatus.Cancelled)]
    public async Task Continue_is_a_no_op_for_a_succeeded_or_cancelled_run(WorkflowRunStatus terminal)
    {
        // Success / Cancelled are truly terminal — there is nothing to revive in place. (A FAILURE run CAN continue in
        // place when it has a resettable unhandled-failed node — see the flaky-node E2E in RerunFromNodeFlowTests; a
        // Failure with no recorded failed cell is a no-op, covered below.)
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowId = await CreateWorkflowAsync(teamId, userId);

        var runId = await StageStuckRunAsync(workflowId, teamId, status: terminal, createdAgo: TimeSpan.FromMinutes(1));

        (await ContinueAsync(runId, teamId)).ShouldBeFalse("a Success / Cancelled run cannot continue in place");
        (await ReadStatusAsync(runId)).ShouldBe(terminal, "it stays terminal, untouched");
    }

    [Fact]
    public async Task Continue_is_a_no_op_for_a_failure_with_no_recorded_failed_node()
    {
        // A Failure run whose ledger records NO failed top-level node cell (only a bare status) has nothing to reset →
        // continue is a clean no-op (false), leaving it terminal. Guards that ContinueFailedRunAsync never flips a run
        // it can't actually re-run — the operator falls back to replay / rerun-from-node.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowId = await CreateWorkflowAsync(teamId, userId);

        var runId = await StageStuckRunAsync(workflowId, teamId, status: WorkflowRunStatus.Failure, createdAgo: TimeSpan.FromMinutes(1));

        (await ContinueAsync(runId, teamId)).ShouldBeFalse("a Failure with no resettable failed-node cell can't continue in place");
        (await ReadStatusAsync(runId)).ShouldBe(WorkflowRunStatus.Failure, "it stays terminal — nothing was flipped");
    }

    [Fact]
    public async Task Continue_a_foreign_team_run_throws_not_found_and_leaks_nothing()
    {
        var (teamA, userA) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var (teamB, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowId = await CreateWorkflowAsync(teamA, userA);

        var runId = await StageStuckRunAsync(workflowId, teamA, status: WorkflowRunStatus.Suspended, createdAgo: TimeSpan.FromMinutes(1));

        await Should.ThrowAsync<KeyNotFoundException>(async () => await ContinueAsync(runId, teamB));
        (await ReadStatusAsync(runId)).ShouldBe(WorkflowRunStatus.Suspended, "the foreign-team continue is a clean 404, leaving the run untouched");
    }

    [Fact]
    public async Task Continue_is_a_no_op_for_an_active_running_run()
    {
        // A Running run is mid-flight, not stranded — continue must NOT touch it (the guard fences every non-Suspended
        // status, so a future guard refactor can't silently start re-dispatching an active run).
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowId = await CreateWorkflowAsync(teamId, userId);

        var runId = await StageStuckRunAsync(workflowId, teamId, status: WorkflowRunStatus.Running, createdAgo: TimeSpan.FromMinutes(1));

        (await ContinueAsync(runId, teamId)).ShouldBeFalse("a Running run is active, not stranded — continue is a no-op");
        (await ReadStatusAsync(runId)).ShouldBe(WorkflowRunStatus.Running, "it stays Running, untouched");
    }

    [Fact]
    public async Task Two_concurrent_continues_drive_exactly_one_redispatch()
    {
        // Race-safety: two operators (or a continue racing the reconciler sweep) hit the same stranded run. The CAS
        // Suspended→Pending serializes them — EXACTLY ONE wins (true), the loser 0-rows to a clean false, and the run
        // is enqueued ONCE (no double-dispatch; the dispatcher's Pending→Enqueued CAS is the second guard).
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowId = await CreateWorkflowAsync(teamId, userId);

        var runId = await StageStuckRunAsync(workflowId, teamId, status: WorkflowRunStatus.Suspended, createdAgo: TimeSpan.FromMinutes(1));
        await PreRecordNodeCompletedAsync(runId, "start");
        await SeedWaitAsync(runId, "start", WorkflowWaitStatuses.Resolved);

        var results = await Task.WhenAll(ContinueAsync(runId, teamId), ContinueAsync(runId, teamId));

        results.Count(won => won).ShouldBe(1, "exactly one concurrent continue wins the CAS; the other is a clean no-op");
        (await ReadStatusAsync(runId)).ShouldBe(WorkflowRunStatus.Enqueued, "the run is enqueued exactly once — no double-dispatch");
    }

    [Fact]
    public async Task Stranded_suspended_with_a_resolved_suspending_node_wait_resumes_from_payload_and_reaches_success()
    {
        // The FAITHFUL stranded scenario the sibling recovery test above only approximates: the orphaned wait
        // belongs to a REAL SUSPENDING node (the SuspendProbeNode, the agent.code stand-in), not the trigger.
        // The other test pre-records the wait on the already-settled "start" trigger, so the re-walk treats it
        // as done and never re-runs a node that consumes its rehydrated payload — it only proves "re-queue +
        // walk the remaining frontier". Here we drive the REAL engine to a genuine park (real branch ledger +
        // a real WorkflowRunWait under iteration key "map#0"), then reproduce the exact orphan: stamp that
        // suspending node's wait Resolved (with the payload it expects on resume) WITHOUT going through the
        // resume service, so NO Suspended→Pending flip / dispatch happens — the run is stranded Suspended with
        // its sole wait Resolved. The sweep must re-dispatch it AND, on the engine re-walk, the suspending node
        // must actually RESUME from its rehydrated payload (proven by results[0].summary, a value the node only
        // emits on its resumed pass) and the run must reach terminal Success — not re-strand.
        var key = "sp-" + Guid.NewGuid().ToString("N");
        SuspendProbeNode.Reset(key);

        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowId = await CreateSuspendingMapWorkflowAsync(teamId, userId, key);   // trigger -> map[suspending body] -> terminal
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId, payloadJson: """{ "things": ["solo"] }""");

        // Drive the engine to a REAL parked state: the suspending leaf node commits its own WorkflowRunWait
        // (token "<key>::solo", iteration key "map#0") and the run flips to Suspended. One element ⇒ one wait.
        await RunEngineAsync(runId);
        (await ReadStatusAsync(runId)).ShouldBe(WorkflowRunStatus.Suspended, "the suspending body node parked a real wait");
        SuspendProbeNode.FirstPassCount(key, "solo").ShouldBe(1, "the suspending node ran its first (parking) pass exactly once");

        // Reproduce the orphan end-state directly on the REAL wait: resolve it (with the resume payload the node
        // expects) but DON'T route through the resume service, so the Suspended→Pending flip + dispatch never
        // fire. Then backdate LastModifiedDate past the grace window. Now: Suspended + zero Pending + 1 Resolved
        // suspending-node wait + stale — the stranded signature, but with a wait that drives a node on re-walk.
        await ResolveWaitInPlaceAsync(runId, $"{key}::solo", """{ "summary": "RES-solo" }""");
        await BackdateLastModifiedAsync(runId, StuckRunReconcilerService.SuspendedStrandedAfter + TimeSpan.FromMinutes(5));

        var summary = await ReconcileAsync();

        summary.RedispatchedFromStrandedSuspended.ShouldBe(1,
            "the stranded Suspended run (zero pending waits, past the grace window) must be re-dispatched by the 4th sweep");
        (await ReadStatusAsync(runId)).ShouldBe(WorkflowRunStatus.Enqueued,
            "after the CAS Suspended→Pending + the dispatcher's Pending→Enqueued, the row waits in Enqueued for the worker");

        // Drive the engine the way the Hangfire worker would. The crux: the suspending node must RESUME from its
        // rehydrated wait payload (not re-park, not re-run its first pass) and the run must walk to Success.
        await RunEngineAsync(runId);

        (await ReadStatusAsync(runId)).ShouldBe(WorkflowRunStatus.Success,
            customMessage: "the re-dispatched stranded run must walk to terminal Success — if it re-suspended, the " +
                           "suspending node's wait was not rehydrated as its ResumePayload and the run re-stranded");

        SuspendProbeNode.FirstPassCount(key, "solo").ShouldBe(1,
            "the suspending node did NOT re-run its parking first pass on the recovery re-walk — it RESUMED from the resolved wait");

        // The observable that PROVES the resume consumed the rehydrated payload: results[0].summary is "RES-solo",
        // a value SuspendProbeNode only emits on its RESUMED pass (echoing the resolved wait's payload). A re-walk
        // that merely advanced the remaining frontier without resuming this node could not produce it.
        using var done = _fixture.BeginScope();
        var db = done.Resolve<CodeSpaceDbContext>();
        var mapNode = await db.WorkflowRunNode.AsNoTracking().SingleAsync(n => n.RunId == runId && n.NodeId == "map" && n.IterationKey == "");
        var results = System.Text.Json.JsonDocument.Parse(mapNode.OutputsJson).RootElement.GetProperty("results");
        results.GetArrayLength().ShouldBe(1);
        results[0].GetProperty("item").GetString().ShouldBe("solo", "the resumed branch echoed its own element");
        results[0].GetProperty("summary").GetString().ShouldBe("RES-solo",
            "the suspending node resumed from its rehydrated wait payload — this summary exists only in the resolved wait");
    }

    [Fact]
    public async Task Stranded_suspended_multi_branch_map_with_all_waits_resolved_redispatches_and_every_branch_resumes()
    {
        // The K>1 generalisation of the stranded-suspended map recovery: a MULTI-branch (K=2) flow.map parked TWO
        // real branch waits, then ALL of them resolved in the narrow flip-before-resolve window so the Suspended→
        // Pending flip + dispatch never fired — the run is stranded Suspended with ZERO pending waits but MORE than
        // one resolved suspending-node wait. The sibling single-element test can't catch a multi-branch re-walk bug
        // (e.g. only one branch rehydrated, or a settled branch re-firing). The sweep must re-dispatch the run AND,
        // on the engine re-walk, BOTH branches resume from their own rehydrated payload and the run reaches Success.
        var key = "sp-" + Guid.NewGuid().ToString("N");
        SuspendProbeNode.Reset(key);

        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowId = await CreateSuspendingMapWorkflowAsync(teamId, userId, key);
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId, payloadJson: """{ "things": ["a", "b"] }""");

        // Drive to a REAL K=2 parked state: two branches each commit their own WorkflowRunWait, the run suspends.
        await RunEngineAsync(runId);
        (await ReadStatusAsync(runId)).ShouldBe(WorkflowRunStatus.Suspended, "both branches parked their own real wait");
        SuspendProbeNode.FirstPassCount(key, "a").ShouldBe(1);
        SuspendProbeNode.FirstPassCount(key, "b").ShouldBe(1, "each branch parked exactly once");

        // Reproduce the orphan end-state on BOTH real waits: resolve each in place (with its resume payload) WITHOUT
        // routing through the resume service, so the flip + dispatch never fire. Then backdate past the grace window.
        await ResolveWaitInPlaceAsync(runId, $"{key}::a", """{ "summary": "RES-a" }""");
        await ResolveWaitInPlaceAsync(runId, $"{key}::b", """{ "summary": "RES-b" }""");
        await BackdateLastModifiedAsync(runId, StuckRunReconcilerService.SuspendedStrandedAfter + TimeSpan.FromMinutes(5));

        var summary = await ReconcileAsync();

        summary.RedispatchedFromStrandedSuspended.ShouldBe(1,
            "a multi-branch Suspended run with zero pending waits past the grace window is stranded — the sweep re-dispatches it once");
        (await ReadStatusAsync(runId)).ShouldBe(WorkflowRunStatus.Enqueued, "after the CAS Suspended→Pending + dispatcher Pending→Enqueued");

        // Drive the engine the way the worker would: BOTH branches must resume from their rehydrated payloads (not
        // re-park, not re-run their first pass) and the run must walk to Success with the ordered reduce.
        await RunEngineAsync(runId);

        (await ReadStatusAsync(runId)).ShouldBe(WorkflowRunStatus.Success,
            customMessage: "the re-dispatched multi-branch stranded run must reach Success — if it re-suspended, a branch wait wasn't rehydrated");

        SuspendProbeNode.FirstPassCount(key, "a").ShouldBe(1, "branch a resumed from its wait — did NOT re-run its parking pass on the recovery re-walk");
        SuspendProbeNode.FirstPassCount(key, "b").ShouldBe(1, "branch b likewise resumed exactly once");

        using var done = _fixture.BeginScope();
        var db = done.Resolve<CodeSpaceDbContext>();
        var mapNode = await db.WorkflowRunNode.AsNoTracking().SingleAsync(n => n.RunId == runId && n.NodeId == "map" && n.IterationKey == "");
        var results = System.Text.Json.JsonDocument.Parse(mapNode.OutputsJson).RootElement.GetProperty("results");
        results.GetArrayLength().ShouldBe(2, "both branches reduced");
        results[0].GetProperty("summary").GetString().ShouldBe("RES-a", "branch 0 resumed from its OWN rehydrated payload, ordered by index");
        results[1].GetProperty("summary").GetString().ShouldBe("RES-b", "branch 1 resumed from its own payload — no cross-branch contamination");
    }

    [Fact]
    public async Task Suspended_with_a_pending_wait_is_NOT_swept_however_old()
    {
        // False-positive guard #1 + #2 + #4: a run legitimately parked on a human approval/action for
        // hours, on a timer/delay, or a freshly-suspended map with K branch waits — ALL have at least
        // one Pending wait. The zero-pending-waits predicate excludes them outright, regardless of age,
        // so the sweep never murders a run that's genuinely waiting for a signal.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowId = await CreateWorkflowAsync(teamId, userId);

        var runId = await StageStuckRunAsync(
            workflowId, teamId,
            status: WorkflowRunStatus.Suspended,
            createdAgo: StuckRunReconcilerService.SuspendedStrandedAfter + TimeSpan.FromHours(2),
            backdateLastModified: true);

        // One PENDING wait — the legitimately-parked signature (approval / timer / map branch).
        await SeedWaitAsync(runId, "start", WorkflowWaitStatuses.Pending);

        var summary = await ReconcileAsync();

        summary.RedispatchedFromStrandedSuspended.ShouldBe(0,
            "a Suspended run with a Pending wait is parked, not stranded — it must NOT be swept, however old it is");
        (await ReadStatusAsync(runId)).ShouldBe(WorkflowRunStatus.Suspended,
            "a run waiting on a Pending wait stays Suspended until its real signal arrives");
    }

    [Fact]
    public async Task Suspended_with_zero_pending_waits_but_within_grace_window_is_NOT_swept()
    {
        // False-positive guard #3: the microsecond window during a NORMAL last-wait resume — the run
        // is momentarily Suspended with zero pending waits between the resolve CAS and the
        // Suspended→Pending flip. A fresh LastModifiedDate keeps it inside the grace window, so the
        // sweep leaves it alone and lets the concurrent flip drive the dispatch.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowId = await CreateWorkflowAsync(teamId, userId);

        var runId = await StageStuckRunAsync(
            workflowId, teamId,
            status: WorkflowRunStatus.Suspended,
            createdAgo: TimeSpan.Zero,
            backdateLastModified: false);   // fresh LastModifiedDate — inside the grace window

        await SeedWaitAsync(runId, "start", WorkflowWaitStatuses.Resolved);   // zero pending, but young

        var summary = await ReconcileAsync();

        summary.RedispatchedFromStrandedSuspended.ShouldBe(0,
            "a Suspended run with zero pending waits but a FRESH LastModifiedDate is mid-resume — the grace " +
            "window must protect it so we don't race the concurrent Suspended→Pending flip");
        (await ReadStatusAsync(runId)).ShouldBe(WorkflowRunStatus.Suspended,
            "within the grace window the run stays Suspended — the resume's own flip will drive it momentarily");
    }

    // ─── Helpers ──────────────────────────────────────────────────────────────────

    private async Task RunEngineAsync(Guid runId)
    {
        using var scope = _fixture.BeginScope();
        await scope.Resolve<CodeSpace.Core.Services.Workflows.Engine.IWorkflowEngine>().ExecuteRunAsync(runId, CancellationToken.None);
    }

    private async Task PreRecordNodeCompletedAsync(Guid runId, string nodeId)
    {
        using var scope = _fixture.BeginScope();
        var logger = scope.Resolve<CodeSpace.Core.Services.Workflows.Lifecycle.IRunRecordLogger>();
        var empty = (IReadOnlyDictionary<string, System.Text.Json.JsonElement>)new Dictionary<string, System.Text.Json.JsonElement>();
        await logger.NodeStartedAsync(runId, nodeId, iterationKey: "", empty, empty, CancellationToken.None);
        await logger.NodeCompletedAsync(runId, nodeId, iterationKey: "", empty, routingHints: null, TimeSpan.FromMilliseconds(1), CancellationToken.None);
    }

    /// <summary>
    /// Resolve a REAL parked wait in place (located by its correlation token) WITHOUT going through the resume
    /// service — stamping it Resolved + injecting the resume payload but firing NO Suspended→Pending flip / no
    /// dispatch. This is precisely the orphaned-wait residue of the resume-flip-before-resolve race: the wait
    /// resolved but the run was never re-queued, leaving it stranded Suspended.
    /// </summary>
    private async Task ResolveWaitInPlaceAsync(Guid runId, string token, string payloadJson)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var wait = await db.WorkflowRunWait.SingleAsync(w => w.RunId == runId && w.Token == token);
        wait.Status = WorkflowWaitStatuses.Resolved;
        wait.PayloadJson = payloadJson;
        wait.ResolvedAt = DateTimeOffset.UtcNow;

        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Backdate a run's LastModifiedDate via raw SQL (EF's audit hook would otherwise re-stamp it to now),
    /// so a genuinely-suspended run looks stale to the stranded-Suspended sweep's grace-window check.
    /// </summary>
    private async Task BackdateLastModifiedAsync(Guid runId, TimeSpan ago)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        await db.Database.ExecuteSqlRawAsync(
            "UPDATE workflow_run SET last_modified_date = {0} WHERE id = {1}", DateTimeOffset.UtcNow - ago, runId);
    }

    private async Task SeedWaitAsync(Guid runId, string nodeId, string status)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        db.WorkflowRunWait.Add(new WorkflowRunWait
        {
            Id = Guid.NewGuid(),
            RunId = runId,
            NodeId = nodeId,
            IterationKey = string.Empty,
            WaitKind = WorkflowWaitKinds.Approval,
            Token = Guid.NewGuid().ToString("N"),
            Status = status,
            PayloadJson = status == WorkflowWaitStatuses.Resolved ? "{}" : null,
            CreatedAt = DateTimeOffset.UtcNow,
            ResolvedAt = status == WorkflowWaitStatuses.Resolved ? DateTimeOffset.UtcNow : null,
        });

        await db.SaveChangesAsync();
    }

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
    /// Create a workflow whose body is an N-element (parameterised by the seeded payload) flow.map over a real
    /// SUSPENDING node (<see cref="SuspendProbeNode"/>) — the lightest faithful reuse of the proven map-resume fixtures.
    /// Mirrors <c>MapDurableResumeFlowTests.SuspendingMapDefinition</c>: trigger → map[ms → leaf(suspend probe)]
    /// → terminal. The leaf parks an Action wait on its first pass and, on resume, echoes { item, summary }.
    /// </summary>
    private async Task<Guid> CreateSuspendingMapWorkflowAsync(Guid teamId, Guid userId, string key)
    {
        using var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin);
        var mediator = scope.Resolve<IMediator>();
        return await mediator.Send(new CreateWorkflowCommand
        {
            Name = "reconciler-suspend-" + Guid.NewGuid().ToString("N")[..8],
            Description = null,
            Definition = new CodeSpace.Messages.Dtos.Workflows.WorkflowDefinition
            {
                SchemaVersion = 1,
                Nodes = new List<CodeSpace.Messages.Dtos.Workflows.NodeDefinition>
                {
                    new() { Id = "start", TypeKey = "trigger.manual", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
                    new() { Id = "map", TypeKey = "flow.map", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.Json("""{ "items": "{{trigger.things}}" }""") },
                    new() { Id = "ms", TypeKey = "flow.map_start", ParentId = "map", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
                    new() { Id = "leaf", TypeKey = SuspendProbeNode.Key, ParentId = "map", Config = WorkflowsTestSeed.EmptyJson(),
                            Inputs = WorkflowsTestSeed.Json("""{ "key": "__KEY__", "item": "{{item}}" }""".Replace("__KEY__", key)) },
                    new() { Id = "end", TypeKey = "builtin.terminal", Config = WorkflowsTestSeed.EmptyJson(),
                            Inputs = WorkflowsTestSeed.Json("""{ "count": "{{nodes.map.outputs.count}}" }""") },
                },
                Edges = new List<CodeSpace.Messages.Dtos.Workflows.EdgeDefinition>
                {
                    new() { From = "start", To = "map" },
                    new() { From = "map", To = "end" },
                    new() { From = "ms", To = "leaf" },
                },
            },
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
            SourceType = WorkflowRunSourceTypes.Manual,
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

    private async Task<bool> ContinueAsync(Guid runId, Guid teamId)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<CodeSpace.Core.Services.Workflows.IWorkflowService>().ContinueRunAsync(runId, teamId, CancellationToken.None);
    }
}
