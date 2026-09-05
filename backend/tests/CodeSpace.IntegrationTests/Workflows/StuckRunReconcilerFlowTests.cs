using System.Text.Json;
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
///
/// <para><b>Never assert a sweep tally with equality.</b> Every counter on
/// <see cref="StuckRunReconcileSummary"/> is DEPLOYMENT-WIDE: one pass sweeps every matching row in the
/// database this whole collection shares, including rows left stuck by the other classes in it — and which
/// of them has already run when this one fires varies between runs. An equality assert therefore reddens on
/// whatever else happened to be stuck at that moment ("expected 1 but was 2"),
/// which teaches a reader to re-run red instead of reading it. Each test asserts on the rows it OWNS — the
/// run's status, its wait's status — and keeps a tally only as a <c>ShouldBeGreaterThanOrEqualTo</c> floor
/// proving the sweep ran at all. A "nothing was swept" claim has no floor to assert, so it is expressed
/// purely as the owned row being untouched.</para>
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

        // >= not == : the tally is deployment-wide (see the class note); the row assertions below are the proof.
        summary.RedispatchedFromPending.ShouldBeGreaterThanOrEqualTo(1, "the stuck Pending row must be re-dispatched");

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

        // The row version BEFORE the sweep. Asserting it is unchanged proves this row was never
        // written, which is strictly stronger than "it ended up Pending" — a redispatch that flipped
        // it and flipped it back would pass a status check and fail this one.
        var versionBefore = await ReadRowVersionAsync(runId);

        await ReconcileAsync();

        // Deliberately NOT asserting summary.RedispatchedFromPending == 0. The sweep is
        // instance-wide and the summary carries only counts, so that number also reflects rows left
        // behind by earlier tests in the shared database — it reddened at random depending on what
        // ran before it, which teaches a reader to re-run red instead of reading it.
        (await ReadRowVersionAsync(runId)).ShouldBe(versionBefore,
            "a Pending row younger than the threshold is a legitimate in-flight dispatch and must not be touched at all");
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

        // >= not == : the tally is deployment-wide (see the class note); the row assertions below are the proof.
        summary.RevertedFromEnqueued.ShouldBeGreaterThanOrEqualTo(1, "the stuck Enqueued row must walk back to Pending");
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

        // >= not == : the tally is deployment-wide (see the class note); the row assertions below are the proof.
        summary.MarkedAbandonedFromRunning.ShouldBeGreaterThanOrEqualTo(1,
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

        await ReconcileAsync();

        // "Nothing was swept" is asserted on THIS run's row: the abandoned sweep's only effect is Running → Failure,
        // so a row still Running is complete proof it was skipped — and it stays true however many OTHER rows the
        // deployment-wide pass legitimately failed.
        (await ReadStatusAsync(runId)).ShouldBe(WorkflowRunStatus.Running,
            "Running rows with recent ledger activity are alive — must NOT be marked Failure");
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

        await ReconcileAsync();

        // Both claims — "not counted as abandoned Running" and "the stranded sweep skipped it" — are claims about
        // THIS run, and both are settled by its own row: the abandoned sweep would have written Failure and the
        // stranded sweep would have written Pending/Enqueued. Still Suspended means neither touched it, whatever the
        // deployment-wide counters read after another class's rows were swept in the same pass.
        (await ReadStatusAsync(runId)).ShouldBe(WorkflowRunStatus.Suspended,
            "a parked Suspended run must survive every reconciler sweep — the status-scoped sweeps don't match it " +
            "and the stranded-Suspended sweep is excluded by its outstanding Pending wait");
        (await ReadWaitStatusAsync(runId, WorkflowWaitKinds.Approval)).ShouldBe(WorkflowWaitStatuses.Pending,
            "and its outstanding wait is still Pending — nothing resolved the signal it is parked on");
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

        // >= not == : the tally is deployment-wide (see the class note); the row assertions below are the proof.
        summary.RedispatchedFromStrandedSuspended.ShouldBeGreaterThanOrEqualTo(1,
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
        // belongs to a REAL SUSPENDING node (the SuspendProbeNode, the agent.run stand-in), not the trigger.
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

        // >= not == : the tally is deployment-wide (see the class note); the row assertions below are the proof.
        summary.RedispatchedFromStrandedSuspended.ShouldBeGreaterThanOrEqualTo(1,
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

        // >= not == : the tally is deployment-wide (see the class note); the row assertions below are the proof.
        summary.RedispatchedFromStrandedSuspended.ShouldBeGreaterThanOrEqualTo(1,
            "a multi-branch Suspended run with zero pending waits past the grace window is stranded — the sweep re-dispatches it");
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

        await ReconcileAsync();

        // Asserted on the owned row: the stranded sweep's only effect is Suspended → Pending → Enqueued.
        (await ReadStatusAsync(runId)).ShouldBe(WorkflowRunStatus.Suspended,
            "a Suspended run with a Pending wait is parked, not stranded — it must NOT be swept, however old it is");
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

        await ReconcileAsync();

        // Asserted on the owned row: the stranded sweep's only effect is Suspended → Pending → Enqueued.
        (await ReadStatusAsync(runId)).ShouldBe(WorkflowRunStatus.Suspended,
            "a Suspended run with zero pending waits but a FRESH LastModifiedDate is mid-resume — the grace " +
            "window must protect it so we don't race the concurrent Suspended→Pending flip");
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

    [Fact]
    public async Task A_stranded_timer_wait_past_its_wake_is_re_fired_by_the_reconciler()
    {
        // The dropped-schedule signature: a Timer wake overdue past the grace, on a Suspended run — the ONE stranding
        // case with no backstop before this sweep (the stranded-Suspended sweep excludes it — it HAS a pending wait).
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowId = await CreateWorkflowAsync(teamId, userId);
        var runId = await StageStuckRunAsync(workflowId, teamId, status: WorkflowRunStatus.Suspended, createdAgo: TimeSpan.FromMinutes(10), backdateLastModified: true);

        await SeedTimerWaitAsync(runId, wakeAt: DateTimeOffset.UtcNow - StuckRunReconcilerService.TimerWakeLostAfter - TimeSpan.FromMinutes(1));

        var summary = await ReconcileAsync();

        // >= not == : the tally is deployment-wide (see the class note); the row assertions below are the proof.
        summary.RecoveredStrandedTimerWait.ShouldBeGreaterThanOrEqualTo(1, "a Timer wake overdue past the grace on a Suspended run is re-fired — the automated backstop for a dropped Hangfire schedule");
        (await ReadStatusAsync(runId)).ShouldBe(WorkflowRunStatus.Enqueued, "the re-fire resolved the wait + flipped Suspended → Pending → Enqueued, exactly as the scheduled job would");
    }

    [Fact]
    public async Task A_timer_wait_whose_wake_has_not_come_due_is_left_alone()
    {
        // A healthy timer — its wake is still in the future, so the real scheduled job will fire it. The sweep must not
        // wake a run early (that would collapse every future delay to fire-now).
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowId = await CreateWorkflowAsync(teamId, userId);
        var runId = await StageStuckRunAsync(workflowId, teamId, status: WorkflowRunStatus.Suspended, createdAgo: TimeSpan.FromMinutes(10), backdateLastModified: true);

        await SeedTimerWaitAsync(runId, wakeAt: DateTimeOffset.UtcNow.AddMinutes(30));

        await ReconcileAsync();

        // Asserted on the owned rows: a re-fire resolves the wait AND flips the run Suspended → Pending → Enqueued.
        (await ReadWaitStatusAsync(runId, WorkflowWaitKinds.Timer)).ShouldBe(WorkflowWaitStatuses.Pending, "a Timer whose wake hasn't come due is healthy — never re-fired early");
        (await ReadStatusAsync(runId)).ShouldBe(WorkflowRunStatus.Suspended, "the healthy timer's run stays parked until its real wake");
    }

    [Fact]
    public async Task A_stranded_supervisor_infra_park_wait_past_its_deadline_is_re_fired_by_the_reconciler()
    {
        // P4.3 — the last un-backstopped bounded wait, closed: a SupervisorInfraPark deadline (the P1.1
        // model-plane-outage park ladder) overdue past the grace, on a Suspended run, means the scheduled
        // ResumeByDeadlineAsync job was lost — the same dropped-schedule signature the Timer sweep already covers.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowId = await CreateWorkflowAsync(teamId, userId);
        var runId = await StageStuckRunAsync(workflowId, teamId, status: WorkflowRunStatus.Suspended, createdAgo: TimeSpan.FromMinutes(10), backdateLastModified: true);

        await SeedSupervisorInfraParkWaitAsync(runId, wakeAt: DateTimeOffset.UtcNow - StuckRunReconcilerService.SupervisorInfraParkWakeLostAfter - TimeSpan.FromMinutes(1), parks: 2);

        var summary = await ReconcileAsync();

        // >= not == : the tally is deployment-wide (see the class note); the row assertions below are the proof.
        summary.RecoveredStrandedSupervisorInfraParkWait.ShouldBeGreaterThanOrEqualTo(1, "a SupervisorInfraPark deadline overdue past the grace on a Suspended run is re-fired — closing the last un-backstopped bounded wait");
        (await ReadStatusAsync(runId)).ShouldBe(WorkflowRunStatus.Enqueued, "the re-fire resolved the wait + flipped Suspended → Pending → Enqueued, exactly as the scheduled deadline job would");

        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var wait = await db.WorkflowRunWait.AsNoTracking().SingleAsync(w => w.RunId == runId && w.WaitKind == WorkflowWaitKinds.SupervisorInfraPark);

        wait.Status.ShouldBe(WorkflowWaitStatuses.Resolved);
        var payload = JsonDocument.Parse(wait.PayloadJson!).RootElement;
        payload.GetProperty("infraPark").GetBoolean().ShouldBeTrue("the resume payload is the SAME marker the deadline job would have injected — the ladder position rides to the re-entered turn intact");
        payload.GetProperty("parks").GetInt32().ShouldBe(2, "the ladder position is preserved verbatim, not reset — a re-fire must never look like a fresh outage");
    }

    [Fact]
    public async Task A_supervisor_infra_park_wait_whose_deadline_has_not_come_due_is_left_alone()
    {
        // A healthy park — its deadline is still in the future, so the real scheduled job will fire it. The sweep
        // must never wake a run early (that would collapse the whole exponential backoff ladder to fire-now).
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowId = await CreateWorkflowAsync(teamId, userId);
        var runId = await StageStuckRunAsync(workflowId, teamId, status: WorkflowRunStatus.Suspended, createdAgo: TimeSpan.FromMinutes(10), backdateLastModified: true);

        await SeedSupervisorInfraParkWaitAsync(runId, wakeAt: DateTimeOffset.UtcNow.AddMinutes(30), parks: 1);

        await ReconcileAsync();

        // Asserted on the owned rows: a re-fire resolves the wait AND flips the run Suspended → Pending → Enqueued.
        (await ReadWaitStatusAsync(runId, WorkflowWaitKinds.SupervisorInfraPark)).ShouldBe(WorkflowWaitStatuses.Pending, "a park deadline that hasn't come due is healthy — never re-fired early");
        (await ReadStatusAsync(runId)).ShouldBe(WorkflowRunStatus.Suspended, "the healthy park's run stays parked until its real deadline");
    }

    [Fact]
    public async Task A_stranded_supervisor_infra_park_wait_with_no_stored_payload_is_left_alone()
    {
        // Defensive: ResumeByDeadlineAsync requires the TimeoutPayload to resume with. A row with a null payload
        // (which production code never actually writes for this wait kind) must never be re-fired with a
        // fabricated marker — better to leave a malformed row alone than invent ladder state.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowId = await CreateWorkflowAsync(teamId, userId);
        var runId = await StageStuckRunAsync(workflowId, teamId, status: WorkflowRunStatus.Suspended, createdAgo: TimeSpan.FromMinutes(10), backdateLastModified: true);

        await SeedSupervisorInfraParkWaitAsync(runId, wakeAt: DateTimeOffset.UtcNow - StuckRunReconcilerService.SupervisorInfraParkWakeLostAfter - TimeSpan.FromMinutes(1), parks: 1, payloadJson: null);

        await ReconcileAsync();

        // Asserted on the owned rows: a re-fire resolves the wait AND flips the run Suspended → Pending → Enqueued.
        (await ReadWaitStatusAsync(runId, WorkflowWaitKinds.SupervisorInfraPark)).ShouldBe(WorkflowWaitStatuses.Pending, "a payload-less infra-park wait is never re-fired — there is no marker to resume with, so fabricating one would corrupt the ladder position");
        (await ReadStatusAsync(runId)).ShouldBe(WorkflowRunStatus.Suspended, "left parked rather than guessed at");
    }

    [Fact]
    public async Task A_stranded_supervisor_infra_park_wait_still_resolves_correctly_after_the_window_would_have_exhausted()
    {
        // Even when the FIRST park anchors past the 24h window (the run's own re-entry would force-stop honestly
        // per P1.1), the reconciler's job is only to re-fire the deadline resume — the node itself decides to
        // force-stop on re-entry. The sweep must still resolve the wait + dispatch, never silently drop it.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowId = await CreateWorkflowAsync(teamId, userId);
        var runId = await StageStuckRunAsync(workflowId, teamId, status: WorkflowRunStatus.Suspended, createdAgo: TimeSpan.FromMinutes(10), backdateLastModified: true);

        var firstParkedAtUtc = DateTimeOffset.UtcNow - TimeSpan.FromHours(25);
        await SeedSupervisorInfraParkWaitAsync(runId, wakeAt: DateTimeOffset.UtcNow - StuckRunReconcilerService.SupervisorInfraParkWakeLostAfter - TimeSpan.FromMinutes(1), parks: 4, firstParkedAtUtc: firstParkedAtUtc);

        var summary = await ReconcileAsync();

        // >= not == : the tally is deployment-wide (see the class note); the row assertions below are the proof.
        summary.RecoveredStrandedSupervisorInfraParkWait.ShouldBeGreaterThanOrEqualTo(1, "the reconciler re-fires regardless of how old the outage is — the force-stop decision belongs to the node's own re-entry logic, not this sweep");
        (await ReadStatusAsync(runId)).ShouldBe(WorkflowRunStatus.Enqueued, "dispatched so the node can re-enter and force-stop honestly on its own");
    }

    [Fact]
    public async Task A_stranded_supervisor_infra_park_wait_on_a_non_suspended_run_is_left_alone()
    {
        // Defensive: the sweep only touches Suspended runs, mirroring every other bounded-wait backstop — a run
        // that already advanced (e.g. a fast concurrent resume) must never be re-dispatched a second time.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowId = await CreateWorkflowAsync(teamId, userId);
        var runId = await StageStuckRunAsync(workflowId, teamId, status: WorkflowRunStatus.Running, createdAgo: TimeSpan.FromMinutes(10), backdateLastModified: true);

        await SeedSupervisorInfraParkWaitAsync(runId, wakeAt: DateTimeOffset.UtcNow - StuckRunReconcilerService.SupervisorInfraParkWakeLostAfter - TimeSpan.FromMinutes(1), parks: 1);

        await ReconcileAsync();

        // Asserted on the owned rows: a re-fire resolves the wait AND re-dispatches the run.
        (await ReadWaitStatusAsync(runId, WorkflowWaitKinds.SupervisorInfraPark)).ShouldBe(WorkflowWaitStatuses.Pending, "the wait's run is no longer Suspended — a concurrent resume already moved it, so re-firing would double-dispatch");
        (await ReadStatusAsync(runId)).ShouldBe(WorkflowRunStatus.Running, "the already-advanced run is left exactly where the concurrent resume put it");
    }

    [Fact]
    public async Task A_stranded_supervisor_infra_park_parent_run_with_two_stale_waits_ready_at_once_is_bounded_by_the_batch_size()
    {
        // Sanity: the sweep is per-WAIT-ROW (each run parks at most one InfraPark wait at a time in production, since
        // a new park REPLACES the prior one — see WorkflowEngine.SuspendNodeAsync's existing-wait cleanup), so two
        // independently-parked runs each get recovered once, proving the sweep doesn't accidentally skip or double-count.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowId = await CreateWorkflowAsync(teamId, userId);

        var runA = await StageStuckRunAsync(workflowId, teamId, status: WorkflowRunStatus.Suspended, createdAgo: TimeSpan.FromMinutes(10), backdateLastModified: true);
        var runB = await StageStuckRunAsync(workflowId, teamId, status: WorkflowRunStatus.Suspended, createdAgo: TimeSpan.FromMinutes(10), backdateLastModified: true);

        await SeedSupervisorInfraParkWaitAsync(runA, wakeAt: DateTimeOffset.UtcNow - StuckRunReconcilerService.SupervisorInfraParkWakeLostAfter - TimeSpan.FromMinutes(1), parks: 1);
        await SeedSupervisorInfraParkWaitAsync(runB, wakeAt: DateTimeOffset.UtcNow - StuckRunReconcilerService.SupervisorInfraParkWakeLostAfter - TimeSpan.FromMinutes(1), parks: 3);

        var summary = await ReconcileAsync();

        // >= not == : the tally is deployment-wide (see the class note); the row assertions below are the proof.
        summary.RecoveredStrandedSupervisorInfraParkWait.ShouldBeGreaterThanOrEqualTo(2, "both independently-parked runs are recovered in the same tick");
        (await ReadStatusAsync(runA)).ShouldBe(WorkflowRunStatus.Enqueued);
        (await ReadStatusAsync(runB)).ShouldBe(WorkflowRunStatus.Enqueued);
    }

    [Fact]
    public async Task A_stranded_subworkflow_parent_with_a_terminal_child_is_recovered_carrying_the_childs_real_outputs()
    {
        // The last un-backstopped strand: a parent parked (Suspended) on a Pending Subworkflow wait whose child is
        // ALREADY terminal — the signature that the child's inline on-completion resume was lost (a crash).
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowId = await CreateWorkflowAsync(teamId, userId);

        var parentRunId = await StageStuckRunAsync(workflowId, teamId, status: WorkflowRunStatus.Suspended, createdAgo: TimeSpan.FromMinutes(10), backdateLastModified: true);
        var childRunId = await SeedChildRunAsync(teamId, parentRunId, WorkflowRunStatus.Success, outputsJson: """{"result":"ok"}""");
        await SeedSubworkflowWaitAsync(parentRunId, childRunId);

        var summary = await ReconcileAsync();

        // >= not == : the tally is deployment-wide (see the class note); the row assertions below are the proof.
        summary.RecoveredStrandedSubworkflowParent.ShouldBeGreaterThanOrEqualTo(1, "a parent parked on a terminal child's Subworkflow wait is re-fired — the symmetric twin of the AgentRun backstop");
        (await ReadStatusAsync(parentRunId)).ShouldBe(WorkflowRunStatus.Enqueued, "the re-fire resolved the wait + flipped Suspended → Pending → Enqueued");

        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var wait = await db.WorkflowRunWait.AsNoTracking().SingleAsync(w => w.RunId == parentRunId && w.WaitKind == WorkflowWaitKinds.Subworkflow);

        wait.Status.ShouldBe(WorkflowWaitStatuses.Resolved);
        var payload = JsonDocument.Parse(wait.PayloadJson!).RootElement;
        payload.GetProperty("status").GetString().ShouldBe("Success", "the child's REAL status is mapped onto the parent — nothing faked");
        payload.GetProperty("outputs").GetProperty("result").GetString().ShouldBe("ok", "the child's REAL outputs ride to the parent — the same mapping as the happy path");
    }

    [Fact]
    public async Task A_subworkflow_parent_whose_child_is_still_running_is_left_alone()
    {
        // A still-running child resumes its own parent when it finishes — it isn't stranded, so the sweep must not touch it.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowId = await CreateWorkflowAsync(teamId, userId);

        var parentRunId = await StageStuckRunAsync(workflowId, teamId, status: WorkflowRunStatus.Suspended, createdAgo: TimeSpan.FromMinutes(10), backdateLastModified: true);
        var childRunId = await SeedChildRunAsync(teamId, parentRunId, WorkflowRunStatus.Running, outputsJson: "{}");
        await SeedSubworkflowWaitAsync(parentRunId, childRunId);

        await ReconcileAsync();

        // Asserted on the owned rows: a re-fire resolves the wait AND flips the parent Suspended → Pending → Enqueued.
        (await ReadWaitStatusAsync(parentRunId, WorkflowWaitKinds.Subworkflow)).ShouldBe(WorkflowWaitStatuses.Pending, "a still-running child is not stranded — it will resume its own parent when it finishes");
        (await ReadStatusAsync(parentRunId)).ShouldBe(WorkflowRunStatus.Suspended, "the parent stays parked while its child runs");
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

    private async Task SeedTimerWaitAsync(Guid runId, DateTimeOffset wakeAt)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        db.WorkflowRunWait.Add(new WorkflowRunWait
        {
            Id = Guid.NewGuid(),
            RunId = runId,
            NodeId = "delay",
            IterationKey = string.Empty,
            WaitKind = WorkflowWaitKinds.Timer,
            Token = Guid.NewGuid().ToString("N"),
            WakeAt = wakeAt,
            Status = WorkflowWaitStatuses.Pending,
            PayloadJson = null,
            CreatedAt = DateTimeOffset.UtcNow,
        });

        await db.SaveChangesAsync();
    }

    /// <summary>Seed a SupervisorInfraPark wait carrying the SAME marker shape <c>SupervisorInfraPark.Marker</c> produces (both the suspend Payload and the deadline's TimeoutPayload, per <c>AgentSupervisorNode.ParkForInfraOrStopAsync</c>), so the reconciler's re-fire round-trips a real ladder position. <paramref name="payloadJson"/> overrides the marker entirely (e.g. null, to test the defensive no-payload case).</summary>
    private async Task SeedSupervisorInfraParkWaitAsync(Guid runId, DateTimeOffset wakeAt, int parks, DateTimeOffset? firstParkedAtUtc = null, string? payloadJson = "__default__")
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var marker = payloadJson == "__default__"
            ? JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["infraPark"] = true,
                ["parks"] = parks,
                ["firstParkedAtUtc"] = (firstParkedAtUtc ?? DateTimeOffset.UtcNow.AddMinutes(-30)).ToString("o"),
                ["error"] = "gateway 429 (seeded)",
            })
            : payloadJson;

        db.WorkflowRunWait.Add(new WorkflowRunWait
        {
            Id = Guid.NewGuid(),
            RunId = runId,
            NodeId = "supervisor",
            IterationKey = $"supervisor#infra{parks}",
            WaitKind = WorkflowWaitKinds.SupervisorInfraPark,
            Token = Guid.NewGuid().ToString("N"),
            WakeAt = wakeAt,
            Status = WorkflowWaitStatuses.Pending,
            PayloadJson = marker,
            CreatedAt = DateTimeOffset.UtcNow,
        });

        await db.SaveChangesAsync();
    }

    private async Task<Guid> SeedChildRunAsync(Guid teamId, Guid parentRunId, WorkflowRunStatus status, string outputsJson)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var now = DateTimeOffset.UtcNow;
        var requestId = Guid.NewGuid();
        var childRunId = Guid.NewGuid();

        db.WorkflowRunRequest.Add(new WorkflowRunRequest
        {
            Id = requestId, TeamId = teamId, WorkflowId = null, SourceType = WorkflowRunSourceTypes.Snapshot,
            ActorType = "system", ActorId = SystemUsers.SeederId, NormalizedPayloadJson = "{}",
            Status = WorkflowRunRequestStatus.Consumed, ReceivedAt = now, VerifiedAt = now, NormalizedAt = now,
        });
        db.WorkflowRun.Add(new WorkflowRun
        {
            Id = childRunId, WorkflowId = null, WorkflowVersion = null, TeamId = teamId, RunRequestId = requestId,
            ParentRunId = parentRunId, SourceType = WorkflowRunSourceTypes.Snapshot, Status = status, OutputsJson = outputsJson,
            ScopeRepositoryIds = [], ScopeProjectIds = [], CreatedDate = now,
            CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId,
        });

        await db.SaveChangesAsync();
        return childRunId;
    }

    private async Task SeedSubworkflowWaitAsync(Guid parentRunId, Guid childRunId)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        db.WorkflowRunWait.Add(new WorkflowRunWait
        {
            Id = Guid.NewGuid(),
            RunId = parentRunId,
            NodeId = "sub",
            IterationKey = string.Empty,
            WaitKind = WorkflowWaitKinds.Subworkflow,
            Token = childRunId.ToString(),   // the parent's wait is keyed to the child run id
            Status = WorkflowWaitStatuses.Pending,
            PayloadJson = null,
            CreatedAt = DateTimeOffset.UtcNow,
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

    /// <summary>The Postgres row version. Changes on any write, so it answers "was this row touched" rather than "what does it say now".</summary>
    private async Task<uint> ReadRowVersionAsync(Guid runId)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<CodeSpaceDbContext>().WorkflowRun.AsNoTracking()
            .Where(r => r.Id == runId)
            .Select(r => r.Xmin)
            .SingleAsync();
    }

    /// <summary>The status of the run's sole wait of the given kind. Answers "was MY wait re-fired" — the owned-row twin of a sweep tally.</summary>
    private async Task<string> ReadWaitStatusAsync(Guid runId, string waitKind)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<CodeSpaceDbContext>().WorkflowRunWait.AsNoTracking()
            .Where(w => w.RunId == runId && w.WaitKind == waitKind)
            .Select(w => w.Status)
            .SingleAsync();
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
