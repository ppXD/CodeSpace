using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Variables;
using CodeSpace.Core.Services.Workflows;
using CodeSpace.Core.Services.Workflows.Engine;
using CodeSpace.Core.Services.Workflows.RunSources;
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

namespace CodeSpace.IntegrationTests.Workflows;

/// <summary>
/// From-node rerun (D7) — drives the REAL <see cref="IWorkflowService.RerunFromNodeAsync"/> seam against a real
/// engine + real Postgres, then re-walks the fork with the real <see cref="IWorkflowEngine"/> (no hand-staged
/// rows — unlike the replay tests' <c>StageReplayAsync</c>, this exercises the production planner → side-effect
/// gate → reusability resolver → forked staging → cell pre-seed → RehydrateFromLedger resume chain end-to-end).
///
/// <para>Tier: high-fidelity — real <see cref="RerunFromNodeAsync"/>, real <see cref="IRunFromSnapshotStarter"/>
/// (snapshot origin), real <see cref="IVariableService"/> (frozen-scope), real engine re-walk. The crown jewel is
/// the <see cref="MutatingProbeNode"/> execution counter: a node that is REUSED never re-enters RunAsync, so its
/// counter is unchanged — hard proof the upstream output was carried forward, not recomputed. The generic
/// reuse-vs-rerun discriminator across every shape is the <c>node.started</c> ledger count: 0 for a reused cell
/// (pre-seeded terminal-only), 1 for a genuinely re-run node.</para>
///
/// <para>Coverage: happy-path reuse (linear / diamond / skipped-branch / failed-with-error-edge / completed
/// container / root≡replay); origin variants + frozen scope (authored / snapshot / empty-snapshot sentinel /
/// frozen plain var); lineage + atomic pre-seed + original-immutability; and every fail-closed gate (cross-team
/// 404, unknown node, side-effecting node in closure, non-reusable kept upstream) — each asserting NOTHING was
/// written.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class RerunFromNodeFlowTests
{
    private readonly PostgresFixture _fixture;

    public RerunFromNodeFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    // ─────────────────────────────────────────────────────────────────────────────
    //  A. Happy-path reuse — the crown jewels
    // ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Rerun_from_midpoint_reuses_side_effecting_upstream_without_re_executing_it()
    {
        // CROWN JEWEL. start → mutator(side-effecting, counts executions) → transform(echoes mutator.n) → end.
        // Rerun from "transform": the mutator is UPSTREAM (kept + reused), the transform + end re-run. If reuse
        // is real, the mutator's RunAsync never fires again (counter stays 1) and the transform re-computes from
        // the CARRIED-FORWARD mutator output (n=1). A broken reuse would re-run the mutator → counter 2, n=2.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var probeKey = "rerun-crown-" + Guid.NewGuid().ToString("N");
        MutatingProbeNode.Reset(probeKey);

        var workflowId = await CreateWorkflowAsync(teamId, userId, MutatorTransformDef(probeKey));
        var originalRunId = await RunFreshAsync(workflowId, teamId);

        await AssertRunStatusAsync(originalRunId, WorkflowRunStatus.Success);
        MutatingProbeNode.ExecutionsFor(probeKey).ShouldBe(1, "the original run executes the mutator exactly once");

        var rerunId = await RerunAsync(originalRunId, "transform", teamId, userId);
        await RunEngineAsync(rerunId);

        await AssertRunStatusAsync(rerunId, WorkflowRunStatus.Success);
        MutatingProbeNode.ExecutionsFor(probeKey).ShouldBe(1,
            "the mutator is a REUSED upstream node — it must NOT re-execute on the rerun (counter would be 2 if it did)");

        // node.started discriminator: reused cells carry zero starts; re-run cells carry exactly one (no retries).
        (await NodeStartedCountAsync(rerunId, "start")).ShouldBe(0, "reused trigger node was not re-run");
        (await NodeStartedCountAsync(rerunId, "mutator")).ShouldBe(0, "reused side-effecting node was not re-run");
        (await NodeStartedCountAsync(rerunId, "transform")).ShouldBe(1, "the from-node re-ran exactly once");
        (await NodeStartedCountAsync(rerunId, "end")).ShouldBe(1, "the downstream node re-ran exactly once");

        // The transform recomputed from the carried-forward mutator output → n is still 1, proving the reused
        // upstream OUTPUT (not just the absence of execution) flowed into the re-run frontier.
        var transformCell = await LoadCellAsync(rerunId, "transform");
        JsonDocument.Parse(transformCell.OutputsJson).RootElement.GetProperty("n").GetInt32()
            .ShouldBe(1, "the re-run transform must echo the REUSED mutator output, not a recomputed one");

        // Exactly-once-on-fork: after the walk, every cell — reused or re-run — carries exactly ONE terminal
        // record. A reused cell that the engine wrongly re-settled, or a re-run node that double-fired, would
        // show two. This pins the durable ledger's exactly-once property across the rerun re-walk.
        foreach (var n in new[] { "start", "mutator", "transform", "end" })
            (await TerminalRecordCountAsync(rerunId, n)).ShouldBe(1, $"node '{n}' must have exactly one terminal record on the fork");
    }

    [Fact]
    public async Task Rerun_from_diamond_join_reuses_every_upstream_arm()
    {
        // start → A → {B, C} → D(join) → end. Rerun from the join D reuses start, A, B, C; re-runs D + end. The
        // node.started counts prove the whole fan-out/fan-in upstream subgraph was carried forward as one frontier.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowId = await CreateWorkflowAsync(teamId, userId, DiamondDef());
        var originalRunId = await RunFreshAsync(workflowId, teamId);
        await AssertRunStatusAsync(originalRunId, WorkflowRunStatus.Success);

        var rerunId = await RerunAsync(originalRunId, "d", teamId, userId);
        await RunEngineAsync(rerunId);

        await AssertRunStatusAsync(rerunId, WorkflowRunStatus.Success);
        foreach (var kept in new[] { "start", "a", "b", "c" })
            (await NodeStartedCountAsync(rerunId, kept)).ShouldBe(0, $"diamond upstream '{kept}' was reused, not re-run");
        (await NodeStartedCountAsync(rerunId, "d")).ShouldBe(1, "the join re-ran exactly once");
        (await NodeStartedCountAsync(rerunId, "end")).ShouldBe(1, "the terminal re-ran exactly once");
    }

    [Fact]
    public async Task Rerun_reuses_skipped_branch_as_skipped()
    {
        // start → if(true) → {T runs, F skipped} → J(join) → end. Rerun from J reuses the branch: T as Success,
        // F as Skipped (the NodeSkipped seeder path), the if with its routing hints. The fork must faithfully
        // carry F's skipped state — not re-enliven the dead branch.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowId = await CreateWorkflowAsync(teamId, userId, BranchJoinDef());
        var originalRunId = await RunFreshAsync(workflowId, teamId, payloadJson: """{"branch":"yes"}""");
        await AssertRunStatusAsync(originalRunId, WorkflowRunStatus.Success);
        (await LoadCellAsync(originalRunId, "f")).Status.ShouldBe(NodeStatus.Skipped, "the false branch was skipped originally");

        var rerunId = await RerunAsync(originalRunId, "j", teamId, userId);
        await RunEngineAsync(rerunId);

        await AssertRunStatusAsync(rerunId, WorkflowRunStatus.Success);
        (await LoadCellAsync(rerunId, "f")).Status.ShouldBe(NodeStatus.Skipped,
            "a reused skipped branch must STAY skipped on the fork (NodeSkipped pre-seed path)");
        (await NodeStartedCountAsync(rerunId, "f")).ShouldBe(0, "the reused skipped branch did not re-run");
        (await NodeStartedCountAsync(rerunId, "t")).ShouldBe(0, "the reused taken branch did not re-run");
        (await NodeStartedCountAsync(rerunId, "j")).ShouldBe(1, "the join re-ran exactly once");

        // Load-bearing routing-hints carry-forward: the kept branch cell's frozen hints MUST survive onto the
        // fork. A seeder that dropped them would re-emit the iff cell with null hints — and null hints make
        // IsEdgeLive treat BOTH handles as live, so the originally-dead 'false' edge would re-enliven. Assert the
        // exact frozen hint is carried so this test fails if hints are ever dropped.
        var iffCell = await LoadCellAsync(rerunId, "iff");
        iffCell.RoutingHintsJson.ShouldNotBeNull("the reused branch cell must carry its frozen routing hints, not null");
        JsonDocument.Parse(iffCell.RoutingHintsJson!).RootElement.EnumerateArray().Select(e => e.GetString())
            .ShouldBe(new[] { "true" }, "the carried-forward hint must be the original frozen decision ('true'), so the dead 'false' edge stays dead");
    }

    [Fact]
    public async Task Rerun_reuses_failed_upstream_and_a_re_run_node_consumes_the_rebuilt_error_output()
    {
        // start → flaky(always fails) =(error)=> caught(reads {{flaky.error.message}}) → end ; flaky =(normal)=> ok.
        // The original routes the error branch. Rerun from "caught": flaky is REUSED as a failed source (the
        // NodeFailed seeder path), and caught RE-RUNS — so this proves the full rehydrate→BuildErrorOutput→
        // downstream-consume path: a re-run node reads the error output rebuilt from the reused failed cell.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var flakyKey = "rerun-failed-" + Guid.NewGuid().ToString("N");
        var workflowId = await CreateWorkflowAsync(teamId, userId, FailWithErrorEdgeDef(flakyKey));
        var originalRunId = await RunFreshAsync(workflowId, teamId);
        await AssertRunStatusAsync(originalRunId, WorkflowRunStatus.Success);
        (await LoadCellAsync(originalRunId, "flaky")).Status.ShouldBe(NodeStatus.Failure);
        var attemptsAfterOriginal = FlakyTestNode.AttemptsFor(flakyKey);

        var rerunId = await RerunAsync(originalRunId, "caught", teamId, userId);
        await RunEngineAsync(rerunId);

        await AssertRunStatusAsync(rerunId, WorkflowRunStatus.Success);
        (await LoadCellAsync(rerunId, "flaky")).Status.ShouldBe(NodeStatus.Failure,
            "the reused failed-with-error-edge upstream must be re-settled as a failed source");
        (await NodeStartedCountAsync(rerunId, "flaky")).ShouldBe(0, "the reused failed node did not re-run");
        FlakyTestNode.AttemptsFor(flakyKey).ShouldBe(attemptsAfterOriginal,
            "reuse must not re-invoke the failing node (attempt count unchanged from the original run)");
        (await NodeStartedCountAsync(rerunId, "caught")).ShouldBe(1, "the from-node re-ran exactly once");

        // The crux: the RE-RUN caught node resolved {{nodes.flaky.outputs.error.message}} from the error output
        // the engine REBUILT from the reused (failed) flaky cell — not from a stale copy.
        var caughtCell = await LoadCellAsync(rerunId, "caught");
        JsonDocument.Parse(caughtCell.OutputsJson).RootElement.GetProperty("msg").GetString()
            .ShouldContain("flaky failure", customMessage: "the re-run node must consume the rebuilt error output of the reused failed upstream");
    }

    [Fact]
    public async Task Rerun_reuses_completed_container_as_a_single_leaf_body_not_re_entered()
    {
        // start → loop(1 pass; body: loop_start → mutator) → transform → end. Rerun from "transform" keeps the
        // loop as ONE settled top-level leaf — its body (the side-effecting mutator inside) is never re-entered.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var bodyKey = "rerun-loopbody-" + Guid.NewGuid().ToString("N");
        MutatingProbeNode.Reset(bodyKey);

        var workflowId = await CreateWorkflowAsync(teamId, userId, LoopBodyDef(bodyKey));
        var originalRunId = await RunFreshAsync(workflowId, teamId);
        await AssertRunStatusAsync(originalRunId, WorkflowRunStatus.Success);
        MutatingProbeNode.ExecutionsFor(bodyKey).ShouldBe(1, "the loop body executed once originally");

        var rerunId = await RerunAsync(originalRunId, "transform", teamId, userId);
        await RunEngineAsync(rerunId);

        await AssertRunStatusAsync(rerunId, WorkflowRunStatus.Success);
        MutatingProbeNode.ExecutionsFor(bodyKey).ShouldBe(1,
            "a reused container is one settled leaf — the engine must NOT re-enter its body on the rerun");
        (await NodeStartedCountAsync(rerunId, "loop")).ShouldBe(0, "the reused container was not re-run at top level");
        (await NodeStartedCountAsync(rerunId, "transform")).ShouldBe(1, "the from-node re-ran exactly once");
    }

    [Fact]
    public async Task Rerun_from_the_root_node_re_runs_everything_like_a_replay()
    {
        // Rerun from the trigger (root): the closure is the WHOLE graph, the kept set is empty, nothing is
        // pre-seeded — the degenerate case that is equivalent to a frozen replay. Every node re-runs once.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowId = await CreateWorkflowAsync(teamId, userId, LinearEchoDef());
        var originalRunId = await RunFreshAsync(workflowId, teamId);
        await AssertRunStatusAsync(originalRunId, WorkflowRunStatus.Success);

        var rerunId = await RerunAsync(originalRunId, "start", teamId, userId);
        await RunEngineAsync(rerunId);

        await AssertRunStatusAsync(rerunId, WorkflowRunStatus.Success);
        foreach (var n in new[] { "start", "a", "end" })
            (await NodeStartedCountAsync(rerunId, n)).ShouldBe(1, $"rerun-from-root re-runs every node, including '{n}'");
    }

    // ─────────────────────────────────────────────────────────────────────────────
    //  B. Origin variants + frozen scope
    // ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Rerun_from_a_snapshot_origin_run_forks_the_inline_definition()
    {
        // A snapshot run (no Workflow row — inline frozen definition). Rerun must plan over the INLINE definition,
        // fork version-less, and carry the same frozen definition JSON.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var originalRunId = await StartSnapshotAsync(teamId, userId, LinearEchoDef());
        await RunEngineAsync(originalRunId);
        await AssertRunStatusAsync(originalRunId, WorkflowRunStatus.Success);

        var rerunId = await RerunAsync(originalRunId, "a", teamId, userId);
        await RunEngineAsync(rerunId);

        await AssertRunStatusAsync(rerunId, WorkflowRunStatus.Success);
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var original = await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == originalRunId);
        var rerun = await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == rerunId);
        var rerunRequest = await db.WorkflowRunRequest.AsNoTracking().SingleAsync(r => r.Id == rerun.RunRequestId);

        rerun.WorkflowId.ShouldBeNull("a snapshot-origin rerun stays version-less (no Workflow row)");
        rerun.DefinitionSnapshotJson.ShouldBe(original.DefinitionSnapshotJson, "the fork carries the original's frozen inline definition");
        rerunRequest.SourceType.ShouldBe(WorkflowRunSourceTypes.Rerun,
            "the snapshot-fork must carry source=rerun (the sourceType threaded into StageReplayFromSnapshotAsync)");
        (await NodeStartedCountAsync(rerunId, "start")).ShouldBe(0, "the snapshot-origin upstream was reused");
        (await NodeStartedCountAsync(rerunId, "a")).ShouldBe(1, "the from-node re-ran exactly once");
    }

    [Fact]
    public async Task Rerun_with_an_empty_original_snapshot_writes_the_replay_sentinel()
    {
        // The definitions here declare no wf/team variables, so the original snapshot is empty. The seeder must
        // write a Sys-scoped "__rerun__" sentinel variable so the engine takes the REPLAY (frozen) scope fork
        // even though there are no plain values to freeze — without it the engine would take the fresh path.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowId = await CreateWorkflowAsync(teamId, userId, LinearEchoDef());
        var originalRunId = await RunFreshAsync(workflowId, teamId);
        await AssertRunStatusAsync(originalRunId, WorkflowRunStatus.Success);

        var rerunId = await RerunAsync(originalRunId, "a", teamId, userId);

        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            var sentinel = await db.WorkflowRunVariable.AsNoTracking()
                .SingleOrDefaultAsync(v => v.RunId == rerunId && v.Scope == "Sys" && v.Name == "__rerun__");
            sentinel.ShouldNotBeNull("an empty original snapshot must seed the replay-path sentinel row");
        }

        await RunEngineAsync(rerunId);
        await AssertRunStatusAsync(rerunId, WorkflowRunStatus.Success);
    }

    [Fact]
    public async Task Rerun_reads_a_re_run_nodes_plain_variable_from_the_frozen_snapshot_not_live()
    {
        // start → a(echoes {{team.REGION}}) → b → end. The original snapshots REGION. We then MUTATE the live
        // team variable and rerun from "a" (so "a" RE-RUNS and reads REGION again). The fork must read the FROZEN
        // snapshot value, not the mutated live one — the replay-scope contract for a re-run node.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        await SetTeamVarAsync(teamId, userId, "REGION", "us-east-1");

        var workflowId = await CreateWorkflowAsync(teamId, userId, EchoTeamVarDef("REGION"));
        var originalRunId = await RunFreshAsync(workflowId, teamId);
        await AssertRunStatusAsync(originalRunId, WorkflowRunStatus.Success);

        await SetTeamVarAsync(teamId, userId, "REGION", "eu-west-2");   // rotate AFTER the original

        var rerunId = await RerunAsync(originalRunId, "a", teamId, userId);
        await RunEngineAsync(rerunId);

        await AssertRunStatusAsync(rerunId, WorkflowRunStatus.Success);
        var aCell = await LoadCellAsync(rerunId, "a");
        JsonDocument.Parse(aCell.OutputsJson).RootElement.GetProperty("v").GetString()
            .ShouldBe("us-east-1", "a re-run node must read the FROZEN snapshot value, not the mutated live variable");
    }

    // ─────────────────────────────────────────────────────────────────────────────
    //  C. Lineage, atomic pre-seed, original immutability
    // ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Rerun_stamps_parent_causation_and_rerun_source_type()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowId = await CreateWorkflowAsync(teamId, userId, LinearEchoDef());
        var originalRunId = await RunFreshAsync(workflowId, teamId);

        Guid originalRequestId;
        using (var scope = _fixture.BeginScope())
            originalRequestId = await scope.Resolve<CodeSpaceDbContext>().WorkflowRun.AsNoTracking()
                .Where(r => r.Id == originalRunId).Select(r => r.RunRequestId).SingleAsync();

        var rerunId = await RerunAsync(originalRunId, "a", teamId, userId);

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();
        var rerun = await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == rerunId);
        var request = await db.WorkflowRunRequest.AsNoTracking().SingleAsync(r => r.Id == rerun.RunRequestId);

        rerun.ParentRunId.ShouldBe(originalRunId, "rerun lineage: fork parents the original run");
        rerun.RootRunId.ShouldBe(originalRunId, "the snapshot-path fork inherits the lineage root (the original, a first-time run) — so they collapse to one Runs-index entry");
        rerun.RerunFromNodeId.ShouldBe("a", "the fork records the node it re-ran from — drives the per-node rerun history");
        request.SourceType.ShouldBe(WorkflowRunSourceTypes.Rerun, "the fork request is tagged source=rerun");
        request.CausationId.ShouldBe(originalRequestId, "rerun lineage: the request links back to the original's request");
    }

    [Fact]
    public async Task Rerun_pre_seeds_kept_cells_as_terminal_only_records_before_the_engine_runs()
    {
        // Atomicity + shape of the pre-seed: BEFORE the fork is walked, the kept cells already project through the
        // workflow_run_node view as settled (Success) with ZERO node.started records (terminal-only re-emit), and
        // the closure nodes have no cell yet. The whole stage+seed lands in one transaction (the run is dispatchable).
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowId = await CreateWorkflowAsync(teamId, userId, LinearEchoDef());
        var originalRunId = await RunFreshAsync(workflowId, teamId);

        var rerunId = await RerunAsync(originalRunId, "a", teamId, userId);   // staged + dispatched, NOT yet walked

        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var startCell = await db.WorkflowRunNode.AsNoTracking().SingleAsync(n => n.RunId == rerunId && n.NodeId == "start" && n.IterationKey == "");
        startCell.Status.ShouldBe(NodeStatus.Success, "the kept upstream cell is pre-seeded as settled before the walk");
        (await NodeStartedCountAsync(rerunId, "start")).ShouldBe(0, "the pre-seed is terminal-only — no node.started record");

        var closureCellExists = await db.WorkflowRunNode.AsNoTracking().AnyAsync(n => n.RunId == rerunId && n.NodeId == "a" && n.IterationKey == "");
        closureCellExists.ShouldBeFalse("the from-node is NOT pre-seeded — it re-runs from scratch");

        var run = await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == rerunId);
        run.Status.ShouldBeOneOf(WorkflowRunStatus.Enqueued, WorkflowRunStatus.Pending);
    }

    [Fact]
    public async Task Rerun_never_mutates_the_original_run()
    {
        // The fork is pure-additive: the original run's cells, its records, and a side-effecting node's counter
        // must all be byte-identical after a rerun (the seeder INSERTs onto the fork, never touches the parent).
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var probeKey = "rerun-immut-" + Guid.NewGuid().ToString("N");
        MutatingProbeNode.Reset(probeKey);

        var workflowId = await CreateWorkflowAsync(teamId, userId, MutatorTransformDef(probeKey));
        var originalRunId = await RunFreshAsync(workflowId, teamId);

        int originalRecordCount = await RecordCountAsync(originalRunId);
        int originalCellCount = await CellCountAsync(originalRunId);

        var rerunId = await RerunAsync(originalRunId, "transform", teamId, userId);
        await RunEngineAsync(rerunId);
        await AssertRunStatusAsync(rerunId, WorkflowRunStatus.Success);

        (await RecordCountAsync(originalRunId)).ShouldBe(originalRecordCount, "the original run's ledger is untouched by a rerun");
        (await CellCountAsync(originalRunId)).ShouldBe(originalCellCount, "the original run's node cells are untouched by a rerun");
        MutatingProbeNode.ExecutionsFor(probeKey).ShouldBe(1, "the original's side effect is not re-fired by the fork");
    }

    // ─────────────────────────────────────────────────────────────────────────────
    //  D. Fail-closed gates — each asserts NOTHING was written
    // ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Rerun_for_a_run_in_another_team_throws_not_found_and_writes_nothing()
    {
        var (teamA, userA) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var (teamB, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowId = await CreateWorkflowAsync(teamA, userA, LinearEchoDef());
        var originalRunId = await RunFreshAsync(workflowId, teamA);

        var before = await RunCountAsync(teamB);
        await Should.ThrowAsync<KeyNotFoundException>(async () =>
            await RerunAsync(originalRunId, "a", teamB, userA));   // teamB does not own the run → 404-conflated

        (await RunCountAsync(teamB)).ShouldBe(before, "a cross-team rerun must write nothing");
    }

    [Fact]
    public async Task Rerun_from_an_unknown_node_throws_target_not_found_and_writes_nothing()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowId = await CreateWorkflowAsync(teamId, userId, LinearEchoDef());
        var originalRunId = await RunFreshAsync(workflowId, teamId);

        var before = await RunCountAsync(teamId);
        await Should.ThrowAsync<RerunTargetNotFoundException>(async () =>
            await RerunAsync(originalRunId, "does-not-exist", teamId, userId));

        (await RunCountAsync(teamId)).ShouldBe(before, "an unknown from-node must write nothing");
    }

    // ─────────────────────────────────────────────────────────────────────────────
    //  D7-3 — a re-run side-effecting node is approval-gated (not silently re-fired)
    // ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Rerun_gates_a_re_run_side_effecting_node_until_approved_then_fires_exactly_once()
    {
        // CROWN JEWEL. start → mutator(side-effecting) → transform(pure) → end. Rerun FROM the mutator: it is in
        // the re-run closure, so the engine PARKS it on an Approval wait — the side effect does NOT fire while
        // the run is Suspended (counter unchanged). On approve, the mutator runs exactly once (counter +1), the
        // pure transform re-runs un-gated, and the run reaches Success.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var probeKey = "rerun-gate-approve-" + Guid.NewGuid().ToString("N");
        MutatingProbeNode.Reset(probeKey);

        var workflowId = await CreateWorkflowAsync(teamId, userId, MutatorTransformDef(probeKey));
        var originalRunId = await RunFreshAsync(workflowId, teamId);
        await AssertRunStatusAsync(originalRunId, WorkflowRunStatus.Success);
        MutatingProbeNode.ExecutionsFor(probeKey).ShouldBe(1, "the original run fired the side effect once");

        var rerunId = await RerunAsync(originalRunId, "mutator", teamId, userId);
        await RunEngineAsync(rerunId);

        await AssertRunStatusAsync(rerunId, WorkflowRunStatus.Suspended);
        MutatingProbeNode.ExecutionsFor(probeKey).ShouldBe(1,
            "the re-run side-effecting node MUST NOT fire while parked on the approval gate (would be 2 if it re-fired silently)");
        (await PendingApprovalWaitCountAsync(rerunId)).ShouldBe(1, "the gate parks exactly one Approval wait");
        (await NodeStartedCountAsync(rerunId, "transform")).ShouldBe(0, "downstream of the gate has not run yet");

        (await ApproveRerunGateAsync(rerunId, teamId, userId, approved: true)).ShouldBeTrue();
        await RunEngineAsync(rerunId);

        await AssertRunStatusAsync(rerunId, WorkflowRunStatus.Success);
        MutatingProbeNode.ExecutionsFor(probeKey).ShouldBe(2,
            "after approval the side effect fires EXACTLY once on the rerun (suspend+resume must not double-fire)");
        (await TerminalRecordCountAsync(rerunId, "mutator")).ShouldBe(1, "the gated node has exactly one terminal record after the walk");
        (await NodeStartedCountAsync(rerunId, "transform")).ShouldBe(1, "the pure downstream re-ran once, un-gated, after approval");
    }

    [Fact]
    public async Task Rerun_rejected_side_effect_gate_skips_the_node_and_never_fires()
    {
        // Reject the gate → the side-effecting node is SKIPPED (its effect never fires) and its downstream, having
        // no other live input, is skipped too. The run still completes (a rejected gate is a clean outcome).
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var probeKey = "rerun-gate-reject-" + Guid.NewGuid().ToString("N");
        MutatingProbeNode.Reset(probeKey);

        var workflowId = await CreateWorkflowAsync(teamId, userId, MutatorTransformDef(probeKey));
        var originalRunId = await RunFreshAsync(workflowId, teamId);
        MutatingProbeNode.ExecutionsFor(probeKey).ShouldBe(1);

        var rerunId = await RerunAsync(originalRunId, "mutator", teamId, userId);
        await RunEngineAsync(rerunId);
        await AssertRunStatusAsync(rerunId, WorkflowRunStatus.Suspended);

        (await ApproveRerunGateAsync(rerunId, teamId, userId, approved: false)).ShouldBeTrue();
        await RunEngineAsync(rerunId);

        await AssertRunStatusAsync(rerunId, WorkflowRunStatus.Success);
        MutatingProbeNode.ExecutionsFor(probeKey).ShouldBe(1, "a rejected gate must NEVER fire the side effect");
        (await LoadCellAsync(rerunId, "mutator")).Status.ShouldBe(NodeStatus.Skipped, "a rejected side-effecting node is skipped");
        (await LoadCellAsync(rerunId, "transform")).Status.ShouldBe(NodeStatus.Skipped, "downstream with no other live input is skipped");
        (await NodeStartedCountAsync(rerunId, "transform")).ShouldBe(0, "the skipped downstream never started — the reject genuinely propagated, not silently ran");
    }

    [Fact]
    public async Task Side_effecting_node_runs_without_a_gate_on_a_normal_run()
    {
        // Control: the gate is RERUN-ONLY. A normal (manual) run fires the side-effecting node straight away — it
        // never suspends and stages no Approval wait. This pins that the gate does not over-fire on first runs.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var probeKey = "rerun-gate-normal-" + Guid.NewGuid().ToString("N");
        MutatingProbeNode.Reset(probeKey);

        var workflowId = await CreateWorkflowAsync(teamId, userId, MutatorTransformDef(probeKey));
        var runId = await RunFreshAsync(workflowId, teamId);

        await AssertRunStatusAsync(runId, WorkflowRunStatus.Success);
        MutatingProbeNode.ExecutionsFor(probeKey).ShouldBe(1, "a normal run fires the side effect without a gate");
        (await PendingApprovalWaitCountAsync(runId)).ShouldBe(0, "a normal run stages no rerun approval gate");
    }

    [Fact]
    public async Task Rerun_does_not_gate_a_reused_upstream_side_effecting_node()
    {
        // A side-effecting node that is REUSED (upstream of the from-node) is pre-seeded + settled, so it never
        // executes — and therefore never trips the gate. Rerun FROM "transform" (downstream of the mutator):
        // the run completes straight to Success with no approval wait, and the reused mutator never re-fires.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var probeKey = "rerun-gate-reused-" + Guid.NewGuid().ToString("N");
        MutatingProbeNode.Reset(probeKey);

        var workflowId = await CreateWorkflowAsync(teamId, userId, MutatorTransformDef(probeKey));
        var originalRunId = await RunFreshAsync(workflowId, teamId);

        var rerunId = await RerunAsync(originalRunId, "transform", teamId, userId);
        await RunEngineAsync(rerunId);

        await AssertRunStatusAsync(rerunId, WorkflowRunStatus.Success, "a reused side-effecting upstream creates no gate");
        (await PendingApprovalWaitCountAsync(rerunId)).ShouldBe(0, "no gate is staged for a reused (never-executed) side-effecting node");
        MutatingProbeNode.ExecutionsFor(probeKey).ShouldBe(1, "the reused side-effecting node did not re-fire");
        (await NodeStartedCountAsync(rerunId, "mutator")).ShouldBe(0, "the reused side-effecting node did not re-run");
    }

    [Fact]
    public async Task Rerun_gates_each_re_run_side_effecting_node_independently_one_approval_at_a_time()
    {
        // start → fork(pure) → {a(side-effecting), b(side-effecting)} → join(pure) → end. Rerun FROM the fork puts
        // BOTH side-effecting arms in the closure → TWO independent gates. They resolve one approval at a time
        // (the oldest-pending rule); after both, each fired exactly once and the run completes.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var keyA = "rerun-gate-parA-" + Guid.NewGuid().ToString("N");
        var keyB = "rerun-gate-parB-" + Guid.NewGuid().ToString("N");
        MutatingProbeNode.Reset(keyA);
        MutatingProbeNode.Reset(keyB);

        var workflowId = await CreateWorkflowAsync(teamId, userId, TwoSideEffectDiamondDef(keyA, keyB));
        var originalRunId = await RunFreshAsync(workflowId, teamId);
        MutatingProbeNode.ExecutionsFor(keyA).ShouldBe(1);
        MutatingProbeNode.ExecutionsFor(keyB).ShouldBe(1);

        var rerunId = await RerunAsync(originalRunId, "fork", teamId, userId);
        await RunEngineAsync(rerunId);

        await AssertRunStatusAsync(rerunId, WorkflowRunStatus.Suspended);
        (await PendingApprovalWaitCountAsync(rerunId)).ShouldBe(2, "both side-effecting arms park independent gates");

        // First approval resolves the oldest gate; the run is still parked on the second.
        (await ApproveRerunGateAsync(rerunId, teamId, userId, approved: true)).ShouldBeTrue();
        await RunEngineAsync(rerunId);
        await AssertRunStatusAsync(rerunId, WorkflowRunStatus.Suspended, "still parked on the second gate");
        (await PendingApprovalWaitCountAsync(rerunId)).ShouldBe(1);
        // EXACTLY ONE arm advanced (which one is wave-order-dependent, so assert the sum): one fired (→2), the
        // other is still gated (→1). This would fail if approving one gate wrongly released BOTH arms.
        (MutatingProbeNode.ExecutionsFor(keyA) + MutatingProbeNode.ExecutionsFor(keyB)).ShouldBe(3,
            "one arm fired on the first approval; the other is still parked on its independent gate");

        (await ApproveRerunGateAsync(rerunId, teamId, userId, approved: true)).ShouldBeTrue();
        await RunEngineAsync(rerunId);

        await AssertRunStatusAsync(rerunId, WorkflowRunStatus.Success);
        MutatingProbeNode.ExecutionsFor(keyA).ShouldBe(2, "arm A fired exactly once on the rerun");
        MutatingProbeNode.ExecutionsFor(keyB).ShouldBe(2, "arm B fired exactly once on the rerun");
    }

    [Fact]
    public async Task Rerun_gate_re_walk_before_approval_does_not_fire_and_keeps_exactly_one_wait()
    {
        // Durability crown: a spurious re-dispatch (reconciler / duplicate worker) that re-walks the fork BEFORE
        // the operator approves must be a safe idempotent no-op — the side effect stays un-fired and the run
        // re-parks on a single Approval wait (the prior pending wait is replaced, never duplicated). Only after a
        // real approval does it fire, exactly once.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var probeKey = "rerun-gate-rewalk-" + Guid.NewGuid().ToString("N");
        MutatingProbeNode.Reset(probeKey);

        var workflowId = await CreateWorkflowAsync(teamId, userId, MutatorTransformDef(probeKey));
        var originalRunId = await RunFreshAsync(workflowId, teamId);

        var rerunId = await RerunAsync(originalRunId, "mutator", teamId, userId);
        await RunEngineAsync(rerunId);
        await AssertRunStatusAsync(rerunId, WorkflowRunStatus.Suspended);
        MutatingProbeNode.ExecutionsFor(probeKey).ShouldBe(1);

        // Simulate a spurious re-dispatch of the still-unapproved fork (the reconciler flips a stuck run back to
        // Enqueued) and re-walk — the gate must re-suspend without firing or leaking a second wait.
        await ForceEnqueuedAsync(rerunId);
        await RunEngineAsync(rerunId);

        await AssertRunStatusAsync(rerunId, WorkflowRunStatus.Suspended, "a re-walk before approval re-parks the gate");
        MutatingProbeNode.ExecutionsFor(probeKey).ShouldBe(1, "the unapproved re-walk MUST NOT fire the side effect");
        (await PendingApprovalWaitCountAsync(rerunId)).ShouldBe(1, "the re-suspend replaces the prior wait — never a duplicate");

        (await ApproveRerunGateAsync(rerunId, teamId, userId, approved: true)).ShouldBeTrue();
        await RunEngineAsync(rerunId);
        await AssertRunStatusAsync(rerunId, WorkflowRunStatus.Success);
        MutatingProbeNode.ExecutionsFor(probeKey).ShouldBe(2, "after approval the side effect fires exactly once despite the earlier spurious re-walk");
    }

    [Fact]
    public async Task Rerun_rejected_gate_skips_the_node_without_taking_its_error_edge()
    {
        // Semantics pin: a rejected gate SKIPS the node — it is NOT a failure, so a downstream error-branch
        // handler is NOT taken (a skip kills every handle, including 'error'). Reject ≠ error.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var probeKey = "rerun-gate-erroredge-" + Guid.NewGuid().ToString("N");
        MutatingProbeNode.Reset(probeKey);

        var workflowId = await CreateWorkflowAsync(teamId, userId, SideEffectWithErrorEdgeDef(probeKey));
        var originalRunId = await RunFreshAsync(workflowId, teamId);

        var rerunId = await RerunAsync(originalRunId, "mutator", teamId, userId);
        await RunEngineAsync(rerunId);
        await AssertRunStatusAsync(rerunId, WorkflowRunStatus.Suspended);

        (await ApproveRerunGateAsync(rerunId, teamId, userId, approved: false)).ShouldBeTrue();
        await RunEngineAsync(rerunId);

        await AssertRunStatusAsync(rerunId, WorkflowRunStatus.Success);
        MutatingProbeNode.ExecutionsFor(probeKey).ShouldBe(1, "a rejected gate never fires the side effect");
        (await LoadCellAsync(rerunId, "mutator")).Status.ShouldBe(NodeStatus.Skipped);
        (await LoadCellAsync(rerunId, "caught")).Status.ShouldBe(NodeStatus.Skipped,
            "a rejected (skipped) node is NOT a failure — its error edge is dead, so the handler is not taken");
        (await NodeStartedCountAsync(rerunId, "caught")).ShouldBe(0, "the error handler never ran on a reject");
    }

    [Fact]
    public async Task Rerun_with_a_side_effecting_AND_suspendable_node_in_the_closure_is_refused_and_writes_nothing()
    {
        // Precedence pin: a node that is BOTH side-effecting AND suspendable (the chat.post_message shape) is
        // refused at staging via the CanSuspend arm — fail-closed wins, it never reaches the runtime side-effect
        // gate. (No frontend / docs should imply such a node is approval-gated; this is the conservative path.)
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowId = await CreateWorkflowAsync(teamId, userId, BothFlagsDef());
        var originalRunId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);
        await RunEngineAsync(originalRunId);   // suspends at the both-flags node; the gate fires regardless

        var before = await RunCountAsync(teamId);
        var ex = await Should.ThrowAsync<RerunBlockedByUnsupportedNodeException>(async () =>
            await RerunAsync(originalRunId, "start", teamId, userId));
        ex.BlockedNodeIds.ShouldContain("bothflags", "a side-effecting+suspendable node is refused via the CanSuspend arm, not approval-gated");

        (await RunCountAsync(teamId)).ShouldBe(before, "a both-flags-blocked rerun must write nothing");
    }

    [Fact]
    public async Task Rerun_when_a_kept_upstream_did_not_settle_reusably_is_refused_and_writes_nothing()
    {
        // start → flaky(always fails, NO error edge) → end. The original FAILS at flaky; end never runs. Rerun
        // from "end" keeps flaky, which failed WITHOUT an error edge → no reusable outcome → hard refuse.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var flakyKey = "rerun-nonreuse-" + Guid.NewGuid().ToString("N");
        var workflowId = await CreateWorkflowAsync(teamId, userId, FailNoErrorEdgeDef(flakyKey));
        var originalRunId = await RunFreshAsync(workflowId, teamId);
        await AssertRunStatusAsync(originalRunId, WorkflowRunStatus.Failure);

        var before = await RunCountAsync(teamId);
        await Should.ThrowAsync<RerunUpstreamNotReusableException>(async () =>
            await RerunAsync(originalRunId, "end", teamId, userId));

        (await RunCountAsync(teamId)).ShouldBe(before, "a non-reusable-upstream rerun must write nothing");
    }

    [Fact]
    public async Task Rerun_when_a_kept_upstream_is_still_suspended_is_refused_and_writes_nothing()
    {
        // start → suspendprobe(parks an Action wait) → downstream → end. The original SUSPENDS at suspendprobe;
        // downstream never runs. Rerun from "downstream" keeps suspendprobe, whose cell is non-terminal
        // (Suspended) → no reusable outcome → hard refuse (the in-flight-upstream guard).
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var probeKey = "rerun-suspended-" + Guid.NewGuid().ToString("N");
        SuspendProbeNode.Reset(probeKey);

        var workflowId = await CreateWorkflowAsync(teamId, userId, SuspendThenDownstreamDef(probeKey));
        var originalRunId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);
        await RunEngineAsync(originalRunId);
        await AssertRunStatusAsync(originalRunId, WorkflowRunStatus.Suspended);

        var before = await RunCountAsync(teamId);
        await Should.ThrowAsync<RerunUpstreamNotReusableException>(async () =>
            await RerunAsync(originalRunId, "downstream", teamId, userId));

        (await RunCountAsync(teamId)).ShouldBe(before, "a rerun over an in-flight (suspended) kept upstream must write nothing");
    }

    // ─────────────────────────────────────────────────────────────────────────────
    //  C2 — continue a FAILED run IN PLACE (same run id): re-run the halting node where it died
    // ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Continue_a_failed_run_reruns_the_halting_node_in_place_and_reaches_terminal()
    {
        // C2 crown: start → flaky(fails its 1st attempt, NO error edge) → end. The original run HALTS at flaky → Failure;
        // end never runs. ContinueRunAsync revives it IN PLACE (same run id, NO fork): the halting node is reset + re-runs
        // (attempt 2 → succeeds), the run walks to Success. Contrast rerun-from-node, which forks a new run id.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var flakyKey = "c2-continue-" + Guid.NewGuid().ToString("N");   // a fresh GUID key starts its attempt counter at 0

        var workflowId = await CreateWorkflowAsync(teamId, userId, FailOnceNoErrorEdgeDef(flakyKey));
        var originalRunId = await RunFreshAsync(workflowId, teamId);
        await AssertRunStatusAsync(originalRunId, WorkflowRunStatus.Failure);
        FlakyTestNode.AttemptsFor(flakyKey).ShouldBe(1, "the original run failed at the flaky node on its first attempt");
        (await NodeStartedCountAsync(originalRunId, "end")).ShouldBe(0, "the terminal never ran — the run halted at the failure");

        var before = await RunCountAsync(teamId);
        (await ContinueAsync(originalRunId, teamId)).ShouldBeTrue("a Failure run with an unhandled-failed node continues in place");

        (await RunCountAsync(teamId)).ShouldBe(before, "continue-in-place forks NO new run — same run id (the whole point vs rerun-from-node)");

        await RunEngineAsync(originalRunId);

        await AssertRunStatusAsync(originalRunId, WorkflowRunStatus.Success, "the reset halting node re-ran (attempt 2, succeeds), downstream ran, the SAME run reached Success");
        FlakyTestNode.AttemptsFor(flakyKey).ShouldBe(2, "the halting node re-ran exactly once on the in-place continue");
        (await NodeStartedCountAsync(originalRunId, "end")).ShouldBe(1, "the previously-blocked terminal ran after the halting node recovered");
    }

    [Fact]
    public async Task Continue_a_failed_run_does_not_re_fire_a_succeeded_side_effecting_upstream()
    {
        // The safety property that makes in-place continue narrower + safer than a full replay: continue re-runs ONLY the
        // not-succeeded nodes. start → mutator(side-effecting, SUCCEEDS) → flaky(fails once) → end. The original: mutator
        // fires once (count 1), flaky halts the run. On continue ONLY flaky is reset + re-runs; the mutator's Success cell
        // is settled + REUSED, never re-fired — so its side-effect count stays 1 while the run completes.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var probeKey = "c2-noreheat-" + Guid.NewGuid().ToString("N");
        var flakyKey = "c2-noreheat-flaky-" + Guid.NewGuid().ToString("N");   // fresh GUID keys start their counters at 0
        MutatingProbeNode.Reset(probeKey);

        var workflowId = await CreateWorkflowAsync(teamId, userId, SideEffectThenFlakyDef(probeKey, flakyKey));
        var originalRunId = await RunFreshAsync(workflowId, teamId);
        await AssertRunStatusAsync(originalRunId, WorkflowRunStatus.Failure);
        MutatingProbeNode.ExecutionsFor(probeKey).ShouldBe(1, "the side-effecting upstream fired once on the original run");

        (await ContinueAsync(originalRunId, teamId)).ShouldBeTrue();
        await RunEngineAsync(originalRunId);

        await AssertRunStatusAsync(originalRunId, WorkflowRunStatus.Success);
        MutatingProbeNode.ExecutionsFor(probeKey).ShouldBe(1, "the succeeded side-effecting upstream was REUSED, never re-fired — only the failed node re-ran");
        // In-place continue keeps the SAME run id, so the mutator's ORIGINAL node.started stays in the ledger — the proof
        // it wasn't re-run is that the count stays at 1 (a re-run would add a second node.started → 2).
        (await NodeStartedCountAsync(originalRunId, "mutator")).ShouldBe(1, "the reused upstream started once (originally) and was NOT re-started on the in-place continue");
    }

    [Fact]
    public async Task Continue_a_failed_run_resets_every_parallel_halting_node_and_completes()
    {
        // Multi-failure: start → {flakyA, flakyB}(both fail their 1st attempt, NO error edge) → end. BOTH branches fail in
        // one ready wave → the run halts with TWO unhandled-failed top-level cells. Continue must reset BOTH (not just the
        // first) so the re-walk re-runs both (attempt 2 → succeed) and the run reaches Success — pins that toReset covers
        // every parallel failure, not one.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var keyA = "c2-diamondA-" + Guid.NewGuid().ToString("N");
        var keyB = "c2-diamondB-" + Guid.NewGuid().ToString("N");

        var workflowId = await CreateWorkflowAsync(teamId, userId, FailOnceDiamondDef(keyA, keyB));
        var originalRunId = await RunFreshAsync(workflowId, teamId);
        await AssertRunStatusAsync(originalRunId, WorkflowRunStatus.Failure);
        FlakyTestNode.AttemptsFor(keyA).ShouldBe(1, "branch A failed on its first attempt");
        FlakyTestNode.AttemptsFor(keyB).ShouldBe(1, "branch B failed on its first attempt");

        var before = await RunCountAsync(teamId);
        (await ContinueAsync(originalRunId, teamId)).ShouldBeTrue("a Failure run with multiple unhandled-failed nodes continues in place");
        (await RunCountAsync(teamId)).ShouldBe(before, "continue-in-place forks no new run");

        await RunEngineAsync(originalRunId);

        await AssertRunStatusAsync(originalRunId, WorkflowRunStatus.Success, "BOTH reset branches re-ran and succeeded → the run completed");
        FlakyTestNode.AttemptsFor(keyA).ShouldBe(2, "branch A re-ran exactly once on the continue");
        FlakyTestNode.AttemptsFor(keyB).ShouldBe(2, "branch B re-ran exactly once on the continue");
    }

    [Fact]
    public async Task Rerun_with_a_container_in_the_closure_is_refused_and_writes_nothing()
    {
        // A Map/Loop/Try container in the re-run closure is refused by KIND — re-running a container re-runs its
        // whole body atomically, which isn't supported yet (the D7-4 container-rerun slice). Here the loop body
        // is PURE (json_emit) yet the loop is still refused — this PINS that conservatism so a future
        // body-effectfulness relaxation is a deliberate, test-visible change.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowId = await CreateWorkflowAsync(teamId, userId, PureBodiedLoopDef());
        var originalRunId = await RunFreshAsync(workflowId, teamId);
        await AssertRunStatusAsync(originalRunId, WorkflowRunStatus.Success);

        var before = await RunCountAsync(teamId);
        var ex = await Should.ThrowAsync<RerunBlockedByUnsupportedNodeException>(async () =>
            await RerunAsync(originalRunId, "start", teamId, userId));   // closure includes the loop container
        ex.BlockedNodeIds.ShouldContain("loop", "a container in the closure is refused by Kind (rerun-unsupported)");

        (await RunCountAsync(teamId)).ShouldBe(before, "a container-blocked rerun must write nothing");
    }

    [Fact]
    public async Task Rerun_with_a_suspendable_node_in_the_closure_is_refused_and_writes_nothing()
    {
        // A CanSuspend node in the closure is rerun-unsupported (re-running a parking node re-stages an external
        // wait / agent run). start → suspendprobe(CanSuspend) → end; rerun from "start" puts suspendprobe in the
        // closure → refused (distinct from a SIDE-EFFECTING node, which is approval-gated rather than refused).
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var probeKey = "rerun-cansuspend-" + Guid.NewGuid().ToString("N");
        SuspendProbeNode.Reset(probeKey);

        var workflowId = await CreateWorkflowAsync(teamId, userId, SuspendInlineDef(probeKey));
        var originalRunId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);
        await RunEngineAsync(originalRunId);   // suspends; the gate fires regardless of the original's state

        var before = await RunCountAsync(teamId);
        var ex = await Should.ThrowAsync<RerunBlockedByUnsupportedNodeException>(async () =>
            await RerunAsync(originalRunId, "start", teamId, userId));
        ex.BlockedNodeIds.ShouldContain("suspendprobe", "a CanSuspend node in the closure is refused (rerun-unsupported)");

        (await RunCountAsync(teamId)).ShouldBe(before, "a suspend-blocked rerun must write nothing");
    }

    [Fact]
    public async Task Rerun_from_an_agent_code_root_with_an_unsupported_downstream_is_refused_on_the_downstream_not_the_root()
    {
        // P2.2 made agent.run an ADMITTED from-node root. A from-node ROOT re-walks its WHOLE forward closure, so an
        // unsupported node DOWNSTREAM of the admitted root must STILL refuse the rerun (the admission is surgical, not
        // a hole). start → agent.run(a) → suspendprobe(b) → end; rerun from "a" scans the closure {a, b, end}. The
        // discriminator proves BOTH halves at once: BlockedNodeIds holds "b" (the gate still does its job downstream)
        // but NOT "a" — the agent.run root is admitted. That ShouldNotContain("a") is the load-bearing control:
        // revert the one-line P2.2 flip and "a" reappears here (agent.run refused as a root), failing this test.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var probeKey = "rerun-agentroot-unsupported-" + Guid.NewGuid().ToString("N");
        SuspendProbeNode.Reset(probeKey);

        var jobClient = ResolveJobClient();
        jobClient.Clear();
        jobClient.AutoExecute = false;   // park agent.run(a) cleanly; never run the binary-less harness

        try
        {
            var workflowId = await CreateWorkflowAsync(teamId, userId, AgentThenSuspendProbeDef(probeKey));
            var originalRunId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);
            await RunEngineAsync(originalRunId);   // suspends parked on agent.run(a)'s AgentRun wait
            await AssertRunStatusAsync(originalRunId, WorkflowRunStatus.Suspended);

            var before = await RunCountAsync(teamId);
            var ex = await Should.ThrowAsync<RerunBlockedByUnsupportedNodeException>(async () =>
                await RerunAsync(originalRunId, "a", teamId, userId));   // "a" = the admitted agent.run root

            ex.BlockedNodeIds.ShouldContain("b", "the unsupported suspendprobe DOWNSTREAM of the admitted agent.run root still refuses the rerun");
            ex.BlockedNodeIds.ShouldNotContain("a", "the agent.run root is ADMITTED (P2.2) — revert the flip and it would be blocked here too: the load-bearing control");

            (await RunCountAsync(teamId)).ShouldBe(before, "a downstream-blocked rerun must write nothing");
        }
        finally
        {
            jobClient.AutoExecute = true;   // restore the shared fixture's default — never poison later tests in the collection
        }
    }

    [Fact]
    public async Task Rerun_from_a_sleep_node_re_arms_a_fresh_timer_on_the_fork_then_completes()
    {
        // D3 made flow.sleep an ADMITTED from-node root — the last CanSuspend node whose re-execution is a clean
        // re-stage (its "external run" is just the engine's OWN self-woken Timer, keyed to the run id). start →
        // sleep(60s) → end. The original suspends on the timer, resumes to Success. Rerun FROM "sleep": the fork
        // RE-EXECUTES the sleep, parking a FRESH Timer wait keyed to the FORK's run id (a fresh wait row) while the
        // original's resolved wait is untouched; resolving the fork's wait drives it to Success. Proves the admission
        // is real (no 422), the timer is self-contained per-run, and the original is immutable.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowId = await CreateWorkflowAsync(teamId, userId, SleepThenEndDef(seconds: 60));
        var originalRunId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);

        await RunEngineAsync(originalRunId);                       // original parks on the sleep timer
        await AssertRunStatusAsync(originalRunId, WorkflowRunStatus.Suspended, "the original parks on the sleep timer");
        (await ResolvePendingWaitAsync(originalRunId)).ShouldBeTrue("resolving the original's timer wait succeeds");
        await RunEngineAsync(originalRunId);                       // original resumes → Success
        await AssertRunStatusAsync(originalRunId, WorkflowRunStatus.Success, "the original completes so it is rerunnable");

        var rerunId = await RerunAsync(originalRunId, "sleep", teamId, userId);   // flow.sleep admitted as a from-node root
        await RunEngineAsync(rerunId);

        await AssertRunStatusAsync(rerunId, WorkflowRunStatus.Suspended, "the fork re-executes the sleep and parks on a FRESH timer");
        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();

            var forkWait = await db.WorkflowRunWait.AsNoTracking().SingleAsync(w => w.RunId == rerunId);
            forkWait.NodeId.ShouldBe("sleep");
            forkWait.WaitKind.ShouldBe(WorkflowWaitKinds.Timer, "the re-staged wait is a fresh self-woken Timer, not an external-signal wait");
            forkWait.Status.ShouldBe(WorkflowWaitStatuses.Pending);
            forkWait.RunId.ShouldBe(rerunId, "the fresh timer is keyed to the FORK's run id — self-contained, no external re-issue");

            var fork = await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == rerunId);
            fork.ParentRunId.ShouldBe(originalRunId, "the rerun fork parents the original (rerun lineage)");

            (await db.WorkflowRunWait.AsNoTracking().SingleAsync(w => w.RunId == originalRunId)).Status
                .ShouldBe(WorkflowWaitStatuses.Resolved, "the original run's timer wait is untouched by the fork");
            (await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == originalRunId)).Status
                .ShouldBe(WorkflowRunStatus.Success, "the original run is immutable");
        }

        (await NodeStartedCountAsync(rerunId, "sleep")).ShouldBe(1, "the sleep node re-ran exactly once on the fork");
        (await NodeStartedCountAsync(rerunId, "start")).ShouldBe(0, "the upstream trigger was reused, not re-run");

        (await ResolvePendingWaitAsync(rerunId)).ShouldBeTrue("resolving the fork's fresh timer succeeds");
        await RunEngineAsync(rerunId);

        await AssertRunStatusAsync(rerunId, WorkflowRunStatus.Success, "the fork completes after its own timer resolves");
        (await NodeStartedCountAsync(rerunId, "end")).ShouldBe(1, "the previously-blocked terminal re-ran on the fork");
    }

    [Fact]
    public async Task The_run_detail_RerunnableFromHere_flag_matches_what_the_endpoint_accepts_per_node()
    {
        // P1.1 honest gating: the run-detail RerunnableFromHere flag is computed by the SAME gate the rerun endpoint
        // enforces, so flag==true ⇔ POST /rerun-from-node ACCEPTS, flag==false ⇔ it refuses (422). Proven WIRE-HONEST
        // on PureBodiedLoopDef (start → loop(container) → end): the loop is rerun-unsupported by Kind, so its closure
        // poisons start + loop (false), while a from-"end" rerun (closure = {end}) is clean (true). The UI gates the
        // "Rerun from here" button on this flag, so it can never offer a button that 422s.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowId = await CreateWorkflowAsync(teamId, userId, PureBodiedLoopDef());
        var originalRunId = await RunFreshAsync(workflowId, teamId);
        await AssertRunStatusAsync(originalRunId, WorkflowRunStatus.Success);

        using (var scope = _fixture.BeginScope())
        {
            var detail = await scope.Resolve<IWorkflowService>().GetRunAsync(originalRunId, teamId, CancellationToken.None);
            detail.ShouldNotBeNull();

            bool Flag(string nodeId) => detail!.Nodes.Single(n => n.NodeId == nodeId && n.IterationKey == WorkflowIterationKeys.TopLevel).RerunnableFromHere;

            Flag("end").ShouldBeTrue("a from-node rerun of the terminal (closure = {end}) is accepted");
            Flag("start").ShouldBeFalse("start's closure includes the loop container → the endpoint would refuse");
            Flag("loop").ShouldBeFalse("the loop container itself is rerun-unsupported by Kind");

            detail!.Nodes.Where(n => n.IterationKey != WorkflowIterationKeys.TopLevel)
                .ShouldAllBe(n => !n.RerunnableFromHere, "an iterated (container-body) row is never a from-node rerun target");
        }

        // Correlate the flag with the REAL endpoint per node — the wire-honest proof the UI can trust the flag.
        var rerunId = await RerunAsync(originalRunId, "end", teamId, userId);   // flag==true → ACCEPTED
        await RunEngineAsync(rerunId);
        await AssertRunStatusAsync(rerunId, WorkflowRunStatus.Success);

        var blocked = await Should.ThrowAsync<RerunBlockedByUnsupportedNodeException>(async () =>
            await RerunAsync(originalRunId, "start", teamId, userId));          // flag==false → refused
        blocked.BlockedNodeIds.ShouldContain("loop", "the flag==false node is exactly the one the endpoint blocks on");
    }

    [Fact]
    public async Task The_flag_is_false_for_a_suspendable_node_and_its_upstream_matching_the_endpoint_refusal()
    {
        // The fail-closed CanSuspend ARM — the supervisor / un-opted suspendable class (every CanSuspend node EXCEPT
        // the agent.run re-stage opt-in, which P2.2 admits). A SuspendProbe (CanSuspend, IsRerunnableWhenSuspendable
        // unset → RefuseSuspendable, like a supervisor) and ANYTHING upstream of it are not from-node rerunnable, so the
        // flag is false for both — matching the endpoint's RerunBlockedByUnsupportedNodeException.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var probeKey = "rerun-flag-cansuspend-" + Guid.NewGuid().ToString("N");
        SuspendProbeNode.Reset(probeKey);

        var workflowId = await CreateWorkflowAsync(teamId, userId, SuspendInlineDef(probeKey));
        var originalRunId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);
        await RunEngineAsync(originalRunId);
        await AssertRunStatusAsync(originalRunId, WorkflowRunStatus.Suspended);

        using var scope = _fixture.BeginScope();
        var detail = await scope.Resolve<IWorkflowService>().GetRunAsync(originalRunId, teamId, CancellationToken.None);
        detail.ShouldNotBeNull();

        bool Flag(string nodeId) => detail!.Nodes.Single(n => n.NodeId == nodeId && n.IterationKey == WorkflowIterationKeys.TopLevel).RerunnableFromHere;

        Flag("suspendprobe").ShouldBeFalse("an un-opted CanSuspend node is never a from-node rerun target (the supervisor class; agent.run opts in via P2.2)");
        Flag("start").ShouldBeFalse("start's closure includes the suspendable node → the endpoint would refuse");
    }

    [Fact]
    public async Task The_flag_is_false_when_a_kept_upstream_sibling_did_not_settle_reusably_matching_the_endpoint_422()
    {
        // Gate (c) — the run-STATE-dependent kept-upstream-reusability refusal (RerunUpstreamNotReusableException), the
        // 3rd of the endpoint's 3 fail-closed gates. A failing diamond start → {flakyA, flakyB} → end: both siblings
        // fail in one ready wave. flakyB's CLOSURE {flakyB, end} is structurally clean (gate b passes), but a
        // rerun-from-flakyB KEEPS flakyA, which is Failure-without-error-edge → not reusable → the endpoint 422s. The
        // flag must model this and read FALSE, so the UI never offers a button that 422s. (Rerun-from-start keeps
        // nothing → accepted, so start stays true.)
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var keyA = "diamond-A-" + Guid.NewGuid().ToString("N");
        var keyB = "diamond-B-" + Guid.NewGuid().ToString("N");
        var workflowId = await CreateWorkflowAsync(teamId, userId, FailingDiamondDef(keyA, keyB));
        var originalRunId = await RunFreshAsync(workflowId, teamId);
        await AssertRunStatusAsync(originalRunId, WorkflowRunStatus.Failure);

        using (var scope = _fixture.BeginScope())
        {
            var detail = await scope.Resolve<IWorkflowService>().GetRunAsync(originalRunId, teamId, CancellationToken.None);
            detail.ShouldNotBeNull();

            bool Flag(string nodeId) => detail!.Nodes.Single(n => n.NodeId == nodeId && n.IterationKey == WorkflowIterationKeys.TopLevel).RerunnableFromHere;

            Flag("flakyB").ShouldBeFalse("rerun-from-flakyB keeps flakyA (Failure, no error edge) → not reusable, the endpoint would 422 — the flag must model gate (c), not just the closure");
            Flag("flakyA").ShouldBeFalse("symmetric: rerun-from-flakyA keeps flakyB, also non-reusable");
            Flag("start").ShouldBeTrue("rerun-from-start keeps nothing (a full replay) → accepted");
        }

        // The wire-honest correlation for gate (c): the flag==false node is exactly the one the endpoint 422s on.
        var blocked = await Should.ThrowAsync<RerunUpstreamNotReusableException>(async () =>
            await RerunAsync(originalRunId, "flakyB", teamId, userId));
        blocked.Message.ShouldContain("flakyA", Case.Insensitive);
    }

    // ─────────────────────────────────────────────────────────────────────────────
    //  Workflow definition builders
    // ─────────────────────────────────────────────────────────────────────────────

    // start → {flakyA, flakyB}(both always fail, no error edge) → end. A failing diamond: rerunning from one failed
    // sibling KEEPS the other (non-reusable) → the endpoint's kept-upstream-reusability gate refuses it.
    private static WorkflowDefinition FailingDiamondDef(string keyA, string keyB) => new()
    {
        SchemaVersion = 1,
        Nodes = new List<NodeDefinition>
        {
            new() { Id = "start", TypeKey = "trigger.manual", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "flakyA", TypeKey = FlakyTestNode.Key, Config = WorkflowsTestSeed.Json($$"""{"key":"{{keyA}}","failTimes":99}"""), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "flakyB", TypeKey = FlakyTestNode.Key, Config = WorkflowsTestSeed.Json($$"""{"key":"{{keyB}}","failTimes":99}"""), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "end", TypeKey = "builtin.terminal", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
        },
        Edges = new List<EdgeDefinition>
        {
            new() { From = "start", To = "flakyA" },
            new() { From = "start", To = "flakyB" },
            new() { From = "flakyA", To = "end" },
            new() { From = "flakyB", To = "end" },
        },
    };

    // start(manual) → mutator(side-effecting, counts) → transform(json_emit echoes mutator.n) → end(terminal).
    private static WorkflowDefinition MutatorTransformDef(string probeKey) => new()
    {
        SchemaVersion = 1,
        Nodes = new List<NodeDefinition>
        {
            new() { Id = "start", TypeKey = "trigger.manual", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "mutator", TypeKey = MutatingProbeNode.Key, Config = WorkflowsTestSeed.Json($$"""{"key":"{{probeKey}}"}"""), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "transform", TypeKey = JsonEmitNode.Key, Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.Json("""{"n":"{{nodes.mutator.outputs.n}}"}""") },
            new() { Id = "end", TypeKey = "builtin.terminal", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
        },
        Edges = new List<EdgeDefinition>
        {
            new() { From = "start", To = "mutator" },
            new() { From = "mutator", To = "transform" },
            new() { From = "transform", To = "end" },
        },
    };

    // start → sleep(seconds) → end. The suspend/resume node whose re-execution is a clean self-woken re-stage (D3).
    private static WorkflowDefinition SleepThenEndDef(int seconds) => new()
    {
        SchemaVersion = 1,
        Nodes = new List<NodeDefinition>
        {
            new() { Id = "start", TypeKey = "trigger.manual", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "sleep", TypeKey = "flow.sleep", Config = WorkflowsTestSeed.Json($$"""{"seconds":{{seconds}}}"""), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "end", TypeKey = "builtin.terminal", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
        },
        Edges = new List<EdgeDefinition>
        {
            new() { From = "start", To = "sleep" },
            new() { From = "sleep", To = "end" },
        },
    };

    // start → a → end (all json_emit / terminal — no side effects).
    private static WorkflowDefinition LinearEchoDef() => new()
    {
        SchemaVersion = 1,
        Nodes = new List<NodeDefinition>
        {
            new() { Id = "start", TypeKey = "trigger.manual", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "a", TypeKey = JsonEmitNode.Key, Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.Json("""{"tag":"a"}""") },
            new() { Id = "end", TypeKey = "builtin.terminal", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
        },
        Edges = new List<EdgeDefinition>
        {
            new() { From = "start", To = "a" },
            new() { From = "a", To = "end" },
        },
    };

    // start → a → {b, c} → d(join) → end.
    private static WorkflowDefinition DiamondDef() => new()
    {
        SchemaVersion = 1,
        Nodes = new List<NodeDefinition>
        {
            new() { Id = "start", TypeKey = "trigger.manual", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "a", TypeKey = JsonEmitNode.Key, Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.Json("""{"tag":"a"}""") },
            new() { Id = "b", TypeKey = JsonEmitNode.Key, Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.Json("""{"tag":"b"}""") },
            new() { Id = "c", TypeKey = JsonEmitNode.Key, Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.Json("""{"tag":"c"}""") },
            new() { Id = "d", TypeKey = JsonEmitNode.Key, Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.Json("""{"tag":"d"}""") },
            new() { Id = "end", TypeKey = "builtin.terminal", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
        },
        Edges = new List<EdgeDefinition>
        {
            new() { From = "start", To = "a" },
            new() { From = "a", To = "b" },
            new() { From = "a", To = "c" },
            new() { From = "b", To = "d" },
            new() { From = "c", To = "d" },
            new() { From = "d", To = "end" },
        },
    };

    // start → if(true) → {t (true handle), f (false handle)} → j(join) → end.
    private static WorkflowDefinition BranchJoinDef() => new()
    {
        SchemaVersion = 1,
        Nodes = new List<NodeDefinition>
        {
            new() { Id = "start", TypeKey = "trigger.manual", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "iff", TypeKey = "logic.if", Config = WorkflowsTestSeed.Json("""{"condition":"{{trigger.branch}} == \"yes\""}"""), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "t", TypeKey = JsonEmitNode.Key, Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.Json("""{"tag":"t"}""") },
            new() { Id = "f", TypeKey = JsonEmitNode.Key, Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.Json("""{"tag":"f"}""") },
            new() { Id = "j", TypeKey = JsonEmitNode.Key, Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.Json("""{"tag":"j"}""") },
            new() { Id = "end", TypeKey = "builtin.terminal", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
        },
        Edges = new List<EdgeDefinition>
        {
            new() { From = "start", To = "iff" },
            new() { From = "iff", To = "t", SourceHandle = "true" },
            new() { From = "iff", To = "f", SourceHandle = "false" },
            new() { From = "t", To = "j" },
            new() { From = "f", To = "j" },
            new() { From = "j", To = "end" },
        },
    };

    // start → flaky(always fails) =(error)=> caught → end ; flaky =(normal)=> ok.
    private static WorkflowDefinition FailWithErrorEdgeDef(string flakyKey) => new()
    {
        SchemaVersion = 1,
        Nodes = new List<NodeDefinition>
        {
            new() { Id = "start", TypeKey = "trigger.manual", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "flaky", TypeKey = FlakyTestNode.Key, Config = WorkflowsTestSeed.Json($$"""{"key":"{{flakyKey}}","failTimes":99}"""), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "ok", TypeKey = JsonEmitNode.Key, Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.Json("""{"tag":"ok"}""") },
            // caught CONSUMES the failed upstream's rebuilt error output — the path the rerun must reconstruct.
            new() { Id = "caught", TypeKey = JsonEmitNode.Key, Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.Json("""{"msg":"{{nodes.flaky.outputs.error.message}}"}""") },
            new() { Id = "end", TypeKey = "builtin.terminal", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
        },
        Edges = new List<EdgeDefinition>
        {
            new() { From = "start", To = "flaky" },
            new() { From = "flaky", To = "ok" },
            new() { From = "flaky", To = "caught", SourceHandle = WorkflowHandles.Error },
            new() { From = "caught", To = "end" },
        },
    };

    // start → flaky(always fails) → end. No error edge — the failure fails the run.
    private static WorkflowDefinition FailNoErrorEdgeDef(string flakyKey) => new()
    {
        SchemaVersion = 1,
        Nodes = new List<NodeDefinition>
        {
            new() { Id = "start", TypeKey = "trigger.manual", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "flaky", TypeKey = FlakyTestNode.Key, Config = WorkflowsTestSeed.Json($$"""{"key":"{{flakyKey}}","failTimes":99}"""), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "end", TypeKey = "builtin.terminal", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
        },
        Edges = new List<EdgeDefinition>
        {
            new() { From = "start", To = "flaky" },
            new() { From = "flaky", To = "end" },
        },
    };

    // start → flaky(fails its 1st attempt, succeeds its 2nd; NO error edge) → end. The original halts at flaky (Failure);
    // an in-place continue re-runs flaky (attempt 2 → Success) and the run completes. (C2.)
    private static WorkflowDefinition FailOnceNoErrorEdgeDef(string flakyKey) => new()
    {
        SchemaVersion = 1,
        Nodes = new List<NodeDefinition>
        {
            new() { Id = "start", TypeKey = "trigger.manual", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "flaky", TypeKey = FlakyTestNode.Key, Config = WorkflowsTestSeed.Json($$"""{"key":"{{flakyKey}}","failTimes":1}"""), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "end", TypeKey = "builtin.terminal", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
        },
        Edges = new List<EdgeDefinition>
        {
            new() { From = "start", To = "flaky" },
            new() { From = "flaky", To = "end" },
        },
    };

    // start → {flakyA, flakyB}(both fail once, NO error edge) → end. Both branches halt the run in one wave → an in-place
    // continue resets BOTH and re-runs them to Success. (C2 multi-failure.)
    private static WorkflowDefinition FailOnceDiamondDef(string keyA, string keyB) => new()
    {
        SchemaVersion = 1,
        Nodes = new List<NodeDefinition>
        {
            new() { Id = "start", TypeKey = "trigger.manual", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "flakyA", TypeKey = FlakyTestNode.Key, Config = WorkflowsTestSeed.Json($$"""{"key":"{{keyA}}","failTimes":1}"""), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "flakyB", TypeKey = FlakyTestNode.Key, Config = WorkflowsTestSeed.Json($$"""{"key":"{{keyB}}","failTimes":1}"""), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "end", TypeKey = "builtin.terminal", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
        },
        Edges = new List<EdgeDefinition>
        {
            new() { From = "start", To = "flakyA" },
            new() { From = "start", To = "flakyB" },
            new() { From = "flakyA", To = "end" },
            new() { From = "flakyB", To = "end" },
        },
    };

    // start → mutator(side-effecting, succeeds) → flaky(fails once) → end. Proves an in-place continue re-runs ONLY the
    // failed node — the succeeded side-effecting upstream is settled + reused, never re-fired. (C2 safety.)
    private static WorkflowDefinition SideEffectThenFlakyDef(string probeKey, string flakyKey) => new()
    {
        SchemaVersion = 1,
        Nodes = new List<NodeDefinition>
        {
            new() { Id = "start", TypeKey = "trigger.manual", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "mutator", TypeKey = MutatingProbeNode.Key, Config = WorkflowsTestSeed.Json($$"""{"key":"{{probeKey}}"}"""), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "flaky", TypeKey = FlakyTestNode.Key, Config = WorkflowsTestSeed.Json($$"""{"key":"{{flakyKey}}","failTimes":1}"""), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "end", TypeKey = "builtin.terminal", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
        },
        Edges = new List<EdgeDefinition>
        {
            new() { From = "start", To = "mutator" },
            new() { From = "mutator", To = "flaky" },
            new() { From = "flaky", To = "end" },
        },
    };

    // start → loop(1 pass; body: loop_start → mutator) → transform(json_emit) → end.
    private static WorkflowDefinition LoopBodyDef(string bodyKey) => new()
    {
        SchemaVersion = 1,
        Nodes = new List<NodeDefinition>
        {
            new() { Id = "start", TypeKey = "trigger.manual", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "loop", TypeKey = "flow.loop", Config = WorkflowsTestSeed.Json("""{"maxIterations":1}"""), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "ls", TypeKey = "flow.loop_start", ParentId = "loop", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "body", TypeKey = MutatingProbeNode.Key, ParentId = "loop", Config = WorkflowsTestSeed.Json($$"""{"key":"{{bodyKey}}"}"""), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "transform", TypeKey = JsonEmitNode.Key, Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.Json("""{"tag":"transform"}""") },
            new() { Id = "end", TypeKey = "builtin.terminal", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
        },
        Edges = new List<EdgeDefinition>
        {
            new() { From = "start", To = "loop" },
            new() { From = "loop", To = "transform" },
            new() { From = "ls", To = "body" },
            new() { From = "transform", To = "end" },
        },
    };

    // start → a(json_emit echoes {{team.VAR}} as output "v") → b → end.
    private static WorkflowDefinition EchoTeamVarDef(string varName) => new()
    {
        SchemaVersion = 1,
        Nodes = new List<NodeDefinition>
        {
            new() { Id = "start", TypeKey = "trigger.manual", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "a", TypeKey = JsonEmitNode.Key, Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.Json($$"""{"v":"{{"{{"}}team.{{varName}}{{"}}"}}"}""") },
            new() { Id = "b", TypeKey = JsonEmitNode.Key, Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.Json("""{"v":"{{nodes.a.outputs.v}}"}""") },
            new() { Id = "end", TypeKey = "builtin.terminal", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
        },
        Edges = new List<EdgeDefinition>
        {
            new() { From = "start", To = "a" },
            new() { From = "a", To = "b" },
            new() { From = "b", To = "end" },
        },
    };

    // start → suspendprobe(parks an Action wait) → downstream → end.
    private static WorkflowDefinition SuspendThenDownstreamDef(string probeKey) => new()
    {
        SchemaVersion = 1,
        Nodes = new List<NodeDefinition>
        {
            new() { Id = "start", TypeKey = "trigger.manual", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "suspendprobe", TypeKey = SuspendProbeNode.Key, Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.Json($$"""{"key":"{{probeKey}}","item":"x"}""") },
            new() { Id = "downstream", TypeKey = JsonEmitNode.Key, Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.Json("""{"tag":"downstream"}""") },
            new() { Id = "end", TypeKey = "builtin.terminal", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
        },
        Edges = new List<EdgeDefinition>
        {
            new() { From = "start", To = "suspendprobe" },
            new() { From = "suspendprobe", To = "downstream" },
            new() { From = "downstream", To = "end" },
        },
    };

    // start → suspendprobe(CanSuspend) → end.
    private static WorkflowDefinition SuspendInlineDef(string probeKey) => new()
    {
        SchemaVersion = 1,
        Nodes = new List<NodeDefinition>
        {
            new() { Id = "start", TypeKey = "trigger.manual", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "suspendprobe", TypeKey = SuspendProbeNode.Key, Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.Json($$"""{"key":"{{probeKey}}","item":"x"}""") },
            new() { Id = "end", TypeKey = "builtin.terminal", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
        },
        Edges = new List<EdgeDefinition>
        {
            new() { From = "start", To = "suspendprobe" },
            new() { From = "suspendprobe", To = "end" },
        },
    };

    // start → agent.run(a) [admitted from-node root, P2.2] → suspendprobe(b) [unsupported] → end.
    private static WorkflowDefinition AgentThenSuspendProbeDef(string probeKey) => new()
    {
        SchemaVersion = 1,
        Nodes = new List<NodeDefinition>
        {
            new() { Id = "start", TypeKey = "trigger.manual", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "a", TypeKey = "agent.run",
                    Config = WorkflowsTestSeed.Json("""{ "goal": "Work on alpha", "harness": "codex-cli" }"""), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "b", TypeKey = SuspendProbeNode.Key, Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.Json($$"""{"key":"{{probeKey}}","item":"x"}""") },
            new() { Id = "end", TypeKey = "builtin.terminal", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
        },
        Edges = new List<EdgeDefinition>
        {
            new() { From = "start", To = "a" },
            new() { From = "a", To = "b" },
            new() { From = "b", To = "end" },
        },
    };

    // start → loop(1 pass; body: loop_start → bodyjson — PURE, no side effects) → end. The loop is still
    // refused in a rerun closure by KIND (slice-1 conservatism), independent of its pure body.
    private static WorkflowDefinition PureBodiedLoopDef() => new()
    {
        SchemaVersion = 1,
        Nodes = new List<NodeDefinition>
        {
            new() { Id = "start", TypeKey = "trigger.manual", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "loop", TypeKey = "flow.loop", Config = WorkflowsTestSeed.Json("""{"maxIterations":1}"""), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "ls", TypeKey = "flow.loop_start", ParentId = "loop", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "bodyjson", TypeKey = JsonEmitNode.Key, ParentId = "loop", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.Json("""{"tag":"body"}""") },
            new() { Id = "end", TypeKey = "builtin.terminal", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
        },
        Edges = new List<EdgeDefinition>
        {
            new() { From = "start", To = "loop" },
            new() { From = "loop", To = "end" },
            new() { From = "ls", To = "bodyjson" },
        },
    };

    // start → fork(pure) → {a (side-effecting), b (side-effecting)} → join(pure) → end.
    private static WorkflowDefinition TwoSideEffectDiamondDef(string keyA, string keyB) => new()
    {
        SchemaVersion = 1,
        Nodes = new List<NodeDefinition>
        {
            new() { Id = "start", TypeKey = "trigger.manual", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "fork", TypeKey = JsonEmitNode.Key, Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.Json("""{"tag":"fork"}""") },
            new() { Id = "a", TypeKey = MutatingProbeNode.Key, Config = WorkflowsTestSeed.Json($$"""{"key":"{{keyA}}"}"""), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "b", TypeKey = MutatingProbeNode.Key, Config = WorkflowsTestSeed.Json($$"""{"key":"{{keyB}}"}"""), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "join", TypeKey = JsonEmitNode.Key, Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.Json("""{"tag":"join"}""") },
            new() { Id = "end", TypeKey = "builtin.terminal", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
        },
        Edges = new List<EdgeDefinition>
        {
            new() { From = "start", To = "fork" },
            new() { From = "fork", To = "a" },
            new() { From = "fork", To = "b" },
            new() { From = "a", To = "join" },
            new() { From = "b", To = "join" },
            new() { From = "join", To = "end" },
        },
    };

    // start → mutator(side-effecting) =(error)=> caught ; mutator → end.
    private static WorkflowDefinition SideEffectWithErrorEdgeDef(string probeKey) => new()
    {
        SchemaVersion = 1,
        Nodes = new List<NodeDefinition>
        {
            new() { Id = "start", TypeKey = "trigger.manual", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "mutator", TypeKey = MutatingProbeNode.Key, Config = WorkflowsTestSeed.Json($$"""{"key":"{{probeKey}}"}"""), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "caught", TypeKey = JsonEmitNode.Key, Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.Json("""{"tag":"caught"}""") },
            new() { Id = "end", TypeKey = "builtin.terminal", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
        },
        Edges = new List<EdgeDefinition>
        {
            new() { From = "start", To = "mutator" },
            new() { From = "mutator", To = "end" },
            new() { From = "mutator", To = "caught", SourceHandle = WorkflowHandles.Error },
        },
    };

    // start → bothflags(IsSideEffecting AND CanSuspend) → end.
    private static WorkflowDefinition BothFlagsDef() => new()
    {
        SchemaVersion = 1,
        Nodes = new List<NodeDefinition>
        {
            new() { Id = "start", TypeKey = "trigger.manual", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "bothflags", TypeKey = BothFlagsProbeNode.Key, Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "end", TypeKey = "builtin.terminal", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
        },
        Edges = new List<EdgeDefinition>
        {
            new() { From = "start", To = "bothflags" },
            new() { From = "bothflags", To = "end" },
        },
    };

    // ─────────────────────────────────────────────────────────────────────────────
    //  Run-staging + query helpers
    // ─────────────────────────────────────────────────────────────────────────────

    private async Task<Guid> CreateWorkflowAsync(Guid teamId, Guid userId, WorkflowDefinition def)
    {
        using var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin);
        return await scope.Resolve<IMediator>().Send(new CodeSpace.Messages.Commands.Workflows.CreateWorkflowCommand
        {
            Name = "rerun-" + Guid.NewGuid().ToString("N")[..8],
            Description = null,
            Definition = def,
            Activations = new List<WorkflowActivationInput>(),
            Enabled = true,
        });
    }

    /// <summary>Seed + walk a fresh authored run to terminal.</summary>
    private async Task<Guid> RunFreshAsync(Guid workflowId, Guid teamId, string payloadJson = "{}")
    {
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId, payloadJson: payloadJson);
        await RunEngineAsync(runId);
        return runId;
    }

    /// <summary>Stage a snapshot-origin run (no Workflow row) via the real starter, returning its id (Pending → walk it).</summary>
    private async Task<Guid> StartSnapshotAsync(Guid teamId, Guid userId, WorkflowDefinition def)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<IRunFromSnapshotStarter>().StartFromSnapshotAsync(def, teamId, userId, "{}", scopeRepositoryIds: null, projectionKind: null, session: null, CancellationToken.None);
    }

    /// <summary>Drive the REAL service seam, then return the new run id (dispatch fires inline post-commit, leaving the fork Enqueued).</summary>
    private async Task<Guid> RerunAsync(Guid originalRunId, string fromNodeId, Guid teamId, Guid userId)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<IWorkflowService>().RerunFromNodeAsync(originalRunId, fromNodeId, teamId, userId, CancellationToken.None);
    }

    /// <summary>Drive the REAL in-place continue seam (same run id, no fork) — returns whether the run was revived.</summary>
    private async Task<bool> ContinueAsync(Guid runId, Guid teamId)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<IWorkflowService>().ContinueRunAsync(runId, teamId, CancellationToken.None);
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

    /// <summary>Flip a Suspended run back to Enqueued — simulates a spurious reconciler / duplicate-worker re-dispatch so a re-walk can be exercised.</summary>
    private async Task ForceEnqueuedAsync(Guid runId)
    {
        using var scope = _fixture.BeginScope();
        await scope.Resolve<CodeSpaceDbContext>().Database
            .ExecuteSqlInterpolatedAsync($"UPDATE workflow_run SET status = 'Enqueued' WHERE id = {runId}");
    }

    private async Task SetTeamVarAsync(Guid teamId, Guid userId, string name, string value)
    {
        using var scope = _fixture.BeginScope();
        await scope.Resolve<IVariableService>().SetAsync(
            VariableScope.Team, teamId, teamId, name, VariableValueType.String,
            JsonDocument.Parse(JsonSerializer.Serialize(value)).RootElement.Clone(), null, userId, CancellationToken.None);
    }

    private async Task AssertRunStatusAsync(Guid runId, WorkflowRunStatus expected, string? because = null)
    {
        using var scope = _fixture.BeginScope();
        var run = await scope.Resolve<CodeSpaceDbContext>().WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId);
        run.Status.ShouldBe(expected, $"{because} (run {runId} status; error={run.Error})");
    }

    /// <summary>Resolve a run's single pending wait via the REAL <see cref="IWorkflowResumeService"/> — the path a scheduled Timer job invokes when the delay elapses.</summary>
    private async Task<bool> ResolvePendingWaitAsync(Guid runId)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var waitId = (await db.WorkflowRunWait.AsNoTracking().SingleAsync(w => w.RunId == runId && w.Status == WorkflowWaitStatuses.Pending)).Id;
        return await scope.Resolve<IWorkflowResumeService>().ResumeWaitAsync(runId, waitId, null, CancellationToken.None);
    }

    /// <summary>Resolve the rerun gate's pending Approval wait through the REAL ResumeRunCommand chain (→ ApproveRunAsync), as an operator would.</summary>
    private async Task<bool> ApproveRerunGateAsync(Guid runId, Guid teamId, Guid userId, bool approved)
    {
        using var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin);
        return await scope.Resolve<IMediator>().Send(new ResumeRunCommand { RunId = runId, Approved = approved, Comment = approved ? "go" : "skip" });
    }

    private async Task<int> PendingApprovalWaitCountAsync(Guid runId)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<CodeSpaceDbContext>().WorkflowRunWait.AsNoTracking()
            .CountAsync(w => w.RunId == runId && w.Status == WorkflowWaitStatuses.Pending && w.WaitKind == WorkflowWaitKinds.Approval);
    }

    private async Task<WorkflowRunNode> LoadCellAsync(Guid runId, string nodeId)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<CodeSpaceDbContext>().WorkflowRunNode.AsNoTracking()
            .SingleAsync(n => n.RunId == runId && n.NodeId == nodeId && n.IterationKey == "");
    }

    private async Task<int> NodeStartedCountAsync(Guid runId, string nodeId)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<CodeSpaceDbContext>().WorkflowRunRecord.AsNoTracking()
            .CountAsync(r => r.RunId == runId && r.NodeId == nodeId && r.IterationKey == "" && r.RecordType == WorkflowRunRecordTypes.NodeStarted);
    }

    private static readonly string[] TerminalRecordTypes =
        { WorkflowRunRecordTypes.NodeCompleted, WorkflowRunRecordTypes.NodeSkipped, WorkflowRunRecordTypes.NodeFailed };

    private async Task<int> TerminalRecordCountAsync(Guid runId, string nodeId)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<CodeSpaceDbContext>().WorkflowRunRecord.AsNoTracking()
            .CountAsync(r => r.RunId == runId && r.NodeId == nodeId && r.IterationKey == "" && TerminalRecordTypes.Contains(r.RecordType));
    }

    private async Task<int> RecordCountAsync(Guid runId)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<CodeSpaceDbContext>().WorkflowRunRecord.AsNoTracking().CountAsync(r => r.RunId == runId);
    }

    private async Task<int> CellCountAsync(Guid runId)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<CodeSpaceDbContext>().WorkflowRunNode.AsNoTracking().CountAsync(n => n.RunId == runId);
    }

    private async Task<int> RunCountAsync(Guid teamId)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<CodeSpaceDbContext>().WorkflowRun.AsNoTracking().CountAsync(r => r.TeamId == teamId);
    }
}
