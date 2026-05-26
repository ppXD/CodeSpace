using System.Data.Common;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Services.Workflows.Dispatch;
using CodeSpace.Core.Services.Workflows.Engine;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Infrastructure.Jobs;
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
/// Integration-tier proof of the no-double-execution guarantee.
///
/// <para>The status machine relies on two atomic CAS operations to guarantee that no run
/// executes twice:</para>
///
/// <list type="number">
///   <item><see cref="WorkflowRunDispatcher.DispatchAsync"/> performs
///   <c>UPDATE workflow_run SET status='Enqueued' WHERE id=$1 AND status='Pending'</c>.
///   Postgres's row-level lock + the WHERE clause make this single-writer: two callers
///   racing yield exactly one rows-affected = 1 and one = 0.</item>
///   <item><see cref="WorkflowEngine.ExecuteRunAsync"/> performs
///   <c>UPDATE workflow_run SET status='Running' WHERE id=$1 AND status='Enqueued'</c> at
///   entry. Same single-writer guarantee — two workers racing yield exactly one claim and
///   one short-circuit.</item>
/// </list>
///
/// <para>These tests force the race conditions and prove the invariants hold:</para>
/// <list type="bullet">
///   <item><c>Concurrent_dispatchers</c>: 5 parallel <c>DispatchAsync</c> calls on the same
///   runId → exactly 1 returns true + exactly 1 Hangfire Enqueue is recorded</item>
///   <item><c>Concurrent_engine_workers</c>: 5 parallel <c>ExecuteRunAsync</c> calls on the
///   same runId → exactly 1 worker drives the run to terminal + the rest silent no-op</item>
///   <item><c>Dispatch_revert_on_enqueue_throw</c>: simulates Hangfire failure → status walks
///   back to Pending so the reconciler picks it up</item>
///   <item><c>Reentry_on_terminal_status_is_silent_noop</c>: re-entry on Success/Failure runs
///   doesn't mutate the row or emit ledger records</item>
///   <item><c>Reentry_on_pending_is_silent_noop</c>: engine called on a Pending (never-
///   dispatched) row short-circuits — only the dispatcher's CAS legitimately lifts to
///   Enqueued. Defends against bypass attempts where someone calls the engine directly
///   without dispatching first.</item>
///   <item><c>High_load_stress</c>: 10 distinct runs × 8 dispatchers each gated by a
///   <see cref="SemaphoreSlim"/> "starting gun" — 80 simultaneous CAS attempts. Probes
///   for race-window holes that a 5-way race doesn't surface on fast machines. (Sized to
///   fit within Postgres <c>max_connections=100</c> default with xunit parallel-collection
///   headroom; see the test body for the sizing rationale.)</item>
/// </list>
///
/// <para><b>DB-portability note</b>: the CAS contract is satisfied by any SQL store that
/// provides row-level lock + atomic <c>UPDATE ... WHERE</c>. The loser of a CAS race
/// manifests differently across engines though — Postgres READ COMMITTED returns
/// rows-affected=0 silently, SERIALIZABLE / SQL Server SNAPSHOT / CockroachDB throw a
/// serialization-failure exception. These tests treat <see cref="DbException"/> on the
/// loser as a legitimate outcome (catch-and-count-as-loser) so they pass on every
/// conforming engine, not just Postgres-with-READ-COMMITTED.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
public class NoDoubleExecutionFlowTests
{
    private readonly PostgresFixture _fixture;

    public NoDoubleExecutionFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task Concurrent_dispatchers_on_same_run_produce_exactly_one_winner_and_one_enqueue()
    {
        // Setup: a fresh run in Pending state with the in-memory background-job client
        // recording every Enqueue call. The "5 parallel dispatchers" models the race the user
        // most cares about: a reconciler tick + a manual user-action both calling DispatchAsync
        // on the same runId at the same moment.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowId = await CreateWorkflowAsync(teamId, userId);
        var runId = await StageRunInPendingAsync(workflowId, teamId);
        var jobClient = ResolveJobClient();
        jobClient.Clear();

        // Fire N concurrent DispatchAsync calls on the same runId. Each gets its own scope
        // (separate DbContext, separate dispatcher instance) to mirror multi-replica reality —
        // worker A and worker B don't share a connection.
        const int concurrency = 5;
        var results = await Task.WhenAll(Enumerable.Range(0, concurrency).Select(_ => DispatchOnceAsync(runId)));

        // Exactly one CAS succeeded. The Postgres row-level lock + WHERE status=Pending
        // clause guarantees this: even with 5 connections racing, at most one UPDATE affects
        // 1 row; the others see status != Pending after the winner's commit and affect 0.
        //
        // Why DispatchOnceAsync swallows DbException as "false" rather than letting it bubble:
        // CAS losers can manifest TWO ways depending on the engine + isolation level —
        // rows-affected=0 (Postgres READ COMMITTED, the default) OR a serialization-failure
        // exception (SQL Server SNAPSHOT, Postgres SERIALIZABLE, Cockroach). Both are valid
        // "you lost the race" outcomes; both must count toward `success=false` so this test
        // stays meaningful across DB engines.
        results.Count(success => success).ShouldBe(1,
            "exactly ONE dispatcher must win the Pending → Enqueued CAS. " +
            "Any other outcome (0 winners = lost row, 2+ winners = double-execution) violates the invariant");

        // The losers MUST NOT have called Enqueue — they bailed before reaching it.
        jobClient.Calls.Count(c => c.RunId == runId).ShouldBe(1,
            "exactly ONE background-job enqueue must occur — two enqueues = two Hangfire jobs = double-execution");

        // Final row state: Enqueued (the winner transitioned, no one else touched it).
        var finalStatus = await ReadStatusAsync(runId);
        finalStatus.ShouldBe(WorkflowRunStatus.Enqueued);
    }

    [Fact]
    public async Task Concurrent_engine_workers_on_same_enqueued_run_produce_exactly_one_executor()
    {
        // The dispatcher's CAS prevents two Enqueue calls. Now prove the engine's CAS prevents
        // two ExecuteRunAsync calls (the Hangfire-side risk: a retry plus a reconciler's
        // re-enqueue handing the same runId to two workers).
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowId = await CreateWorkflowAsync(teamId, userId, WorkflowsTestSeed.MinimalDefinition());
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);

        // Fire N concurrent ExecuteRunAsync calls. Each gets its own scope. The engine's
        // entry CAS Enqueued → Running must single-thread these even when they overlap.
        const int concurrency = 5;
        await Task.WhenAll(Enumerable.Range(0, concurrency).Select(_ => ExecuteEngineAsync(runId)));

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();
        var finalRun = await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId);

        finalRun.Status.ShouldBe(WorkflowRunStatus.Success,
            "exactly ONE engine instance must drive the run to terminal; the rest must silently short-circuit. " +
            "If the run finishes Success the executor reached the terminal node exactly once");

        // The minimal definition produces exactly one node.completed per node (start + end).
        // Two executors would double the count. Three would triple it. We prove "exactly one
        // executor" by counting node.completed records and asserting the count matches the
        // single-run expected value.
        var nodeCompletedCount = await db.WorkflowRunRecord.AsNoTracking()
            .CountAsync(r => r.RunId == runId && r.RecordType == WorkflowRunRecordTypes.NodeCompleted);
        nodeCompletedCount.ShouldBe(2,
            "minimal-definition (trigger → terminal) emits exactly 2 node.completed records. " +
            $"Found {nodeCompletedCount}. Anything > 2 means the engine ran the graph more than once — " +
            "side-effecting nodes would have fired multiple times in production. THIS is the no-double-execution proof");

        // The run.started ledger record is emitted ONCE per executor entry. There must be
        // exactly 1, not N — proves only the CAS winner reached run.started.
        var runStartedCount = await db.WorkflowRunRecord.AsNoTracking()
            .CountAsync(r => r.RunId == runId && r.RecordType == WorkflowRunRecordTypes.RunStarted);
        runStartedCount.ShouldBe(1,
            "run.started must be emitted exactly once. > 1 means multiple workers passed the entry CAS");
    }

    [Fact]
    public async Task Dispatch_revert_on_enqueue_throw_walks_status_back_to_pending()
    {
        // The third pillar of the design: if Hangfire's Enqueue throws after the CAS already
        // moved the row Enqueued, the dispatcher MUST revert Pending → Enqueued → Pending so
        // the reconciler picks it up. Otherwise an orphaned Enqueued row blocks the row forever.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowId = await CreateWorkflowAsync(teamId, userId);
        var runId = await StageRunInPendingAsync(workflowId, teamId);

        var jobClient = ResolveJobClient();
        jobClient.Clear();
        jobClient.ThrowOnEnqueue = new InvalidOperationException("Simulated Hangfire storage outage");

        await Should.ThrowAsync<InvalidOperationException>(() => DispatchOnceAsync(runId));

        // Status walked back to Pending — the reconciler's "find Pending rows older than N"
        // sweep will find this row and re-dispatch.
        var finalStatus = await ReadStatusAsync(runId);
        finalStatus.ShouldBe(WorkflowRunStatus.Pending,
            "on Enqueue throw the dispatcher MUST revert Pending → Enqueued → Pending; " +
            "leaving the row stuck Enqueued blocks every reconciler tick + every subsequent retry");

        // Sanity: no enqueue actually landed (the throw happened mid-Enqueue, before recording).
        jobClient.Calls.Count(c => c.RunId == runId).ShouldBe(0,
            "the ThrowOnEnqueue path must short-circuit BEFORE the call is recorded");
    }

    [Fact]
    public async Task Reentry_on_terminal_status_is_silent_noop()
    {
        // Engine called on a Success / Failure / Cancelled row must be a no-op. The CAS
        // WHERE clause is status=Enqueued; terminal status != Enqueued so rows-affected = 0
        // and the engine returns without mutating the row OR emitting ledger records.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowId = await CreateWorkflowAsync(teamId, userId, WorkflowsTestSeed.MinimalDefinition());
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);

        // First execution: drives the row to Success.
        await ExecuteEngineAsync(runId);

        using var prep = _fixture.BeginScope();
        var prepDb = prep.Resolve<CodeSpaceDbContext>();
        var preLedgerCount = await prepDb.WorkflowRunRecord.AsNoTracking().CountAsync(r => r.RunId == runId);
        (await prepDb.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId)).Status.ShouldBe(WorkflowRunStatus.Success);

        // Re-invoke engine on the terminal row.
        await ExecuteEngineAsync(runId);

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();
        var finalRun = await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId);

        finalRun.Status.ShouldBe(WorkflowRunStatus.Success,
            "terminal status MUST be preserved on engine re-entry — no rollback, no overwrite");

        var postLedgerCount = await db.WorkflowRunRecord.AsNoTracking().CountAsync(r => r.RunId == runId);
        postLedgerCount.ShouldBe(preLedgerCount,
            "engine re-entry on a terminal row MUST emit NO new ledger records — silent short-circuit");
    }

    [Fact]
    public async Task Reentry_on_pending_status_is_silent_noop_defends_dispatcher_bypass()
    {
        // Defends against the bypass attempt: someone calls engine.ExecuteRunAsync directly
        // on a Pending row WITHOUT going through the dispatcher's CAS. The engine's entry
        // CAS WHERE clause is status=Enqueued, so a Pending row yields rows-affected = 0
        // and the engine refuses to execute. This forces every execution path through the
        // dispatcher — making the dispatcher the single point of "transition to Enqueued"
        // and preserving the single-writer invariant.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowId = await CreateWorkflowAsync(teamId, userId);
        var runId = await StageRunInPendingAsync(workflowId, teamId);

        await ExecuteEngineAsync(runId);

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();
        var finalRun = await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId);

        finalRun.Status.ShouldBe(WorkflowRunStatus.Pending,
            "engine MUST refuse to execute a row stuck in Pending — the dispatcher's CAS is the only legitimate path to Enqueued. " +
            "Allowing a Pending → Running transition would skip the dispatch step entirely, letting Hangfire never be told to retry");
        finalRun.StartedAt.ShouldBeNull("engine MUST NOT set StartedAt on silent short-circuit");

        var ledgerCount = await db.WorkflowRunRecord.AsNoTracking().CountAsync(r => r.RunId == runId);
        // run.queued is emitted by RunStarter (via SeedManualRunAsync's RunRecordLogger call
        // path), so we expect that single record. We don't compare to a pre-state because the
        // helper that staged the run did NOT call run.queued — the test stages the row directly
        // via StageRunInPendingAsync below.
        ledgerCount.ShouldBe(0, "engine re-entry on Pending must not emit any ledger records");
    }

    [Fact]
    public async Task High_load_stress_10_runs_with_8_dispatchers_each_yields_exactly_one_winner_per_run()
    {
        // The 5-way races above can pass on a fast machine even if the CAS has a subtle bug
        // — Task.WhenAll doesn't guarantee true parallelism; the thread-pool scheduler may
        // serialize them. This test scales the contention to where any race-window hole
        // becomes observable: 10 distinct Pending runs × 8 dispatchers each, all gated to
        // fire at the same instant via a SemaphoreSlim starting gun.
        //
        // <b>Sizing rationale:</b> 80 total concurrent CAS attempts. The cap is the Postgres
        // <c>max_connections</c> default of 100 minus headroom for xunit's parallel test
        // collections running against the same PG cluster — pushing past ~80 makes the test
        // flaky with "53300: too many clients". 80 is still 16× the baseline 5-way test,
        // and 8 contenders/row is high enough to exercise the row-lock queue.
        //
        // The per-run invariant is unchanged: each runId has exactly ONE winner. Aggregating
        // 10 such invariants gives a strong signal — any aggregate that's NOT 10 winners
        // means a CAS bug.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowId = await CreateWorkflowAsync(teamId, userId);

        const int runCount = 10;
        const int contendersPerRun = 8;
        var runIds = new Guid[runCount];
        for (int i = 0; i < runCount; i++)
            runIds[i] = await StageRunInPendingAsync(workflowId, teamId);

        // Reset the job client so we count only the calls this test makes.
        var jobClient = ResolveJobClient();
        jobClient.Clear();

        // Starting-gun pattern: spin up all tasks but block them on the semaphore until we
        // release N permits at once. This collapses the "warmup spread" the scheduler would
        // otherwise impose, maximising true overlap.
        var totalContenders = runCount * contendersPerRun;
        using var gate = new SemaphoreSlim(0, totalContenders);

        var contenderTasks = runIds.SelectMany(runId =>
            Enumerable.Range(0, contendersPerRun).Select(async _ =>
            {
                await gate.WaitAsync();
                return (RunId: runId, Won: await DispatchOnceAsync(runId));
            })).ToArray();

        gate.Release(totalContenders);   // open the gate — everyone fires
        var outcomes = await Task.WhenAll(contenderTasks);

        // Aggregate: group by runId, count winners. Every group MUST have winners = 1.
        var winnersPerRun = outcomes
            .GroupBy(o => o.RunId)
            .ToDictionary(g => g.Key, g => g.Count(o => o.Won));

        winnersPerRun.Count.ShouldBe(runCount, "every staged run must appear in the outcomes");
        foreach (var (runId, wins) in winnersPerRun)
            wins.ShouldBe(1,
                $"run {runId}: expected exactly 1 winner among {contendersPerRun} contenders, got {wins}. " +
                "ANY count != 1 means the CAS allowed multiple writers — under load this becomes a " +
                "double-execution in production");

        // Cross-check via the job client: exactly `runCount` Enqueue calls total (one per run).
        jobClient.Calls.Count.ShouldBe(runCount,
            $"total Hangfire enqueues across {runCount} runs must equal {runCount} " +
            $"(one per winning CAS), got {jobClient.Calls.Count}");

        // Final DB state: every staged run row is now Enqueued (the winner transitioned, no
        // one else touched it). We bulk-verify in one query.
        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();
        var statuses = await db.WorkflowRun.AsNoTracking()
            .Where(r => runIds.Contains(r.Id))
            .Select(r => r.Status)
            .ToListAsync();
        statuses.ShouldAllBe(s => s == WorkflowRunStatus.Enqueued,
            "every run row must end up Enqueued; any stuck-Pending row means a winner's commit was lost; " +
            "any other status means a second CAS slipped through");
    }

    // ─── Helpers ──────────────────────────────────────────────────────────────────

    private async Task<Guid> CreateWorkflowAsync(Guid teamId, Guid userId)
        => await CreateWorkflowAsync(teamId, userId, WorkflowsTestSeed.MinimalDefinition());

    private async Task<Guid> CreateWorkflowAsync(Guid teamId, Guid userId, CodeSpace.Messages.Dtos.Workflows.WorkflowDefinition def)
    {
        using var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin);
        var mediator = scope.Resolve<IMediator>();
        return await mediator.Send(new CreateWorkflowCommand
        {
            Name = "no-double-exec-" + Guid.NewGuid().ToString("N")[..8],
            Description = null,
            Definition = def,
            Activations = new List<WorkflowActivationInput>(),
            Enabled = true,
        });
    }

    /// <summary>
    /// Stages a WorkflowRunRequest + WorkflowRun pair directly in <c>Pending</c> status.
    /// Mirrors <see cref="WorkflowsTestSeed.SeedManualRunAsync"/> except the status — the
    /// dispatcher-race tests start from Pending so they exercise the Pending → Enqueued
    /// transition, not the post-dispatch state.
    /// </summary>
    private async Task<Guid> StageRunInPendingAsync(Guid workflowId, Guid teamId)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var requestId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        db.WorkflowRunRequest.Add(new CodeSpace.Core.Persistence.Entities.WorkflowRunRequest
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

        db.WorkflowRun.Add(new CodeSpace.Core.Persistence.Entities.WorkflowRun
        {
            Id = runId,
            WorkflowId = workflowId,
            WorkflowVersion = 1,
            TeamId = teamId,
            RunRequestId = requestId,
            Status = WorkflowRunStatus.Pending,
            CreatedBy = SystemUsers.SeederId,
            LastModifiedBy = SystemUsers.SeederId,
        });

        await db.SaveChangesAsync();
        return runId;
    }

    private async Task<bool> DispatchOnceAsync(Guid runId)
    {
        using var scope = _fixture.BeginScope();
        var dispatcher = scope.Resolve<IWorkflowRunDispatcher>();
        try
        {
            return await dispatcher.DispatchAsync(runId, CancellationToken.None);
        }
        catch (DbException)
        {
            // DB-portability: under stricter isolation (Postgres SERIALIZABLE, SQL Server
            // SNAPSHOT, Cockroach) a CAS loser can surface as a serialization-failure
            // exception instead of rows-affected=0. Both are legitimate "you lost" outcomes —
            // map both to `false` so the "exactly 1 winner" invariant holds across engines.
            // NOTE: this catch is restricted to DbException; other exceptions (logic bugs,
            // misconfiguration) still bubble.
            return false;
        }
        catch (DbUpdateConcurrencyException)
        {
            // Same intent, EF Core-specific wrapper. EF surfaces serialization failures as
            // DbUpdateConcurrencyException when the rows-affected check trips during
            // SaveChanges (we're using ExecuteUpdateAsync so this path is uncommon, but
            // catch it for portability across providers).
            return false;
        }
    }

    private async Task ExecuteEngineAsync(Guid runId)
    {
        using var scope = _fixture.BeginScope();
        await scope.Resolve<IWorkflowEngine>().ExecuteRunAsync(runId, CancellationToken.None);
    }

    private async Task<WorkflowRunStatus> ReadStatusAsync(Guid runId)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<CodeSpaceDbContext>().WorkflowRun.AsNoTracking()
            .Where(r => r.Id == runId)
            .Select(r => r.Status)
            .SingleAsync();
    }

    private InMemoryBackgroundJobClient ResolveJobClient()
    {
        // Resolve the concrete type to access Calls / ThrowOnEnqueue (the interface only
        // exposes Enqueue). The fixture registers it singleton, so this is the same instance
        // every dispatcher resolves via ICodeSpaceBackgroundJobClient.
        using var scope = _fixture.BeginScope();
        return scope.Resolve<InMemoryBackgroundJobClient>();
    }
}
