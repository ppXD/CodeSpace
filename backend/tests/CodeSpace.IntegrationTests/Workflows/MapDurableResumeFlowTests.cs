using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Services.Workflows.Engine;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
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
/// Map-PR2 — durable parallel-branch suspend/resume for the <c>flow.map</c> fan-out, against real Postgres
/// + the real engine + the real <see cref="WorkflowResumeService"/>. This is the headline of the
/// planner+parallel-subagents epic: a map body now contains a node that SUSPENDS (here a hermetic
/// <see cref="SuspendProbeNode"/> that parks a real Action wait — the stand-in for an <c>agent.run</c>
/// that parks to an AgentRun). K subtasks fan out to K parallel branches, each parks under its own
/// iteration key <c>"&lt;mapId&gt;#&lt;i&gt;"</c>, the run stays Suspended until ALL branch waits resolve
/// (the wait-for-all barrier via <see cref="IWorkflowResumeService.ResumeOnWaitCompletionAsync"/> — the
/// exact path the agent + sub-workflow completion notifiers use), then each branch resumes from its own
/// durable wait and the per-element results reduce, ORDERED by element index.
///
/// <para>Mirrors <c>LoopFlowTests.A_suspend_in_a_parallel_loop_body_parks_then_resume_re_runs_only_the_suspended_branch</c>
/// (the proven parallel-suspend-resume template). Pins: (a) one-of-K parks + resumes only that branch +
/// reduces ordered; (b) ALL-K suspend → K waits → Suspended until the LAST resolves → all reduce; (c)
/// exactly-once — a completed branch is NOT re-executed when a SIBLING resolves (the #306 property for
/// map); (d) idempotent double-notify; (e) the planner→map→synthesizer e2e with suspending bodies;
/// (f) covered by <c>MapFlowTests</c> — a synchronous-body map still behaves as PR1.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class MapDurableResumeFlowTests
{
    private readonly PostgresFixture _fixture;

    public MapDurableResumeFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task A_suspend_in_one_of_K_parallel_branches_parks_then_resolving_each_reduces_ordered()
    {
        // (a) Three branches each park their own wait (K=3). Resolving them one at a time leaves the run
        // Suspended until the LAST resolves; then the reduce is ordered by element index, not resume order.
        var key = "sp-" + Guid.NewGuid().ToString("N");
        SuspendProbeNode.Reset(key);

        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowId = await CreateWorkflowAsync(teamId, userId, SuspendingMapDefinition(key));
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId, payloadJson: """{ "things": ["a", "b", "c"] }""");

        await RunEngineAsync(runId);

        using (var parked = _fixture.BeginScope())
        {
            var db = parked.Resolve<CodeSpaceDbContext>();
            (await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId)).Status.ShouldBe(WorkflowRunStatus.Suspended);
            (await db.WorkflowRunWait.AsNoTracking().CountAsync(w => w.RunId == runId && w.Status == WorkflowWaitStatuses.Pending))
                .ShouldBe(3, "K=3 parallel branches park K independent waits, each under '<mapId>#<i>'");
            // Each branch wait is keyed to its own element-branch iteration key.
            var keys = await db.WorkflowRunWait.AsNoTracking().Where(w => w.RunId == runId).Select(w => w.IterationKey).ToListAsync();
            keys.OrderBy(k => k).ShouldBe(new[] { "map#0", "map#1", "map#2" });
        }

        // Resolve branch c (index 2) FIRST, then a (0), then b (1) — out of element order on purpose. The run
        // stays Suspended until the LAST resolves, and the reduce must STILL come out a/b/c by element index.
        (await ResolveBranchAsync(runId, key, "c", "RES-c")).ShouldBeTrue();
        await AssertSuspendedAsync(runId, "two branches still pending after the first resolve");

        (await ResolveBranchAsync(runId, key, "a", "RES-a")).ShouldBeTrue();
        await AssertSuspendedAsync(runId, "one branch still pending after the second resolve");

        (await ResolveBranchAsync(runId, key, "b", "RES-b")).ShouldBeTrue();   // the LAST → re-dispatch
        await RunEngineAsync(runId);

        using var done = _fixture.BeginScope();
        var fdb = done.Resolve<CodeSpaceDbContext>();
        (await fdb.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId)).Status.ShouldBe(WorkflowRunStatus.Success);

        var outputs = JsonDocument.Parse((await MapNodeAsync(fdb, runId)).OutputsJson).RootElement;
        outputs.GetProperty("count").GetInt32().ShouldBe(3);
        var results = outputs.GetProperty("results");
        // results[i] is ordered by ELEMENT index — a/b/c — not the c/a/b resolve order.
        results[0].GetProperty("item").GetString().ShouldBe("a");
        results[1].GetProperty("item").GetString().ShouldBe("b");
        results[2].GetProperty("item").GetString().ShouldBe("c");
        results[0].GetProperty("summary").GetString().ShouldBe("RES-a", "each branch resumed with ITS OWN resolved payload");
        results[1].GetProperty("summary").GetString().ShouldBe("RES-b");
        results[2].GetProperty("summary").GetString().ShouldBe("RES-c");
    }

    [Fact]
    public async Task All_K_branches_suspend_and_the_run_stays_suspended_until_the_last_wait_resolves()
    {
        // (b) The wait-for-all barrier: K=4 branches all park; resolving them via ResumeOnWaitCompletionAsync
        // (the agent/sub-workflow completion path) keeps the run Suspended until the LAST resolves — then it
        // re-dispatches exactly once and all reduce. No partial re-walk advances the run early.
        var key = "sp-" + Guid.NewGuid().ToString("N");
        SuspendProbeNode.Reset(key);

        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowId = await CreateWorkflowAsync(teamId, userId, SuspendingMapDefinition(key));
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId, payloadJson: """{ "things": ["w", "x", "y", "z"] }""");

        await RunEngineAsync(runId);
        await AssertSuspendedAsync(runId, "all four branches parked");

        var elements = new[] { "w", "x", "y", "z" };
        for (var i = 0; i < elements.Length - 1; i++)
        {
            (await ResolveBranchAsync(runId, key, elements[i], $"RES-{elements[i]}")).ShouldBeTrue();
            await AssertSuspendedAsync(runId, $"after resolving {i + 1}/{elements.Length}, siblings still pending → barrier holds");
        }

        // The last completer flips the run Pending and re-dispatches.
        (await ResolveBranchAsync(runId, key, elements[^1], $"RES-{elements[^1]}")).ShouldBeTrue();
        await RunEngineAsync(runId);

        using var done = _fixture.BeginScope();
        var fdb = done.Resolve<CodeSpaceDbContext>();
        (await fdb.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId)).Status.ShouldBe(WorkflowRunStatus.Success);
        var results = JsonDocument.Parse((await MapNodeAsync(fdb, runId)).OutputsJson).RootElement.GetProperty("results");
        results.GetArrayLength().ShouldBe(4);
        for (var i = 0; i < elements.Length; i++)
        {
            results[i].GetProperty("item").GetString().ShouldBe(elements[i]);
            results[i].GetProperty("summary").GetString().ShouldBe($"RES-{elements[i]}");
        }
    }

    [Fact]
    public async Task A_completed_branch_is_not_re_executed_when_a_sibling_resolves()
    {
        // (c) EXACTLY-ONCE (the #306 property for map): two branches park. We resolve branch a via the
        // APPROVAL-style immediate re-dispatch path (ResumeWaitAsync, which re-walks at once), so branch a
        // COMPLETES while branch b re-suspends. Resolving b then triggers a SECOND re-walk — on which the
        // already-completed branch a must NOT re-run its first (suspending) pass. We assert a's first-pass
        // ran exactly once across the whole multi-resume sequence.
        var key = "sp-" + Guid.NewGuid().ToString("N");
        SuspendProbeNode.Reset(key);

        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowId = await CreateWorkflowAsync(teamId, userId, SuspendingMapDefinition(key));
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId, payloadJson: """{ "things": ["a", "b"] }""");

        await RunEngineAsync(runId);
        await AssertSuspendedAsync(runId, "both branches parked");

        // Resolve a via the SINGLE-wait immediate-redispatch path (ResumeWaitAsync re-walks even with a
        // sibling pending), then re-walk: branch a completes its terminal, branch b re-suspends.
        (await ResolveBranchViaResumeWaitAsync(runId, key, "a", "RES-a")).ShouldBeTrue();
        await RunEngineAsync(runId);

        using (var mid = _fixture.BeginScope())
        {
            var db = mid.Resolve<CodeSpaceDbContext>();
            (await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId)).Status
                .ShouldBe(WorkflowRunStatus.Suspended, "branch b is still parked after branch a completed");
            // Branch a's terminal settled Success — it is now a COMPLETED branch.
            (await db.WorkflowRunNode.AsNoTracking().SingleAsync(n => n.RunId == runId && n.NodeId == "leaf" && n.IterationKey == "map#0")).Status
                .ShouldBe(NodeStatus.Success);
        }

        SuspendProbeNode.FirstPassCount(key, "a").ShouldBe(1, "branch a's suspending pass ran once before it completed");

        // Resolve b → second re-walk. Branch a is replayed from the ledger, NOT re-dispatched.
        (await ResolveBranchViaResumeWaitAsync(runId, key, "b", "RES-b")).ShouldBeTrue();
        await RunEngineAsync(runId);

        using var done = _fixture.BeginScope();
        var fdb = done.Resolve<CodeSpaceDbContext>();
        (await fdb.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId)).Status.ShouldBe(WorkflowRunStatus.Success);

        SuspendProbeNode.FirstPassCount(key, "a").ShouldBe(1,
            "the COMPLETED branch a did NOT re-run its side effect on the sibling-triggered re-walk (#306 for map)");

        var results = JsonDocument.Parse((await MapNodeAsync(fdb, runId)).OutputsJson).RootElement.GetProperty("results");
        results[0].GetProperty("summary").GetString().ShouldBe("RES-a");
        results[1].GetProperty("summary").GetString().ShouldBe("RES-b");
    }

    [Fact]
    public async Task A_duplicate_completion_on_one_branch_wait_is_a_noop()
    {
        // (d) Idempotent double-notify: re-resolving branch a's wait after it already resolved must not
        // re-stamp it, advance the run, or disturb the still-pending sibling b.
        var key = "sp-" + Guid.NewGuid().ToString("N");
        SuspendProbeNode.Reset(key);

        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowId = await CreateWorkflowAsync(teamId, userId, SuspendingMapDefinition(key));
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId, payloadJson: """{ "things": ["a", "b"] }""");

        await RunEngineAsync(runId);

        (await ResolveBranchAsync(runId, key, "a", "RES-a")).ShouldBeTrue("first resolve of branch a succeeds");
        (await ResolveBranchAsync(runId, key, "a", "RES-a-dup")).ShouldBeFalse("a duplicate completion notice is a no-op");

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();
        (await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId)).Status
            .ShouldBe(WorkflowRunStatus.Suspended, "the duplicate notice changed nothing — branch b is still pending");

        var waitA = await db.WorkflowRunWait.AsNoTracking().SingleAsync(w => w.RunId == runId && w.IterationKey == "map#0");
        waitA.Status.ShouldBe(WorkflowWaitStatuses.Resolved);
        waitA.PayloadJson!.ShouldContain("RES-a");
        // The duplicate did not overwrite the first resolution's payload.
        waitA.PayloadJson!.Contains("RES-a-dup").ShouldBeFalse();
        (await db.WorkflowRunWait.AsNoTracking().SingleAsync(w => w.RunId == runId && w.IterationKey == "map#1")).Status
            .ShouldBe(WorkflowWaitStatuses.Pending, "the sibling wait is untouched by the duplicate notice");
    }

    [Fact]
    public async Task The_planner_map_synthesizer_e2e_with_suspending_bodies_resumes_and_synthesizes()
    {
        // (e) THE HEADLINE: a planner stub emits json.subtasks (2 elements); flow.map fans over it; each
        // branch is a SUSPENDING node (the agent.run stand-in) that parks to its own wait; we resume each
        // (wait-for-all barrier); the branch results reduce; a downstream synthesizer reads results[] /
        // results[0].summary / results.length as the run's outputs.
        var key = "sp-" + Guid.NewGuid().ToString("N");
        SuspendProbeNode.Reset(key);

        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowId = await CreateWorkflowAsync(teamId, userId, PlannerSuspendingMapSynthesizerDefinition(key));
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);

        await RunEngineAsync(runId);
        await AssertSuspendedAsync(runId, "both planner subtasks fanned to parking branches");

        // The planner emitted subtasks [{value:"plan-x"},{value:"plan-y"}]; each branch's {{item.value}} is
        // the element key the stub parks under. Resolve both (wait-for-all), then re-walk.
        (await ResolveBranchAsync(runId, key, "plan-x", "DONE-x")).ShouldBeTrue();
        await AssertSuspendedAsync(runId, "the second subtask branch is still parked");
        (await ResolveBranchAsync(runId, key, "plan-y", "DONE-y")).ShouldBeTrue();
        await RunEngineAsync(runId);

        using var done = _fixture.BeginScope();
        var fdb = done.Resolve<CodeSpaceDbContext>();
        var run = await fdb.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId);
        run.Status.ShouldBe(WorkflowRunStatus.Success);

        var runOutputs = JsonDocument.Parse(run.OutputsJson).RootElement;
        runOutputs.GetProperty("length").GetInt32().ShouldBe(2, "the synthesizer read results.length over the reduced array");
        runOutputs.GetProperty("first").GetString().ShouldBe("DONE-x", "results[0].summary is the first subtask's resumed result");
        runOutputs.GetProperty("all").GetArrayLength().ShouldBe(2);
    }

    [Fact]
    public async Task A_terminate_failure_does_not_orphan_a_suspended_siblings_wait()
    {
        // (f) MIXED suspend + terminate (the leaked-wait blocker): in TERMINATE mode, branch "a" parks its own
        // wait while sibling "boom" fails unhandled. Failing the map here would orphan a's Pending wait + leak
        // its dispatched work. Instead the map re-SUSPENDS (suspend wins over the deferred terminate); a's wait
        // survives intact. Once a resolves and no branch is parked, the terminate failure finally fails the map.
        var key = "sp-" + Guid.NewGuid().ToString("N");
        SuspendProbeNode.Reset(key);

        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowId = await CreateWorkflowAsync(teamId, userId, BoomOrSuspendMapDefinition(key, boom: "boom", errorHandling: null));   // null ⇒ terminate
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId, payloadJson: """{ "things": ["a", "boom"] }""");

        await RunEngineAsync(runId);

        using (var parked = _fixture.BeginScope())
        {
            var db = parked.Resolve<CodeSpaceDbContext>();
            (await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId)).Status
                .ShouldBe(WorkflowRunStatus.Suspended, "a suspended sibling re-suspends the map instead of letting the terminate failure orphan its wait");

            // The crux: branch a's Pending wait is NOT orphaned — exactly one Pending wait survives.
            var pending = await db.WorkflowRunWait.AsNoTracking().Where(w => w.RunId == runId && w.Status == WorkflowWaitStatuses.Pending).Select(w => w.IterationKey).ToListAsync();
            pending.ShouldBe(new[] { "map#0" });   // branch a's wait survives; no leaked or extra waits
            pending.Count.ShouldBe(1, "branch a's wait survives; no leaked or extra waits");
        }

        // Resolve a (the last parked branch) → re-walk with no branch parked → the terminate failure fails the map.
        (await ResolveBranchAsync(runId, key, "a", "RES-a")).ShouldBeTrue();
        await RunEngineAsync(runId);

        using var done = _fixture.BeginScope();
        var fdb = done.Resolve<CodeSpaceDbContext>();
        (await fdb.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId)).Status
            .ShouldBe(WorkflowRunStatus.Failure, "with no branch parked, the deferred terminate failure now fails the map");
        (await fdb.WorkflowRunWait.AsNoTracking().CountAsync(w => w.RunId == runId && w.Status == WorkflowWaitStatuses.Pending))
            .ShouldBe(0, "no durable wait is left dangling once the map terminates");
    }

    [Fact]
    public async Task A_still_suspended_branch_does_not_re_run_its_suspended_node_on_a_sibling_immediate_re_walk()
    {
        // (g) EXACTLY-ONCE on the immediate single-wait re-walk path: two branches park. Resolving branch a via
        // ResumeWaitAsync (approval/action path — re-walks at once with NO wait-for-all barrier, branch b still
        // Pending) must NOT re-run branch b's suspended node (re-firing its side effect / minting a fresh
        // token). Assert b's first pass ran exactly once across the sibling-triggered re-walk.
        var key = "sp-" + Guid.NewGuid().ToString("N");
        SuspendProbeNode.Reset(key);

        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowId = await CreateWorkflowAsync(teamId, userId, SuspendingMapDefinition(key));
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId, payloadJson: """{ "things": ["a", "b"] }""");

        await RunEngineAsync(runId);
        await AssertSuspendedAsync(runId, "both branches parked");

        SuspendProbeNode.FirstPassCount(key, "a").ShouldBe(1);
        SuspendProbeNode.FirstPassCount(key, "b").ShouldBe(1, "each branch parked exactly once on the first walk");

        // Capture branch b's wait id BEFORE the re-walk, to prove it survives untouched (not re-minted).
        Guid waitBBefore;
        using (var pre = _fixture.BeginScope())
            waitBBefore = await pre.Resolve<CodeSpaceDbContext>().WorkflowRunWait.AsNoTracking()
                .Where(w => w.RunId == runId && w.Token == $"{key}::b").Select(w => w.Id).SingleAsync();

        // Resolve a via the immediate single-wait path; branch b is still Pending → no barrier → the re-walk
        // re-enters branch b, which must short-circuit back to Suspended WITHOUT re-running its node.
        (await ResolveBranchViaResumeWaitAsync(runId, key, "a", "RES-a")).ShouldBeTrue();
        await RunEngineAsync(runId);

        await AssertSuspendedAsync(runId, "branch b is still parked after branch a completed via the immediate path");
        SuspendProbeNode.FirstPassCount(key, "b").ShouldBe(1,
            "the still-suspended branch b did NOT re-run its suspended node on the sibling-triggered immediate re-walk");

        using (var mid = _fixture.BeginScope())
        {
            var db = mid.Resolve<CodeSpaceDbContext>();
            var waitB = await db.WorkflowRunWait.AsNoTracking().SingleAsync(w => w.RunId == runId && w.Token == $"{key}::b");
            waitB.Id.ShouldBe(waitBBefore, "branch b's wait was not deleted + re-minted by a spurious re-suspend");
            waitB.Status.ShouldBe(WorkflowWaitStatuses.Pending, "branch b's wait stays Pending, untouched, ready for its own signal");
        }

        // Finish: resolve b → both reduce.
        (await ResolveBranchViaResumeWaitAsync(runId, key, "b", "RES-b")).ShouldBeTrue();
        await RunEngineAsync(runId);

        using var done = _fixture.BeginScope();
        var fdb = done.Resolve<CodeSpaceDbContext>();
        (await fdb.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId)).Status.ShouldBe(WorkflowRunStatus.Success);
        var results = JsonDocument.Parse((await MapNodeAsync(fdb, runId)).OutputsJson).RootElement.GetProperty("results");
        results[0].GetProperty("summary").GetString().ShouldBe("RES-a");
        results[1].GetProperty("summary").GetString().ShouldBe("RES-b");
    }

    [Fact]
    public async Task A_continue_mode_failed_branch_is_not_re_executed_when_a_suspending_sibling_resolves()
    {
        // (h) EXACTLY-ONCE on the continue-mode error path (#306 for map): in CONTINUE mode, branch "boom" fails
        // (abandoned + counted) while sibling "a" parks. The map re-suspends on a. When a's wait later resolves
        // and the map re-walks, the abandoned boom branch must be REPLAYED from the ledger (its failure marker),
        // NOT re-dispatched — so its failing node never re-fires + the failed-count stays exactly 1.
        var key = "sp-" + Guid.NewGuid().ToString("N");
        SuspendProbeNode.Reset(key);

        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowId = await CreateWorkflowAsync(teamId, userId, BoomOrSuspendMapDefinition(key, boom: "boom", errorHandling: "continue"));
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId, payloadJson: """{ "things": ["boom", "a"] }""");

        await RunEngineAsync(runId);

        // The map re-suspended: boom (index 0) already failed-and-was-counted, while a (index 1) parks its wait.
        await AssertSuspendedAsync(runId, "branch a parked; the boom branch already failed under continue");
        SuspendProbeNode.FirstPassCount(key, "boom").ShouldBe(1, "the boom branch's failing node ran once before being abandoned");

        // Resolve a (the only parked branch) → re-walk. The boom branch is replayed from the ledger, NOT re-run.
        (await ResolveBranchAsync(runId, key, "a", "RES-a")).ShouldBeTrue();
        await RunEngineAsync(runId);

        using var done = _fixture.BeginScope();
        var fdb = done.Resolve<CodeSpaceDbContext>();
        (await fdb.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId)).Status
            .ShouldBe(WorkflowRunStatus.Success, "continue mode keeps the map alive; it completes once the suspending sibling resolves");

        SuspendProbeNode.FirstPassCount(key, "boom").ShouldBe(1,
            "the abandoned boom branch did NOT re-run its failing node on the sibling-triggered re-walk (#306 for map, error path)");

        var outputs = JsonDocument.Parse((await MapNodeAsync(fdb, runId)).OutputsJson).RootElement;
        outputs.GetProperty("count").GetInt32().ShouldBe(2);
        outputs.GetProperty("failed").GetInt32().ShouldBe(1, "the boom branch's failure is counted exactly once across the re-walk");

        var results = outputs.GetProperty("results");
        // results[0] is the boom branch's failure marker; results[1] is a's resumed result — ordered by index.
        results[0].TryGetProperty("error", out _).ShouldBeTrue("the abandoned branch's result is its failure marker, replayed");
        results[1].GetProperty("summary").GetString().ShouldBe("RES-a");
    }

    [Fact]
    public async Task Two_map_branches_resolving_their_action_waits_concurrently_both_resolve_and_the_run_reaches_success()
    {
        // (i) THE PR-A REGRESSION: two map branches each park their OWN Action wait, then BOTH are resolved
        // CONCURRENTLY (the two-near-simultaneous-human-clicks scenario) via ResumeByActionTokenAsync — the
        // path that funnels into ResumeCoreAsync. On the OLD flip-before-resolve code, the loser of the run-
        // flip CAS returned AlreadyResolved WITHOUT resolving its own wait → that branch's wait was orphaned
        // → the run stranded Suspended forever (the human's decision lost). With resolve-FIRST + dispatch-on-
        // last, the wait status CAS is the gate: BOTH callers resolve their own wait (both Resumed), and the
        // run reaches Success. Cases a/b resolve SEQUENTIALLY and can't catch this; this one interleaves.
        var key = "sp-" + Guid.NewGuid().ToString("N");
        SuspendProbeNode.Reset(key);

        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowId = await CreateWorkflowAsync(teamId, userId, SuspendingMapDefinition(key));
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId, payloadJson: """{ "things": ["a", "b"] }""");

        await RunEngineAsync(runId);
        await AssertSuspendedAsync(runId, "both branches parked their own Action wait");

        // Each branch's wait token is "<key>::<element>" (minted by SuspendProbeNode). Fire BOTH resolves
        // concurrently — each in its OWN DI scope, since CodeSpaceDbContext is scoped and not thread-safe.
        var resolveA = ResolveBranchViaActionTokenAsync($"{key}::a", "approve-a", userId, teamId);
        var resolveB = ResolveBranchViaActionTokenAsync($"{key}::b", "approve-b", userId, teamId);
        var results = await Task.WhenAll(resolveA, resolveB);

        // BOTH calls must have resolved their OWN wait — neither lost to a run-flip race before resolving.
        results[0].ShouldBe(ActionResumeResult.Resumed, "branch a's concurrent click resolved its own wait");
        results[1].ShouldBe(ActionResumeResult.Resumed, "branch b's concurrent click resolved its own wait");

        using (var verify = _fixture.BeginScope())
        {
            var db = verify.Resolve<CodeSpaceDbContext>();
            // The crux: NEITHER wait is orphaned. Both ended Resolved (on the old code one stays Pending).
            (await db.WorkflowRunWait.AsNoTracking().CountAsync(w => w.RunId == runId && w.Status == WorkflowWaitStatuses.Pending))
                .ShouldBe(0, "neither branch's wait is orphaned by the concurrent flip-before-resolve race");
            (await db.WorkflowRunWait.AsNoTracking().CountAsync(w => w.RunId == runId && w.Status == WorkflowWaitStatuses.Resolved))
                .ShouldBe(2, "both branch waits resolved with their own concurrent click");
        }

        // The run was flipped + dispatched exactly once by whichever caller resolved the LAST wait → re-walk.
        await RunEngineAsync(runId);

        using var done = _fixture.BeginScope();
        var fdb = done.Resolve<CodeSpaceDbContext>();
        (await fdb.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId)).Status
            .ShouldBe(WorkflowRunStatus.Success, "the map run completes — it was NOT stranded Suspended by the orphaned-wait race");

        var resultsJson = JsonDocument.Parse((await MapNodeAsync(fdb, runId)).OutputsJson).RootElement.GetProperty("results");
        resultsJson.GetArrayLength().ShouldBe(2, "both branches reduced after both concurrent resolves landed");
    }

    [Fact]
    public async Task A_single_wait_run_resumes_identically_via_the_action_token_path()
    {
        // (j) NON-REGRESSION for the overwhelmingly common single-wait case: a run parked on ONE Action wait
        // resolves, finds no other pending wait, flips + dispatches, and reaches Success — the same observable
        // outcome as before the resolve-first reorder. A map of ONE element is the minimal single-wait shape.
        var key = "sp-" + Guid.NewGuid().ToString("N");
        SuspendProbeNode.Reset(key);

        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowId = await CreateWorkflowAsync(teamId, userId, SuspendingMapDefinition(key));
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId, payloadJson: """{ "things": ["solo"] }""");

        await RunEngineAsync(runId);

        using (var parked = _fixture.BeginScope())
            (await parked.Resolve<CodeSpaceDbContext>().WorkflowRunWait.AsNoTracking().CountAsync(w => w.RunId == runId && w.Status == WorkflowWaitStatuses.Pending))
                .ShouldBe(1, "the single-wait run parks exactly one wait");

        (await ResolveBranchViaActionTokenAsync($"{key}::solo", "approve", userId, teamId))
            .ShouldBe(ActionResumeResult.Resumed, "the single wait resolves, no sibling pending → flip + dispatch (unchanged)");

        await RunEngineAsync(runId);

        using var done = _fixture.BeginScope();
        (await done.Resolve<CodeSpaceDbContext>().WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId)).Status
            .ShouldBe(WorkflowRunStatus.Success, "single-wait resume reaches Success identically to before the reorder");
    }

    [Fact]
    public async Task A_nested_map_in_map_resumes_in_scrambled_leaf_order_with_exactly_once_and_ordered_nested_reduce()
    {
        // (k) GAP-3 — NESTED map-in-map durable resume (the one resume path the single-level cases above never
        // exercise). An OUTER flow.map fans over ["o0","o1"]; each outer branch body is itself an INNER flow.map
        // fanning over ["{{item}}::j0","{{item}}::j1"], whose body node SUSPENDS. So four leaf waits park, keyed
        // "outer#i/inner#j". We resolve them in a SCRAMBLED order that interleaves across outer branches —
        // o0/inner1, o1/inner0, o0/inner0, o1/inner1 — never natural order. The two latent-correctness pins:
        //   (b) EXACTLY-ONCE — each leaf's suspending pass ran once and resumed once (FirstPassCount == 1 per
        //       leaf across every sibling-triggered re-walk); no settled outer/inner branch re-ran;
        //   (c) ORDERED NESTED REDUCE — outer results land in OUTER element order and each outer[i]'s inner
        //       results land in INNER element order, REGARDLESS of the scrambled resolve order. Each leaf echoes
        //       its own element ("o0::j1" …) + its own resolved payload, so a misplaced [i][j] slot is visible.
        var key = "sp-" + Guid.NewGuid().ToString("N");
        SuspendProbeNode.Reset(key);

        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowId = await CreateWorkflowAsync(teamId, userId, NestedSuspendingMapDefinition(key));
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId, payloadJson: """{ "things": ["o0", "o1"] }""");

        await RunEngineAsync(runId);

        // All four leaves park. The run is Suspended with four Pending waits, one per "outer#i/inner#j" leaf.
        var leaves = new[] { "o0::j0", "o0::j1", "o1::j0", "o1::j1" };
        using (var parked = _fixture.BeginScope())
        {
            var db = parked.Resolve<CodeSpaceDbContext>();
            (await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId)).Status.ShouldBe(WorkflowRunStatus.Suspended);
            (await db.WorkflowRunWait.AsNoTracking().CountAsync(w => w.RunId == runId && w.Status == WorkflowWaitStatuses.Pending))
                .ShouldBe(4, "2 outer branches × 2 inner branches = 4 leaf waits, each under 'outer#i/inner#j'");
            // Each leaf wait is keyed to its nested iteration key — the engine's outer#i/inner#j attribution.
            var keys = await db.WorkflowRunWait.AsNoTracking().Where(w => w.RunId == runId).Select(w => w.IterationKey).ToListAsync();
            keys.OrderBy(k => k).ShouldBe(new[] { "outer#0/inner#0", "outer#0/inner#1", "outer#1/inner#0", "outer#1/inner#1" });
        }

        foreach (var leaf in leaves)
            SuspendProbeNode.FirstPassCount(key, leaf).ShouldBe(1, $"leaf '{leaf}' parked exactly once on the first walk");

        // SCRAMBLED resolve order — interleave across outer branches, never natural order. Each non-final resolve
        // keeps the run Suspended (siblings still parked → the wait-for-all barrier holds); the last re-dispatches.
        (await ResolveBranchAsync(runId, key, "o0::j1", "R-o0j1")).ShouldBeTrue();
        await AssertSuspendedAsync(runId, "three leaves still pending after the first scrambled resolve");

        (await ResolveBranchAsync(runId, key, "o1::j0", "R-o1j0")).ShouldBeTrue();
        await AssertSuspendedAsync(runId, "two leaves still pending after the second scrambled resolve");

        (await ResolveBranchAsync(runId, key, "o0::j0", "R-o0j0")).ShouldBeTrue();
        await AssertSuspendedAsync(runId, "one leaf still pending after the third scrambled resolve");

        (await ResolveBranchAsync(runId, key, "o1::j1", "R-o1j1")).ShouldBeTrue();   // the LAST → re-dispatch
        await RunEngineAsync(runId);

        using var done = _fixture.BeginScope();
        var fdb = done.Resolve<CodeSpaceDbContext>();
        (await fdb.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId)).Status.ShouldBe(WorkflowRunStatus.Success);

        // EXACTLY-ONCE: every leaf's suspending pass ran exactly once across the whole multi-resume sequence —
        // no settled outer branch (and no settled inner branch within it) re-ran on a sibling-triggered re-walk.
        foreach (var leaf in leaves)
            SuspendProbeNode.FirstPassCount(key, leaf).ShouldBe(1,
                $"leaf '{leaf}' ran its suspending pass once — no nested branch re-dispatched on any scrambled re-walk");

        // ORDERED NESTED REDUCE: outer[i].results is the inner reduced array; assert the EXACT [i][j] structure.
        // outer is element order (o0, o1); each inner is element order (j0, j1) — NOT the scrambled resolve order.
        var outerNode = await fdb.WorkflowRunNode.AsNoTracking().SingleAsync(n => n.RunId == runId && n.NodeId == "outer" && n.IterationKey == "");
        var outerOutputs = JsonDocument.Parse(outerNode.OutputsJson).RootElement;
        outerOutputs.GetProperty("count").GetInt32().ShouldBe(2, "two outer branches reduced");

        var outer = outerOutputs.GetProperty("results");
        outer.GetArrayLength().ShouldBe(2);

        // outer[0] is branch o0 — its inner reduced array, element-ordered j0 then j1, each echoing its own leaf.
        var o0Inner = outer[0].GetProperty("results");
        o0Inner.GetArrayLength().ShouldBe(2);
        o0Inner[0].GetProperty("item").GetString().ShouldBe("o0::j0");
        o0Inner[0].GetProperty("summary").GetString().ShouldBe("R-o0j0");
        o0Inner[1].GetProperty("item").GetString().ShouldBe("o0::j1");
        o0Inner[1].GetProperty("summary").GetString().ShouldBe("R-o0j1");

        // outer[1] is branch o1 — likewise element-ordered j0 then j1, regardless of when each leaf resolved.
        var o1Inner = outer[1].GetProperty("results");
        o1Inner.GetArrayLength().ShouldBe(2);
        o1Inner[0].GetProperty("item").GetString().ShouldBe("o1::j0");
        o1Inner[0].GetProperty("summary").GetString().ShouldBe("R-o1j0");
        o1Inner[1].GetProperty("item").GetString().ShouldBe("o1::j1");
        o1Inner[1].GetProperty("summary").GetString().ShouldBe("R-o1j1");
    }

    [Fact]
    public async Task A_nested_map_in_map_resolved_via_the_immediate_barrier_free_path_resumes_each_leaf_exactly_once()
    {
        // (l) GAP — NESTED map-in-map over the IMMEDIATE single-wait resume path (ResumeWaitAsync), the
        // approval/action route that flips the run Pending + re-walks at once even with siblings still parked
        // (NO wait-for-all barrier). The barrier path is covered by the #403 nested test above; this exercises
        // the OTHER resume route through the same nested structure. The latent risk this guards: on a barrier-
        // free re-walk, a still-suspended inner leaf in another (or the same) outer branch must NOT re-fire its
        // suspending node. Pins FirstPassCount == 1 per leaf across every immediate re-walk, plus the run reaches
        // Success once the last leaf resolves — proving no inner branch re-dispatched on any barrier-free re-walk.
        var key = "sp-" + Guid.NewGuid().ToString("N");
        SuspendProbeNode.Reset(key);

        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowId = await CreateWorkflowAsync(teamId, userId, NestedSuspendingMapDefinition(key));
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId, payloadJson: """{ "things": ["o0", "o1"] }""");

        await RunEngineAsync(runId);

        var leaves = new[] { "o0::j0", "o0::j1", "o1::j0", "o1::j1" };
        await AssertSuspendedAsync(runId, "all four nested leaves parked on the first walk");
        foreach (var leaf in leaves)
            SuspendProbeNode.FirstPassCount(key, leaf).ShouldBe(1, $"leaf '{leaf}' parked exactly once on the first walk");

        // Resolve each leaf via the IMMEDIATE single-wait path (ResumeWaitAsync re-walks at once, no barrier). After
        // each non-final resolve the run re-suspends (other leaves still parked); the immediate re-walk re-enters the
        // still-suspended leaves, which must short-circuit back to Suspended WITHOUT re-running their node.
        for (var i = 0; i < leaves.Length - 1; i++)
        {
            (await ResolveBranchViaResumeWaitAsync(runId, key, leaves[i], $"R-{leaves[i]}")).ShouldBeTrue();
            await RunEngineAsync(runId);
            await AssertSuspendedAsync(runId, $"after immediate-resolving {leaves[i]}, the remaining leaves are still parked");
        }

        foreach (var leaf in leaves)
            SuspendProbeNode.FirstPassCount(key, leaf).ShouldBe(1,
                $"leaf '{leaf}' ran its suspending pass once — no inner branch re-fired on a barrier-free immediate re-walk");

        // Resolve the LAST leaf → final immediate re-walk → the whole nested map completes.
        (await ResolveBranchViaResumeWaitAsync(runId, key, leaves[^1], $"R-{leaves[^1]}")).ShouldBeTrue();
        await RunEngineAsync(runId);

        using var done = _fixture.BeginScope();
        var fdb = done.Resolve<CodeSpaceDbContext>();
        (await fdb.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId)).Status.ShouldBe(WorkflowRunStatus.Success);

        foreach (var leaf in leaves)
            SuspendProbeNode.FirstPassCount(key, leaf).ShouldBe(1,
                $"leaf '{leaf}' resumed exactly once across the whole immediate-path sequence — no nested re-dispatch");

        // ORDERED NESTED REDUCE over the immediate path too — at #403 depth: assert ALL four leaves' item AND
        // summary payloads at their exact [i][j] slots, so a misplaced slot (outer/inner element order vs the
        // resolve order) is visible. Each leaf echoes its own element + its own resolved "R-<element>" summary.
        var outerNode = await fdb.WorkflowRunNode.AsNoTracking().SingleAsync(n => n.RunId == runId && n.NodeId == "outer" && n.IterationKey == "");
        var outerOutputs = JsonDocument.Parse(outerNode.OutputsJson).RootElement;
        outerOutputs.GetProperty("count").GetInt32().ShouldBe(2, "two outer branches reduced");

        var outer = outerOutputs.GetProperty("results");
        outer.GetArrayLength().ShouldBe(2);

        // outer[0] is branch o0 — its inner reduced array, element-ordered j0 then j1, each echoing its own leaf.
        var o0Inner = outer[0].GetProperty("results");
        o0Inner.GetArrayLength().ShouldBe(2);
        o0Inner[0].GetProperty("item").GetString().ShouldBe("o0::j0");
        o0Inner[0].GetProperty("summary").GetString().ShouldBe("R-o0::j0");
        o0Inner[1].GetProperty("item").GetString().ShouldBe("o0::j1");
        o0Inner[1].GetProperty("summary").GetString().ShouldBe("R-o0::j1");

        // outer[1] is branch o1 — likewise element-ordered j0 then j1, regardless of when each leaf resolved.
        var o1Inner = outer[1].GetProperty("results");
        o1Inner.GetArrayLength().ShouldBe(2);
        o1Inner[0].GetProperty("item").GetString().ShouldBe("o1::j0");
        o1Inner[0].GetProperty("summary").GetString().ShouldBe("R-o1::j0");
        o1Inner[1].GetProperty("item").GetString().ShouldBe("o1::j1");
        o1Inner[1].GetProperty("summary").GetString().ShouldBe("R-o1::j1");
    }

    // ─── Helpers ────────────────────────────────────────────────────────────────

    // Resolve ONE branch's Action wait via ResumeByActionTokenAsync — the human-click path that funnels into
    // ResumeCoreAsync (the path the PR-A fix targets). Located by the stub's correlation token "<key>::<element>".
    private async Task<ActionResumeResult> ResolveBranchViaActionTokenAsync(string token, string actionKey, Guid actorUserId, Guid teamId)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<IWorkflowResumeService>()
            .ResumeByActionTokenAsync(token, actionKey, actorUserId, comment: null, values: null, teamId, CancellationToken.None);
    }


    private static async Task<Core.Persistence.Entities.WorkflowRunNode> MapNodeAsync(CodeSpaceDbContext db, Guid runId) =>
        await db.WorkflowRunNode.AsNoTracking().SingleAsync(n => n.RunId == runId && n.NodeId == "map" && n.IterationKey == "");

    private async Task AssertSuspendedAsync(Guid runId, string because)
    {
        using var scope = _fixture.BeginScope();
        (await scope.Resolve<CodeSpaceDbContext>().WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId)).Status
            .ShouldBe(WorkflowRunStatus.Suspended, because);
    }

    // Resolve ONE branch's wait via the wait-for-all barrier path (ResumeOnWaitCompletionAsync) — the exact
    // entry point the agent + sub-workflow completion notifiers use: resolves only this wait, re-dispatches
    // only when no sibling wait remains pending. Located by the stub's correlation token "<key>::<element>".
    private async Task<bool> ResolveBranchAsync(Guid runId, string key, string element, string summary)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var waitId = await db.WorkflowRunWait.AsNoTracking()
            .Where(w => w.RunId == runId && w.Token == $"{key}::{element}")
            .Select(w => w.Id).SingleAsync();

        var payload = JsonSerializer.Serialize(new { summary });
        return await scope.Resolve<IWorkflowResumeService>().ResumeOnWaitCompletionAsync(runId, waitId, payload, CancellationToken.None);
    }

    // Resolve ONE branch's wait via the single-wait IMMEDIATE re-dispatch path (ResumeWaitAsync) — the
    // approval/timer/callback path that flips the run Pending + re-walks even with a sibling still pending.
    // Used by the exactly-once test to force the completed/suspended split across separate re-walks.
    private async Task<bool> ResolveBranchViaResumeWaitAsync(Guid runId, string key, string element, string summary)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var waitId = await db.WorkflowRunWait.AsNoTracking()
            .Where(w => w.RunId == runId && w.Token == $"{key}::{element}" && w.Status == WorkflowWaitStatuses.Pending)
            .Select(w => w.Id).SingleAsync();

        var payload = JsonSerializer.Serialize(new { summary });
        return await scope.Resolve<IWorkflowResumeService>().ResumeWaitAsync(runId, waitId, payload, CancellationToken.None);
    }

    private async Task<Guid> CreateWorkflowAsync(Guid teamId, Guid userId, WorkflowDefinition definition)
    {
        using var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin);
        return await scope.Resolve<IMediator>().Send(new CreateWorkflowCommand
        {
            Name = "map-pr2-" + Guid.NewGuid().ToString("N")[..6],
            Description = null,
            Definition = definition,
            Activations = new List<WorkflowActivationInput>(),
            Enabled = true,
        });
    }

    private async Task RunEngineAsync(Guid runId)
    {
        using var scope = _fixture.BeginScope();
        await scope.Resolve<IWorkflowEngine>().ExecuteRunAsync(runId, CancellationToken.None);
    }

    // manual → map(items={{trigger.things}}; body: ms → park[SuspendProbe, the agent.run stand-in]) → terminal.
    // The body terminal `park` parks an Action wait on its first pass and echoes { item, summary } on resume.
    private static WorkflowDefinition SuspendingMapDefinition(string key) => new()
    {
        SchemaVersion = 1,
        Nodes = new List<NodeDefinition>
        {
            new() { Id = "start", TypeKey = "trigger.manual", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "map", TypeKey = "flow.map", Config = WorkflowsTestSeed.EmptyJson(),
                    Inputs = WorkflowsTestSeed.Json("""{ "items": "{{trigger.things}}" }""") },
            new() { Id = "ms", TypeKey = "flow.map_start", ParentId = "map", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "leaf", TypeKey = SuspendProbeNode.Key, ParentId = "map", Config = WorkflowsTestSeed.EmptyJson(),
                    Inputs = WorkflowsTestSeed.Json("""{ "key": "__KEY__", "item": "{{item}}" }""".Replace("__KEY__", key)) },
            new() { Id = "end", TypeKey = "builtin.terminal", Config = WorkflowsTestSeed.EmptyJson(),
                    Inputs = WorkflowsTestSeed.Json("""{ "count": "{{nodes.map.outputs.count}}" }""") },
        },
        Edges = new List<EdgeDefinition>
        {
            new() { From = "start", To = "map" },
            new() { From = "map", To = "end" },
            new() { From = "ms", To = "leaf" },
        },
    };

    // NESTED map-in-map (GAP-3). manual → OUTER flow.map(items={{trigger.things}}; body: mso → INNER flow.map →
    // outerTerm) → end. The inner map's items are "{{item}}::j0"/"{{item}}::j1" — resolved in the OUTER branch
    // scope, so each carries the outer element (o0 → ["o0::j0","o0::j1"]). The inner body is mst → leaf
    // [SuspendProbe item={{item}}], where leaf is the inner body terminal (it echoes {item,summary} on resume),
    // so inner results[j] = that leaf's resolved result. outerTerm is a JsonEmitNode echoing the inner map's
    // reduced array, so outer results[i] = { results: [innerArray] } — the engine's outer#i/inner#j attribution
    // gives the [i][j] nested shape. A map body's terminal must produce SCOPE outputs (a builtin.terminal would
    // not — its inputs become run outputs, not nodes.<id>.outputs), so JsonEmitNode is the outer body terminal.
    private static WorkflowDefinition NestedSuspendingMapDefinition(string key) => new()
    {
        SchemaVersion = 1,
        Nodes = new List<NodeDefinition>
        {
            new() { Id = "start", TypeKey = "trigger.manual", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "outer", TypeKey = "flow.map", Config = WorkflowsTestSeed.EmptyJson(),
                    Inputs = WorkflowsTestSeed.Json("""{ "items": "{{trigger.things}}" }""") },
            new() { Id = "mso", TypeKey = "flow.map_start", ParentId = "outer", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "inner", TypeKey = "flow.map", ParentId = "outer", Config = WorkflowsTestSeed.EmptyJson(),
                    Inputs = WorkflowsTestSeed.Json("""{ "items": [ "{{item}}::j0", "{{item}}::j1" ] }""") },
            new() { Id = "mst", TypeKey = "flow.map_start", ParentId = "inner", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "leaf", TypeKey = SuspendProbeNode.Key, ParentId = "inner", Config = WorkflowsTestSeed.EmptyJson(),
                    Inputs = WorkflowsTestSeed.Json("""{ "key": "__KEY__", "item": "{{item}}" }""".Replace("__KEY__", key)) },
            new() { Id = "outerTerm", TypeKey = JsonEmitNode.Key, ParentId = "outer", Config = WorkflowsTestSeed.EmptyJson(),
                    Inputs = WorkflowsTestSeed.Json("""{ "results": "{{nodes.inner.outputs.results}}" }""") },
            new() { Id = "end", TypeKey = "builtin.terminal", Config = WorkflowsTestSeed.EmptyJson(),
                    Inputs = WorkflowsTestSeed.Json("""{ "count": "{{nodes.outer.outputs.count}}" }""") },
        },
        Edges = new List<EdgeDefinition>
        {
            new() { From = "start", To = "outer" },
            new() { From = "outer", To = "end" },
            new() { From = "mso", To = "inner" },        // outer body: start → inner map → outer terminal
            new() { From = "inner", To = "outerTerm" },
            new() { From = "mst", To = "leaf" },          // inner body: start → suspending leaf (the inner terminal)
        },
    };

    // manual → planner[emits json.subtasks=[{value:"plan-x"},{value:"plan-y"}]] → map(items={{...subtasks}};
    // body: ms → park[SuspendProbe item={{item.value}}]) → synthesizer terminal(reads results / [0].summary / .length).
    private static WorkflowDefinition PlannerSuspendingMapSynthesizerDefinition(string key) => new()
    {
        SchemaVersion = 1,
        Nodes = new List<NodeDefinition>
        {
            new() { Id = "start", TypeKey = "trigger.manual", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "planner", TypeKey = JsonEmitNode.Key, Config = WorkflowsTestSeed.EmptyJson(),
                    Inputs = WorkflowsTestSeed.Json("""{ "json": { "subtasks": [ { "value": "plan-x" }, { "value": "plan-y" } ] } }""") },
            new() { Id = "map", TypeKey = "flow.map", Config = WorkflowsTestSeed.EmptyJson(),
                    Inputs = WorkflowsTestSeed.Json("""{ "items": "{{nodes.planner.outputs.json.subtasks}}" }""") },
            new() { Id = "ms", TypeKey = "flow.map_start", ParentId = "map", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "park", TypeKey = SuspendProbeNode.Key, ParentId = "map", Config = WorkflowsTestSeed.EmptyJson(),
                    Inputs = WorkflowsTestSeed.Json("""{ "key": "__KEY__", "item": "{{item.value}}" }""".Replace("__KEY__", key)) },
            new() { Id = "end", TypeKey = "builtin.terminal", Config = WorkflowsTestSeed.EmptyJson(),
                    Inputs = WorkflowsTestSeed.Json("""{ "all": "{{nodes.map.outputs.results}}", "first": "{{nodes.map.outputs.results[0].summary}}", "length": "{{nodes.map.outputs.results.length}}" }""") },
        },
        Edges = new List<EdgeDefinition>
        {
            new() { From = "start", To = "planner" },
            new() { From = "planner", To = "map" },
            new() { From = "map", To = "end" },
            new() { From = "ms", To = "park" },
        },
    };

    // manual → map(errorHandling; body: ms → leaf[SuspendProbe with boom={{boom}}]) → terminal. The leaf FAILS
    // for the element equal to `boom` (the abandon / terminate point) and SUSPENDS for every other element —
    // the single body shape both the mixed-suspend+terminate (#1) and continue-error-replay (#3) tests need.
    private static WorkflowDefinition BoomOrSuspendMapDefinition(string key, string boom, string? errorHandling)
    {
        var mapConfig = errorHandling != null ? $$"""{ "errorHandling": "{{errorHandling}}" }""" : "{}";
        return new WorkflowDefinition
        {
            SchemaVersion = 1,
            Nodes = new List<NodeDefinition>
            {
                new() { Id = "start", TypeKey = "trigger.manual", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
                new() { Id = "map", TypeKey = "flow.map", Config = WorkflowsTestSeed.Json(mapConfig),
                        Inputs = WorkflowsTestSeed.Json("""{ "items": "{{trigger.things}}" }""") },
                new() { Id = "ms", TypeKey = "flow.map_start", ParentId = "map", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
                new() { Id = "leaf", TypeKey = SuspendProbeNode.Key, ParentId = "map", Config = WorkflowsTestSeed.EmptyJson(),
                        Inputs = WorkflowsTestSeed.Json("""{ "key": "__KEY__", "item": "{{item}}", "boom": "__BOOM__" }""".Replace("__KEY__", key).Replace("__BOOM__", boom)) },
                new() { Id = "end", TypeKey = "builtin.terminal", Config = WorkflowsTestSeed.EmptyJson(),
                        Inputs = WorkflowsTestSeed.Json("""{ "count": "{{nodes.map.outputs.count}}" }""") },
            },
            Edges = new List<EdgeDefinition>
            {
                new() { From = "start", To = "map" },
                new() { From = "map", To = "end" },
                new() { From = "ms", To = "leaf" },
            },
        };
    }
}
